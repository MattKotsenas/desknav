using System.Collections.Immutable;
using System.Runtime.ExceptionServices;

using Akka.Actor;
using Akka.Event;

namespace Desknav.ControlPlane;

/// <summary>
/// Owns scheduling and cancellation of target enumeration for the UI
/// Automation boundary. Reports successful and expected-failure outcomes
/// to its parent.
/// </summary>
public sealed class TargetDiscoveryActor : ReceiveActor
{
    internal const int MaximumConcurrentOperations = 2;

    private readonly IActorRef _coordinator;
    private readonly ITargetDiscovery _discovery;
    private readonly TimeSpan _operationTimeout;
    private readonly TaskCompletionSource<bool> _shutdown;
    private readonly Action<Exception> _reportFailure;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly Dictionary<
        TargetDiscoveryRequestId,
        DiscoveryOperation> _operations = [];
    private readonly HashSet<Task> _releases = [];
    private TargetDiscoveryRequestId? _pendingRequestId;
    private bool _isTerminating;
    private bool _isUnavailable;

    public TargetDiscoveryActor(
        ITargetDiscovery discovery,
        TimeSpan operationTimeout,
        TaskCompletionSource<bool> shutdown,
        Action<Exception> reportFailure)
    {
        _coordinator = Context.Parent;
        _discovery = discovery;
        _operationTimeout = operationTimeout;
        _shutdown = shutdown;
        _reportFailure = reportFailure;

        Receive<DiscoverTargets>(Handle);
        Receive<CancelTargetDiscovery>(Handle);
        Receive<DiscoveryFinished>(Handle);
        Receive<DiscoveryFaulted>(Handle);
        Receive<DiscoveryReleased>(Handle);
        Receive<CancellationFailed>(Handle);
        Receive<OperationTimedOut>(Handle);
        Receive<CancellationTimedOut>(Handle);
    }

    public static Props CreateProps(
        ITargetDiscovery discovery,
        TimeSpan operationTimeout,
        Action<Exception> reportFailure,
        out Task shutdown)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(reportFailure);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            operationTimeout,
            TimeSpan.Zero);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        shutdown = completion.Task;
        return Props.Create(
            () => new TargetDiscoveryActor(
                discovery,
                operationTimeout,
                completion,
                reportFailure));
    }

    protected override void PostStop()
    {
        CompleteShutdown();

        base.PostStop();
    }

    private void Handle(DiscoverTargets discover)
    {
        if (_isTerminating)
        {
            return;
        }

        if (_isUnavailable)
        {
            _coordinator.Tell(
                new TargetDiscoveryCompleted(
                    discover.RequestId,
                    new TargetDiscoveryResult.Failed()));
            return;
        }

        if (_operations.ContainsKey(discover.RequestId)
            || _pendingRequestId == discover.RequestId)
        {
            StopApplication(
                new InvalidOperationException(
                    $"Discovery request {discover.RequestId}"
                    + " is already active."),
                "Duplicate target discovery request {0}.",
                discover.RequestId);
            return;
        }

        if (_operations.Count >= MaximumConcurrentOperations)
        {
            if (_pendingRequestId is { } supersededRequestId)
            {
                _coordinator.Tell(
                    new TargetDiscoveryCompleted(
                        supersededRequestId,
                        new TargetDiscoveryResult.Failed()));
            }

            _pendingRequestId = discover.RequestId;
            return;
        }

        StartDiscovery(discover.RequestId);
    }

    private void StartDiscovery(TargetDiscoveryRequestId requestId)
    {
        var cancellation = new CancellationTokenSource();
        var timeout = Context.System.Scheduler.ScheduleTellOnceCancelable(
            _operationTimeout,
            Self,
            new OperationTimedOut(requestId),
            Self);
        var execution = Task.Run(
            () => RunDiscoveryAsync(
                requestId,
                cancellation.Token));
        var operation =
            new DiscoveryOperation(cancellation, timeout, execution);
        _operations.Add(requestId, operation);
        execution.PipeTo(
            Self,
            Self,
            failure: exception =>
                new DiscoveryFaulted(requestId, exception));
    }

    private void Handle(CancelTargetDiscovery cancel)
    {
        if (_pendingRequestId == cancel.RequestId)
        {
            _pendingRequestId = null;
            return;
        }

        if (_operations.TryGetValue(cancel.RequestId, out var operation))
        {
            if (operation.TryRequestCancellation(out var cancellation))
            {
                operation.ReplaceTimeout(
                    Context.System.Scheduler.ScheduleTellOnceCancelable(
                        _operationTimeout,
                        Self,
                        new CancellationTimedOut(cancel.RequestId),
                        Self));
                _ = ObserveCancellationAsync(
                    cancel.RequestId,
                    cancellation,
                    Self);
            }
        }
    }

    private void Handle(DiscoveryFinished finished)
    {
        if (!_operations.Remove(
                finished.RequestId,
                out var operation))
        {
            return;
        }

        ReleaseOperation(finished.RequestId, operation);
        if (_isTerminating || _isUnavailable)
        {
            return;
        }

        if (!finished.WasCancellationRequested)
        {
            _coordinator.Tell(
                new TargetDiscoveryCompleted(
                    finished.RequestId,
                    finished.Result));
        }

        StartPendingDiscovery();
    }

    private void Handle(DiscoveryFaulted faulted)
    {
        DiscoveryOperation? operation = null;
        if (_operations.Remove(faulted.RequestId, out operation))
        {
            ReleaseOperation(faulted.RequestId, operation);
        }

        if (faulted.Release is { } release)
        {
            _releases.Remove(release);
        }

        if (operation is not null
            && operation.CancellationRequested
            && !operation.UnsuccessfulCompletionPrecededCancellation)
        {
            _log.Debug(
                faulted.Cause,
                "Canceled target discovery request {0} faulted while"
                + " unwinding.",
                faulted.RequestId);
            StartPendingDiscovery();
            return;
        }

        StopApplication(
            faulted.Cause,
            "Target discovery request {0} faulted unexpectedly.",
            faulted.RequestId);
    }

    private void Handle(CancellationFailed failed) =>
        StopApplication(
            failed.Cause,
            "Cancellation of target discovery request {0} failed.",
            failed.RequestId);

    private void Handle(OperationTimedOut timedOut)
    {
        if (_isTerminating
            || _isUnavailable
            || !_operations.TryGetValue(
                timedOut.RequestId,
                out var operation)
            || operation.CancellationRequested)
        {
            return;
        }

        BecomeUnavailable(
            timedOut.RequestId,
            "Target discovery request {0} exceeded its operation timeout.");
    }

    private void Handle(CancellationTimedOut timedOut)
    {
        if (_isTerminating
            || _isUnavailable
            || !_operations.TryGetValue(
                timedOut.RequestId,
                out var operation)
            || !operation.CancellationRequested)
        {
            return;
        }

        operation.CancellationExpired = true;
        if (_operations.Count >= MaximumConcurrentOperations
            && _operations.Values.All(
                current => current.CancellationExpired))
        {
            BecomeUnavailable(
                timedOut.RequestId,
                "Every target discovery operation failed to stop within"
                + " its cancellation budget; request {0} exhausted the"
                + " remaining budget.");
        }
    }

    private void BecomeUnavailable(
        TargetDiscoveryRequestId timedOutRequestId,
        string warning)
    {
        _isUnavailable = true;
        _log.Warning(warning, timedOutRequestId);

        foreach (var (requestId, operation) in _operations)
        {
            operation.CancelTimeout();
            if (operation.TryRequestCancellation(out var cancellation))
            {
                _ = ObserveCancellationAsync(
                    requestId,
                    cancellation,
                    Self);
            }
            _coordinator.Tell(
                new TargetDiscoveryCompleted(
                    requestId,
                    new TargetDiscoveryResult.Failed()));
        }

        if (_pendingRequestId is { } pendingRequestId)
        {
            _coordinator.Tell(
                new TargetDiscoveryCompleted(
                    pendingRequestId,
                    new TargetDiscoveryResult.Failed()));
            _pendingRequestId = null;
        }
    }

    private void StopApplication(
        Exception cause,
        string message,
        TargetDiscoveryRequestId requestId)
    {
        _reportFailure(cause);
        if (_isTerminating)
        {
            return;
        }
        _isTerminating = true;
        _log.Error(cause, message, requestId);
    }

    private Task CleanupAsync()
    {
        var cleanups = new List<Task>(
            _operations.Count + _releases.Count);
        foreach (var operation in _operations.Values)
        {
            cleanups.Add(CancelDuringShutdownAsync(operation));
        }
        _operations.Clear();
        cleanups.AddRange(_releases);
        _releases.Clear();
        return AwaitCleanupAsync(cleanups);
    }

    private void CompleteShutdown()
    {
        try
        {
            _ = CompleteShutdownAsync(CleanupAsync());
        }
        catch (Exception exception)
        {
            _shutdown.TrySetException(exception);
        }
    }

    private async Task CompleteShutdownAsync(Task cleanup)
    {
        await cleanup.ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing);
        if (cleanup.IsFaulted)
        {
            _shutdown.TrySetException(
                cleanup.Exception!.Flatten().InnerExceptions);
        }
        else if (cleanup.IsCanceled)
        {
            _shutdown.TrySetCanceled();
        }
        else
        {
            _shutdown.TrySetResult(true);
        }
    }

    private void StartPendingDiscovery()
    {
        if (_pendingRequestId is not { } requestId)
        {
            return;
        }

        _pendingRequestId = null;
        StartDiscovery(requestId);
    }

    private void ReleaseOperation(
        TargetDiscoveryRequestId requestId,
        DiscoveryOperation operation)
    {
        var release = operation.DisposeAsync().AsTask();
        _releases.Add(release);
        release.PipeTo(
            Self,
            Self,
            () => new DiscoveryReleased(release),
            exception => new DiscoveryFaulted(
                requestId,
                exception,
                release));
    }

    private void Handle(DiscoveryReleased released) =>
        _releases.Remove(released.Release);

    private static async Task ObserveCancellationAsync(
        TargetDiscoveryRequestId requestId,
        Task cancellation,
        IActorRef owner)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            owner.Tell(
                new CancellationFailed(requestId, exception),
                owner);
        }
    }

    private async Task CancelDuringShutdownAsync(
        DiscoveryOperation operation)
    {
        var failures = new List<Exception>();
        try
        {
            operation.TryRequestCancellation(out _);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (!operation.Execution.IsCompletedSuccessfully
            && operation.UnsuccessfulCompletionPrecededCancellation)
        {
            if (operation.Execution.Exception is { } exception)
            {
                failures.Add(exception);
            }
            else
            {
                failures.Add(
                    new TaskCanceledException(operation.Execution));
            }
        }

        try
        {
            await operation.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Target discovery cleanup failed.",
                failures);
        }
    }

    private static async Task AwaitCleanupAsync(
        IReadOnlyCollection<Task> cleanups)
    {
        await Task.WhenAll(cleanups).ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing);
        var failures = new List<Exception>();
        foreach (var cleanup in cleanups)
        {
            if (cleanup.Exception is { } exception)
            {
                failures.AddRange(exception.InnerExceptions);
            }
            else if (cleanup.IsCanceled)
            {
                failures.Add(new TaskCanceledException(cleanup));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Target discovery cleanup failed.",
                failures);
        }
    }

    private async Task<DiscoveryFinished> RunDiscoveryAsync(
        TargetDiscoveryRequestId requestId,
        CancellationToken cancellationToken)
    {
        var result = await _discovery
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        return new DiscoveryFinished(
            requestId,
            cancellationToken.IsCancellationRequested,
            result);
    }

    /// <summary>
    /// Records cancellation state when an operation exits so canceled work
    /// remains silent.
    /// </summary>
    // Internal so tests can establish mailbox order without timing.
    internal sealed record DiscoveryFinished(
        TargetDiscoveryRequestId RequestId,
        bool WasCancellationRequested,
        TargetDiscoveryResult Result);

    /// <summary>
    /// Preserves request correlation when an operation fails unexpectedly.
    /// </summary>
    private sealed record DiscoveryFaulted(
        TargetDiscoveryRequestId RequestId,
        Exception Cause,
        Task? Release = null);

    private sealed record DiscoveryReleased(Task Release);

    /// <summary>
    /// Reports cancellation callback failure back to the actor thread.
    /// </summary>
    private sealed record CancellationFailed(
        TargetDiscoveryRequestId RequestId,
        Exception Cause);

    /// <summary>
    /// Identifies a running operation whose execution budget expired.
    /// </summary>
    internal sealed record OperationTimedOut(
        TargetDiscoveryRequestId RequestId);

    /// <summary>
    /// Identifies a canceled operation that did not stop within its unwind
    /// budget.
    /// </summary>
    internal sealed record CancellationTimedOut(
        TargetDiscoveryRequestId RequestId);

    /// <summary>
    /// Owns the execution, cancellation, and scheduled timeout resources for
    /// one running discovery.
    /// </summary>
    // Internal so resource lifetime can be tested without actor timing.
    internal sealed class DiscoveryOperation : IAsyncDisposable
    {
        private const int CancellationFirst = 1;
        private const int FailureFirst = 2;
        private readonly CancellationTokenSource _cancellationSource;
        private ICancelable _timeout;
        private Task _cancellation = Task.CompletedTask;
        private int _terminalOrder;
        private bool _isDisposed;

        public DiscoveryOperation(
            CancellationTokenSource cancellation,
            ICancelable timeout,
            Task execution)
        {
            _cancellationSource = cancellation;
            _timeout = timeout;
            Execution = execution;
            _ = execution.ContinueWith(
                static (completed, state) =>
                {
                    if (!completed.IsCompletedSuccessfully)
                    {
                        Interlocked.CompareExchange(
                            ref ((DiscoveryOperation)state!)._terminalOrder,
                            FailureFirst,
                            comparand: 0);
                    }
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public bool CancellationRequested { get; private set; }

        public bool UnsuccessfulCompletionPrecededCancellation =>
            Volatile.Read(ref _terminalOrder) == FailureFirst;

        public Task Cancellation => _cancellation;

        public Task Execution { get; }

        public bool CancellationExpired { get; set; }

        public bool TryRequestCancellation(out Task cancellationTask)
        {
            ThrowIfDisposed();
            if (CancellationRequested)
            {
                cancellationTask = _cancellation;
                return false;
            }

            if (Execution.IsCompleted
                && !Execution.IsCompletedSuccessfully)
            {
                Interlocked.CompareExchange(
                    ref _terminalOrder,
                    FailureFirst,
                    comparand: 0);
            }
            Interlocked.CompareExchange(
                ref _terminalOrder,
                CancellationFirst,
                comparand: 0);
            CancellationRequested = true;
            _cancellation = _cancellationSource.CancelAsync();
            cancellationTask = _cancellation;
            return true;
        }

        public void ReplaceTimeout(ICancelable replacement)
        {
            ThrowIfDisposed();
            ReleaseTimeout();
            _timeout = replacement;
        }

        public void CancelTimeout()
        {
            ThrowIfDisposed();
            _timeout.Cancel();
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            ReleaseTimeout();
            _isDisposed = true;
            await Execution.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing);
            try
            {
                await _cancellation.ConfigureAwait(false);
            }
            finally
            {
                _cancellationSource.Dispose();
            }
        }

        private void DisposeTimeout()
        {
            if (_timeout is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private void ReleaseTimeout()
        {
            _timeout.Cancel();
            DisposeTimeout();
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}

/// <summary>
/// Isolates platform target enumeration from actor request management.
/// </summary>
public interface ITargetDiscovery
{
    /// <summary>
    /// Performs one target enumeration. Expected inability to enumerate
    /// returns <see cref="TargetDiscoveryResult.Failed"/>; throwing is an
    /// unexpected application failure. Cancellation is cooperative and may end
    /// by returning or throwing.
    /// </summary>
    Task<TargetDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken);
}