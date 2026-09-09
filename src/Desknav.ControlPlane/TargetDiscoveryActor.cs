using System.Collections.Immutable;

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
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly Dictionary<
        TargetDiscoveryRequestId,
        DiscoveryOperation> _operations = [];
    private readonly HashSet<Task> _releases = [];
    private TargetDiscoveryRequestId? _pendingRequestId;
    private bool _isShuttingDown;
    private bool _isTerminating;
    private bool _isUnavailable;

    public TargetDiscoveryActor(
        ITargetDiscovery discovery,
        TimeSpan operationTimeout)
    {
        _coordinator = Context.Parent;
        _discovery = discovery;
        _operationTimeout = operationTimeout;

        Receive<DiscoverTargets>(Handle);
        Receive<CancelTargetDiscovery>(Handle);
        Receive<DiscoveryFinished>(Handle);
        Receive<DiscoveryFaulted>(Handle);
        Receive<DiscoveryReleased>(Handle);
        Receive<CancellationFailed>(Handle);
        Receive<OperationTimedOut>(Handle);
        Receive<CancellationTimedOut>(Handle);
        Receive<PrepareForShutdown>(Handle);
        Receive<ShutdownCleanupCompleted>(_ => Context.Stop(Self));
        Receive<ShutdownCleanupFaulted>(Handle);
    }

    public static Props CreateProps(
        ITargetDiscovery discovery,
        TimeSpan operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            operationTimeout,
            TimeSpan.Zero);
        return Props.Create(
            () => new TargetDiscoveryActor(
                discovery,
                operationTimeout));
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

        if (_isShuttingDown)
        {
            return;
        }

        if (operation is not null
            && IsExpectedCancellation(
                faulted.Cause,
                operation.CancellationToken))
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
        _coordinator.Tell(new RuntimeFailure(cause));
        if (_isTerminating)
        {
            return;
        }
        _isTerminating = true;
        _log.Error(cause, message, requestId);
    }

    private void Handle(PrepareForShutdown _)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _isTerminating = true;
        Task cleanup;
        try
        {
            cleanup = CleanupAsync();
        }
        catch (Exception exception)
        {
            cleanup = Task.FromException(exception);
        }

        cleanup.PipeTo(
            Self,
            Self,
            () => new ShutdownCleanupCompleted(),
            exception => new ShutdownCleanupFaulted(exception));
    }

    private void Handle(ShutdownCleanupFaulted faulted)
    {
        _coordinator.Tell(new RuntimeFailure(faulted.Cause));
        _log.Error(faulted.Cause, "Target discovery cleanup failed.");
        Context.Stop(Self);
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

        try
        {
            await operation.Execution.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!IsExpectedCancellation(
                    exception,
                    operation.CancellationToken))
            {
                failures.Add(exception);
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

        if (failures.Count > 0)
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

    private sealed record ShutdownCleanupCompleted;

    private sealed record ShutdownCleanupFaulted(Exception Cause);

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
        private readonly CancellationTokenSource _cancellationSource;
        private readonly CancellationToken _cancellationToken;
        private ICancelable _timeout;
        private Task _cancellation = Task.CompletedTask;
        private bool _isDisposed;

        public DiscoveryOperation(
            CancellationTokenSource cancellation,
            ICancelable timeout,
            Task execution)
        {
            _cancellationSource = cancellation;
            _cancellationToken = cancellation.Token;
            _timeout = timeout;
            Execution = execution;
        }

        public bool CancellationRequested { get; private set; }

        public Task Cancellation => _cancellation;

        public CancellationToken CancellationToken =>
            _cancellationToken;

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

    private static bool IsExpectedCancellation(
        Exception exception,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        && exception is OperationCanceledException canceled
        && canceled.CancellationToken == cancellationToken;
}

/// <summary>
/// Isolates platform target enumeration from actor request management.
/// </summary>
public interface ITargetDiscovery
{
    /// <summary>
    /// Performs one target enumeration. Expected inability to enumerate
    /// returns <see cref="TargetDiscoveryResult.Failed"/>; throwing is an
    /// unexpected application failure. An expected cancellation throws an
    /// <see cref="OperationCanceledException"/> carrying the supplied token;
    /// the operation may also return after cancellation.
    /// </summary>
    Task<TargetDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken);
}