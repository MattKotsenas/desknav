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

    public OverlayActor(IOverlayRenderer renderer)
    {
        _renderer = renderer;

        Receive<ApplyTargetPresentation>(Handle);
        Receive<PreparationFinished>(Handle);
        Receive<PreparationFaulted>(Handle);
        Receive<CancellationFailed>(Handle);
        Receive<ActivationFinished>(Handle);
        Receive<ActivationFaulted>(Handle);
        Receive<SceneReleased>(Handle);
        Receive<SceneReleaseFaulted>(Handle);
    }

    public static Props CreateProps(IOverlayRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return Props.Create(() => new OverlayActor(renderer));
    }

    protected override void PostStop()
    {
        foreach (var operation in _preparations.Values)
        {
            _ = ReleasePreparationDuringShutdownAsync(operation);
        }
        _preparations.Clear();

        if (_ready is { } ready)
        {
            _ready = null;
            _ = ReleaseSceneDuringShutdownAsync(ready.Scene);
        }

        if (_activation is { } activation)
        {
            _activation = null;
            var activeScene = _active?.Scene;
            _active = null;
            _ = ReleaseActivationDuringShutdownAsync(
                activation,
                activeScene);
        }
        else if (_active is { } active)
        {
            _active = null;
            _ = ReleaseSceneDuringShutdownAsync(active.Scene);
        }

        foreach (var release in _releases)
        {
            _ = ObserveReleaseDuringShutdownAsync(release);
        }
        _releases.Clear();

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
        var execution = Task.Run(
            () => _renderer.PrepareAsync(
                presentation,
                cancellation.Token));
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
        if (operation.CancellationRequested)
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
        var execution = Task.Run(
            () => _renderer.ActivateAsync(ready.Scene));
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

    private static void ReleasePreparation(
        PreparationOperation operation) =>
        _ = operation.DisposeAsync();

    private void ReleaseScene(IPreparedScene scene)
    {
        var release = Task.Run(
            async () => await scene.DisposeAsync().ConfigureAwait(false));
        _releases.Add(release);
        release.PipeTo(
            Self,
            Self,
            () => new SceneReleased(release),
            exception => new SceneReleaseFaulted(release, exception));
    }

    private void Handle(SceneReleased released) =>
        _releases.Remove(released.ReleaseTask);

    private void Handle(SceneReleaseFaulted faulted)
    {
        _releases.Remove(faulted.ReleaseTask);
        StopApplication(
            faulted.Cause,
            "Release of an overlay scene faulted unexpectedly.");
    }

    private void StopApplication(
        Exception cause,
        string message,
        params object[] arguments)
    {
        if (_isTerminating)
        {
            return;
        }

        _isTerminating = true;
        _log.Error(cause, message, arguments);
        Context.System.Terminate();
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
        try
        {
            operation.RequestCancellation();

            await operation.Cancellation.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing);
            if (operation.Cancellation.IsFaulted)
            {
                _log.Error(
                    operation.Cancellation.Exception,
                    "Presentation cancellation failed during shutdown.");
            }

            await ((Task)operation.Execution).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing);
            if (operation.Execution.IsFaulted)
            {
                _log.Debug(
                    operation.Execution.Exception,
                    "Canceled presentation preparation faulted during"
                    + " shutdown.");
            }

            if (operation.Execution.Status == TaskStatus.RanToCompletion)
            {
                scene = operation.Execution.Result;
            }
        }
        catch (Exception exception)
        {
            _log.Error(
                exception,
                "Presentation preparation cleanup failed during shutdown.");
        }
        finally
        {
            if (scene is not null)
            {
                await ReleaseSceneDuringShutdownAsync(scene)
                    .ConfigureAwait(false);
            }

            try
            {
                await operation.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Error(
                    exception,
                    "Presentation preparation release failed during"
                    + " shutdown.");
            }
        }
    }

    private async Task ReleaseActivationDuringShutdownAsync(
        ActivationOperation operation,
        IPreparedScene? activeScene)
    {
        await operation.Execution.ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing);
        if (operation.Execution.IsFaulted)
        {
            _log.Error(
                operation.Execution.Exception,
                "Presentation activation faulted during shutdown.");
        }

        await ReleaseSceneDuringShutdownAsync(operation.Scene)
            .ConfigureAwait(false);
        if (activeScene is not null)
        {
            await ReleaseSceneDuringShutdownAsync(activeScene)
                .ConfigureAwait(false);
        }
    }

    private async Task ReleaseSceneDuringShutdownAsync(
        IPreparedScene scene)
    {
        try
        {
            await scene.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Error(
                exception,
                "Overlay scene cleanup failed during shutdown.");
        }
    }

    private async Task ObserveReleaseDuringShutdownAsync(Task release)
    {
        try
        {
            await release.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Error(
                exception,
                "Overlay scene release failed during shutdown.");
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

    private sealed record SceneReleased(Task ReleaseTask);

    private sealed record SceneReleaseFaulted(
        Task ReleaseTask,
        Exception Cause);

    private sealed class PreparationOperation(
        CancellationTokenSource cancellation,
        Task<IPreparedScene> execution)
        : IAsyncDisposable
    {
        private Task _cancellation = Task.CompletedTask;
        private bool _isDisposed;

        public Task<IPreparedScene> Execution { get; } = execution;

        public Task Cancellation => _cancellation;

        public bool CancellationRequested { get; private set; }

        public bool RequestCancellation()
        {
            if (CancellationRequested)
            {
                return false;
            }

            CancellationRequested = true;
            _cancellation = cancellation.CancelAsync();
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
            await _cancellation.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing);
            cancellation.Dispose();
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