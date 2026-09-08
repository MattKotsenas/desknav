using Akka.Actor;
using Akka.Event;

using Desknav.ControlPlane;

namespace Desknav.UI;

/// <summary>
/// Owns preparation, activation, and lifetime of revisioned overlay scenes.
/// </summary>
public sealed class OverlayActor : ReceiveActor
{
    private readonly IOverlayRenderer _renderer;
    private readonly TaskCompletionSource<bool> _shutdown;
    private readonly Action<Exception> _reportFailure;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly Dictionary<
        PresentationRevision,
        PreparationOperation> _preparations = [];
    private readonly HashSet<Task> _releases = [];
    private DesiredPresentation? _desired;
    private ReadyPresentation? _ready;
    private ActivationOperation? _activation;
    private ActivePresentation? _active;
    private bool _isTerminating;

    public OverlayActor(
        IOverlayRenderer renderer,
        TaskCompletionSource<bool> shutdown,
        Action<Exception> reportFailure)
    {
        _renderer = renderer;
        _shutdown = shutdown;
        _reportFailure = reportFailure;

        Receive<ApplyTargetPresentation>(Handle);
        Receive<PreparationFinished>(Handle);
        Receive<PreparationFaulted>(Handle);
        Receive<CancellationFailed>(Handle);
        Receive<ActivationFinished>(Handle);
        Receive<ActivationFaulted>(Handle);
        Receive<ReleaseCompleted>(Handle);
        Receive<ReleaseFaulted>(Handle);
    }

    public static Props CreateProps(
        IOverlayRenderer renderer,
        Action<Exception> reportFailure,
        out Task shutdown)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(reportFailure);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        shutdown = completion.Task;
        return Props.Create(
            () => new OverlayActor(
                renderer,
                completion,
                reportFailure));
    }

    protected override void PostStop()
    {
        CompleteShutdown();

        base.PostStop();
    }

    private void Handle(ApplyTargetPresentation apply)
    {
        if (_isTerminating)
        {
            return;
        }

        if (_desired is { } desired)
        {
            var comparison = apply.Revision.CompareTo(desired.Revision);
            if (comparison < 0)
            {
                _log.Debug(
                    "Ignored stale presentation revision {0};"
                    + " current desired revision is {1}.",
                    apply.Revision,
                    desired.Revision);
                return;
            }

            if (comparison == 0)
            {
                if (_active?.Revision == apply.Revision)
                {
                    Sender.Tell(
                        new TargetPresentationApplied(apply.Revision));
                }
                return;
            }
        }

        _desired = new DesiredPresentation(
            apply.Revision,
            apply.Presentation,
            Sender);

        foreach (var (revision, operation) in _preparations)
        {
            if (operation.RequestCancellation())
            {
                _ = ObserveCancellationAsync(
                    revision,
                    operation.Cancellation,
                    Self);
            }
        }

        if (_ready is { } ready)
        {
            _ready = null;
            ReleaseScene(ready.Scene);
        }

        StartPreparation(apply.Revision, apply.Presentation);
    }

    private void StartPreparation(
        PresentationRevision revision,
        TargetPresentation presentation)
    {
        var cancellation = new CancellationTokenSource();
        var execution = PrepareSceneAsync(
            presentation,
            cancellation.Token);
        var operation =
            new PreparationOperation(cancellation, execution);
        _preparations.Add(revision, operation);
        execution.PipeTo(
            Self,
            Self,
            scene => new PreparationFinished(revision, scene),
            exception => new PreparationFaulted(revision, exception));
    }

    private void Handle(PreparationFinished finished)
    {
        if (!_preparations.Remove(
                finished.Revision,
                out var operation))
        {
            ReleaseScene(finished.Scene);
            return;
        }

        ReleasePreparation(operation);
        if (_isTerminating
            || _desired?.Revision != finished.Revision)
        {
            ReleaseScene(finished.Scene);
            return;
        }

        _ready = new ReadyPresentation(
            finished.Revision,
            finished.Scene);
        TryActivate();
    }

    private void Handle(PreparationFaulted faulted)
    {
        if (!_preparations.Remove(
                faulted.Revision,
                out var operation))
        {
            return;
        }

        ReleasePreparation(operation);
        if (operation.CancellationRequested
            && !operation.UnsuccessfulCompletionPrecededCancellation)
        {
            _log.Debug(
                faulted.Cause,
                "Canceled presentation preparation {0} faulted"
                + " while unwinding.",
                faulted.Revision);
            return;
        }

        StopApplication(
            faulted.Cause,
            "Presentation preparation {0} faulted unexpectedly.",
            faulted.Revision);
    }

    private void Handle(CancellationFailed failed) =>
        StopApplication(
            failed.Cause,
            "Cancellation of presentation preparation {0} failed.",
            failed.Revision);

    private void TryActivate()
    {
        if (_activation is not null
            || _ready is not { } ready)
        {
            return;
        }

        if (_desired is not { } desired)
        {
            var cause = new InvalidOperationException(
                $"Prepared presentation {ready.Revision}"
                + " has no desired state.");
            StopApplication(
                cause,
                "Prepared presentation {0} has no desired state.",
                ready.Revision);
            return;
        }

        _ready = null;
        var execution = ActivateSceneAsync(ready.Scene);
        _activation = new ActivationOperation(
            ready.Revision,
            desired.ReplyTo,
            ready.Scene,
            execution);
        execution.PipeTo(
            Self,
            Self,
            () => new ActivationFinished(ready.Revision),
            exception => new ActivationFaulted(
                ready.Revision,
                exception));
    }

    private void Handle(ActivationFinished finished)
    {
        if (_activation is not { } activation
            || activation.Revision != finished.Revision)
        {
            var cause = new InvalidOperationException(
                $"Presentation activation {finished.Revision}"
                + " completed without a matching operation.");
            StopApplication(
                cause,
                "Presentation activation {0} completed without"
                + " a matching operation.",
                finished.Revision);
            return;
        }

        _activation = null;
        var previous = _active;
        _active = new ActivePresentation(
            activation.Revision,
            activation.Scene);
        activation.ReplyTo.Tell(
            new TargetPresentationApplied(activation.Revision));

        if (previous is not null)
        {
            ReleaseScene(previous.Scene);
        }

        TryActivate();
    }

    private void Handle(ActivationFaulted faulted)
    {
        if (_activation?.Revision != faulted.Revision)
        {
            var cause = new InvalidOperationException(
                $"Presentation activation {faulted.Revision}"
                + " faulted without a matching operation.",
                faulted.Cause);
            StopApplication(
                cause,
                "Presentation activation {0} faulted without"
                + " a matching operation.",
                faulted.Revision);
            return;
        }

        StopApplication(
            faulted.Cause,
            "Presentation activation {0} faulted unexpectedly.",
            faulted.Revision);
    }

    private void ReleasePreparation(PreparationOperation operation) =>
        TrackRelease(DisposeResourceAsync(operation));

    private void ReleaseScene(IPreparedScene scene) =>
        TrackRelease(DisposeResourceAsync(scene));

    private void TrackRelease(Task release)
    {
        _releases.Add(release);
        release.PipeTo(
            Self,
            Self,
            () => new ReleaseCompleted(release),
            exception => new ReleaseFaulted(release, exception));
    }

    private void Handle(ReleaseCompleted completed) =>
        _releases.Remove(completed.ReleaseTask);

    private void Handle(ReleaseFaulted faulted)
    {
        _releases.Remove(faulted.ReleaseTask);
        StopApplication(
            faulted.Cause,
            "Release of an overlay resource faulted unexpectedly.");
    }

    // These wrappers preserve failures as task results for PipeTo, including
    // exceptions thrown before an implementation returns a task.
    private async Task<IPreparedScene> PrepareSceneAsync(
        TargetPresentation presentation,
        CancellationToken cancellationToken) =>
        await _renderer
            .PrepareAsync(presentation, cancellationToken)
            .ConfigureAwait(false);

    private async Task ActivateSceneAsync(IPreparedScene scene) =>
        await _renderer.ActivateAsync(scene).ConfigureAwait(false);

    private static async Task DisposeResourceAsync(
        IAsyncDisposable resource) =>
        await resource.DisposeAsync().ConfigureAwait(false);

    private void StopApplication(
        Exception cause,
        string message,
        params object[] arguments)
    {
        _reportFailure(cause);
        if (_isTerminating)
        {
            return;
        }
        _isTerminating = true;
        _log.Error(cause, message, arguments);
    }

    private Task CleanupAsync()
    {
        var cleanups = new List<Task>(
            (_preparations.Count * 2) + _releases.Count + 2);
        foreach (var operation in _preparations.Values)
        {
            try
            {
                operation.RequestCancellation();
            }
            catch (Exception exception)
            {
                cleanups.Add(Task.FromException(exception));
            }
        }

        cleanups.AddRange(
            _preparations.Values.Select(
                ReleasePreparationDuringShutdownAsync));
        _preparations.Clear();

        if (_ready is { } ready)
        {
            _ready = null;
            cleanups.Add(DisposeResourceAsync(ready.Scene));
        }

        if (_activation is { } activation)
        {
            _activation = null;
            var activeScene = _active?.Scene;
            _active = null;
            cleanups.Add(
                ReleaseActivationDuringShutdownAsync(
                    activation,
                    activeScene));
        }
        else if (_active is { } active)
        {
            _active = null;
            cleanups.Add(DisposeResourceAsync(active.Scene));
        }

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

    private static async Task ObserveCancellationAsync(
        PresentationRevision revision,
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
                new CancellationFailed(revision, exception),
                owner);
        }
    }

    private async Task ReleasePreparationDuringShutdownAsync(
        PreparationOperation operation)
    {
        IPreparedScene? scene = null;
        var failures = new List<Exception>();
        await ((Task)operation.Execution).ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing);
        if (!operation.Execution.IsCompletedSuccessfully)
        {
            if (operation.UnsuccessfulCompletionPrecededCancellation)
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
            else
            {
                _log.Debug(
                    "Canceled presentation preparation ended during"
                    + " shutdown.");
            }
        }

        if (operation.Execution.Status == TaskStatus.RanToCompletion)
        {
            scene = operation.Execution.Result;
        }

        if (scene is not null)
        {
            await CaptureFailureAsync(
                    failures,
                    () => DisposeResourceAsync(scene))
                .ConfigureAwait(false);
        }

        await CaptureFailureAsync(
                failures,
                async () => await operation
                    .DisposeAsync()
                    .ConfigureAwait(false))
            .ConfigureAwait(false);

        ThrowIfCleanupFailed(failures);
    }

    private async Task ReleaseActivationDuringShutdownAsync(
        ActivationOperation operation,
        IPreparedScene? activeScene)
    {
        var failures = new List<Exception>();
        await operation.Execution.ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing);
        if (operation.Execution.Exception is { } exception)
        {
            failures.Add(exception);
        }
        else if (operation.Execution.IsCanceled)
        {
            failures.Add(
                new TaskCanceledException(operation.Execution));
        }

        await CaptureFailureAsync(
                failures,
                () => DisposeResourceAsync(operation.Scene))
            .ConfigureAwait(false);
        if (activeScene is not null)
        {
            await CaptureFailureAsync(
                    failures,
                    () => DisposeResourceAsync(activeScene))
                .ConfigureAwait(false);
        }

        ThrowIfCleanupFailed(failures);
    }

    private static async Task CaptureFailureAsync(
        List<Exception> failures,
        Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
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

        ThrowIfCleanupFailed(failures);
    }

    private static void ThrowIfCleanupFailed(List<Exception> failures)
    {
        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Overlay cleanup failed.",
                failures);
        }
    }

    private sealed record DesiredPresentation(
        PresentationRevision Revision,
        TargetPresentation Presentation,
        IActorRef ReplyTo);

    private sealed record ReadyPresentation(
        PresentationRevision Revision,
        IPreparedScene Scene);

    private sealed record ActivationOperation(
        PresentationRevision Revision,
        IActorRef ReplyTo,
        IPreparedScene Scene,
        Task Execution);

    private sealed record ActivePresentation(
        PresentationRevision Revision,
        IPreparedScene Scene);

    private sealed record PreparationFinished(
        PresentationRevision Revision,
        IPreparedScene Scene);

    private sealed record PreparationFaulted(
        PresentationRevision Revision,
        Exception Cause);

    private sealed record CancellationFailed(
        PresentationRevision Revision,
        Exception Cause);

    private sealed record ActivationFinished(
        PresentationRevision Revision);

    private sealed record ActivationFaulted(
        PresentationRevision Revision,
        Exception Cause);

    private sealed record ReleaseCompleted(Task ReleaseTask);

    private sealed record ReleaseFaulted(
        Task ReleaseTask,
        Exception Cause);

    private sealed class PreparationOperation : IAsyncDisposable
    {
        private const int CancellationFirst = 1;
        private const int FailureFirst = 2;
        private readonly CancellationTokenSource _cancellationSource;
        private Task _cancellation = Task.CompletedTask;
        private int _terminalOrder;
        private bool _isDisposed;

        public PreparationOperation(
            CancellationTokenSource cancellation,
            Task<IPreparedScene> execution)
        {
            _cancellationSource = cancellation;
            Execution = execution;
            _ = execution.ContinueWith(
                static (completed, state) =>
                {
                    if (!completed.IsCompletedSuccessfully)
                    {
                        Interlocked.CompareExchange(
                            ref ((PreparationOperation)state!)._terminalOrder,
                            FailureFirst,
                            comparand: 0);
                    }
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task<IPreparedScene> Execution { get; }

        public Task Cancellation => _cancellation;

        public bool CancellationRequested { get; private set; }

        public bool UnsuccessfulCompletionPrecededCancellation =>
            Volatile.Read(ref _terminalOrder) == FailureFirst;

        public bool RequestCancellation()
        {
            if (CancellationRequested)
            {
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
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            await ((Task)Execution).ConfigureAwait(
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
    }
}

/// <summary>
/// Isolates overlay scene construction and activation from actor policy.
/// Implementations marshal calls to any required UI thread.
/// </summary>
public interface IOverlayRenderer
{
    /// <summary>
    /// Prepares a scene without changing observable desktop state.
    /// Cancellation is cooperative; the operation may end by returning a
    /// prepared scene or by throwing.
    /// Throwing for any other reason is an unexpected application failure.
    /// </summary>
    Task<IPreparedScene> PrepareAsync(
        TargetPresentation presentation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically makes the prepared scene current. The operation completes
    /// only after the swap. It disposes neither the incoming nor the outgoing
    /// scene.
    /// Throwing is an unexpected application failure.
    /// </summary>
    Task ActivateAsync(IPreparedScene scene);
}

/// <summary>
/// Owns resources for one prepared overlay scene.
/// </summary>
public interface IPreparedScene : IAsyncDisposable;