using Akka.Actor;
using Akka.Event;

namespace Desknav.ControlPlane;

/// <summary>
/// Owns scheduling and cancellation of target enumeration for the UI
/// Automation boundary.
/// </summary>
internal sealed class TargetDiscoveryActor : ReceiveActor
{
    internal const int MaximumConcurrentOperations = 2;

    private readonly IActorRef _coordinator;
    private readonly ITargetDiscovery _discovery;
    private readonly TimeSpan _operationTimeout;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly Dictionary<
        TargetDiscoveryRequestId,
        DiscoveryOperation> _operations = [];
    private TargetDiscoveryRequestId? _pendingRequestId;
    private bool _isTerminating;
    private bool _isUnavailable;

    public TargetDiscoveryActor(
        IActorRef coordinator,
        ITargetDiscovery discovery,
        TimeSpan operationTimeout)
    {
        _coordinator = coordinator;
        _discovery = discovery;
        _operationTimeout = operationTimeout;

        Receive<DiscoverTargets>(Handle);
        Receive<CancelTargetDiscovery>(Handle);
        Receive<DiscoveryFinished>(Handle);
        Receive<DiscoveryFaulted>(Handle);
        Receive<CancellationFailed>(Handle);
        Receive<OperationTimedOut>(Handle);
        Receive<CancellationTimedOut>(Handle);
    }

    public static Props CreateProps(
        IActorRef coordinator,
        ITargetDiscovery discovery,
        TimeSpan operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            operationTimeout,
            TimeSpan.Zero);
        return Props.Create(
            () => new TargetDiscoveryActor(
                coordinator,
                discovery,
                operationTimeout));
    }

    protected override void PostStop()
    {
        foreach (var operation in _operations.Values)
        {
            operation.Timeout.Cancel();
            _ = CancelDuringShutdownAsync(operation.Cancellation);
        }

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
                new TargetDiscoveryFailed(discover.RequestId));
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
                    new TargetDiscoveryFailed(supersededRequestId));
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
        var operation = new DiscoveryOperation(cancellation, timeout);
        _operations.Add(requestId, operation);
        Task.Run(
                () => RunDiscoveryAsync(
                    requestId,
                    operation.Cancellation))
            .PipeTo(
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
            if (operation.CancellationRequested)
            {
                return;
            }

            operation.CancellationRequested = true;
            operation.Timeout.Cancel();
            operation.Timeout =
                Context.System.Scheduler.ScheduleTellOnceCancelable(
                    _operationTimeout,
                    Self,
                    new CancellationTimedOut(cancel.RequestId),
                    Self);
            _ = RequestCancellationAsync(
                cancel.RequestId,
                operation.Cancellation,
                Self);
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

        operation.Timeout.Cancel();
        if (_isTerminating || _isUnavailable)
        {
            return;
        }

        if (!finished.WasCancellationRequested)
        {
            if (finished.Result == TargetDiscoveryResult.Succeeded)
            {
                _coordinator.Tell(
                    new TargetDiscoveryCompleted(
                        new TargetSnapshot(finished.RequestId)));
            }
            else
            {
                _coordinator.Tell(
                    new TargetDiscoveryFailed(finished.RequestId));
            }
        }

        StartPendingDiscovery();
    }

    private void Handle(DiscoveryFaulted faulted)
    {
        if (_operations.Remove(faulted.RequestId, out var operation))
        {
            operation.Timeout.Cancel();
        }

        if (_isUnavailable)
        {
            _log.Debug(
                faulted.Cause,
                "Target discovery request {0} faulted after the owner"
                + " became unavailable.",
                faulted.RequestId);
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
            operation.Timeout.Cancel();
            _ = RequestCancellationAsync(
                requestId,
                operation.Cancellation,
                Self);
            _coordinator.Tell(new TargetDiscoveryFailed(requestId));
        }

        if (_pendingRequestId is { } pendingRequestId)
        {
            _coordinator.Tell(
                new TargetDiscoveryFailed(pendingRequestId));
            _pendingRequestId = null;
        }
    }

    private void StopApplication(
        Exception cause,
        string message,
        TargetDiscoveryRequestId requestId)
    {
        if (_isTerminating)
        {
            return;
        }

        _isTerminating = true;
        _log.Error(cause, message, requestId);
        Context.System.Terminate();
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

    private static async Task RequestCancellationAsync(
        TargetDiscoveryRequestId requestId,
        CancellationTokenSource operation,
        IActorRef owner)
    {
        try
        {
            await operation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception exception)
        {
            owner.Tell(
                new CancellationFailed(requestId, exception),
                owner);
        }
    }

    private async Task CancelDuringShutdownAsync(
        CancellationTokenSource operation)
    {
        try
        {
            await operation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            _log.Debug(
                "Target discovery ended before shutdown cancellation"
                + " reached it.");
        }
        catch (Exception exception)
        {
            _log.Error(
                exception,
                "Target discovery cancellation failed during shutdown.");
        }
    }

    private async Task<DiscoveryFinished> RunDiscoveryAsync(
        TargetDiscoveryRequestId requestId,
        CancellationTokenSource operation)
    {
        try
        {
            var result = await _discovery
                .DiscoverAsync(operation.Token)
                .ConfigureAwait(false);
            return new DiscoveryFinished(
                requestId,
                operation.IsCancellationRequested,
                result);
        }
        catch (Exception exception) when (operation.IsCancellationRequested)
        {
            _log.Debug(
                exception,
                "Canceled target discovery request {0} faulted while"
                + " unwinding.",
                requestId);
            return new DiscoveryFinished(
                requestId,
                WasCancellationRequested: true,
                TargetDiscoveryResult.Failed);
        }
        finally
        {
            operation.Dispose();
        }
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
        Exception Cause);

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
    /// Keeps cancellation and its scheduled deadline together for one running
    /// operation.
    /// </summary>
    private sealed class DiscoveryOperation(
        CancellationTokenSource cancellation,
        ICancelable timeout)
    {
        public CancellationTokenSource Cancellation { get; } =
            cancellation;

        public ICancelable Timeout { get; set; } = timeout;

        public bool CancellationRequested { get; set; }

        public bool CancellationExpired { get; set; }
    }
}

/// <summary>
/// Isolates platform target enumeration from actor request management.
/// </summary>
internal interface ITargetDiscovery
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

/// <summary>
/// Distinguishes a usable observation from an expected inability to enumerate
/// without turning either into an exception.
/// </summary>
internal enum TargetDiscoveryResult
{
    Succeeded,

    Failed,
}
