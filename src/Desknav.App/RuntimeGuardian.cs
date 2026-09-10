using Akka;
using Akka.Actor;
using Akka.Event;

using Microsoft.Extensions.Hosting;

using Desknav.ControlPlane;
using Desknav.UI;

namespace Desknav.App;

/// <summary>
/// Owns the critical local actor graph and translates unrecoverable component
/// loss into host shutdown.
/// </summary>
internal sealed class RuntimeGuardian : ReceiveActor
{
    private static readonly TimeSpan TargetDiscoveryTimeout =
        TimeSpan.FromSeconds(5);

    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly RuntimeOutcome _outcome;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly IActorRef _overlay;
    private readonly IActorRef _coordinator;
    private readonly IActorRef _kanata;
    private readonly HashSet<IActorRef> _children;
    private IActorRef? _shutdownRequester;
    private bool _isStopping;

    public RuntimeGuardian(
        ITargetDiscovery targetDiscovery,
        IOverlayRenderer overlayRenderer,
        IHostApplicationLifetime applicationLifetime,
        RuntimeOutcome outcome)
    {
        _applicationLifetime = applicationLifetime;
        _outcome = outcome;

        _overlay = Context.ActorOf(
            OverlayActor.CreateProps(overlayRenderer),
            "overlay");
        _coordinator = Context.ActorOf(
            NavigationCoordinator.CreateProps(
                TargetDiscoveryActor.CreateProps(
                    targetDiscovery,
                    TargetDiscoveryTimeout),
                ActorRefs.Nobody,
                _overlay),
            "coordinator");
        _kanata = Context.ActorOf(
            KanataActor.CreateProps(_coordinator),
            "kanata");
        _children = [_overlay, _coordinator, _kanata];
        foreach (var child in _children)
        {
            Context.Watch(child);
        }

        Receive<RuntimeFailure>(failure => StopApplication(failure.Cause));
        Receive<PrepareForShutdown>(Handle);
        Receive<Terminated>(Handle);
        ReceiveAny(_kanata.Forward);
    }

    protected override SupervisorStrategy SupervisorStrategy() =>
        new OneForOneStrategy(
            exception =>
            {
                StopApplication(exception);
                return Directive.Stop;
            });

    private void Handle(PrepareForShutdown _)
    {
        if (_isStopping)
        {
            return;
        }

        _isStopping = true;
        _shutdownRequester = Sender;
        if (_children.Contains(_overlay))
        {
            _overlay.Tell(new PrepareForShutdown());
        }
        if (_children.Contains(_coordinator))
        {
            _coordinator.Tell(new PrepareForShutdown());
        }
        if (_children.Contains(_kanata))
        {
            Context.Stop(_kanata);
        }

        CompleteShutdownIfReady();
    }

    private void Handle(Terminated terminated)
    {
        if (!_children.Remove(terminated.ActorRef))
        {
            return;
        }

        if (!_isStopping)
        {
            StopApplication(
                new InvalidOperationException(
                    $"{terminated.ActorRef.Path.Name} stopped unexpectedly."));
            return;
        }

        CompleteShutdownIfReady();
    }

    private void CompleteShutdownIfReady()
    {
        if (_children.Count != 0)
        {
            return;
        }

        _shutdownRequester?.Tell(Done.Instance);
        Context.Stop(Self);
    }

    private void StopApplication(Exception cause)
    {
        _outcome.RecordFailure(cause);
        _log.Error(cause, "A critical Desknav component failed.");
        _applicationLifetime.StopApplication();
    }
}
