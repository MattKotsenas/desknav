using Akka.Actor;

namespace Desknav.ControlPlane;

public sealed class NavigationCoordinator : ReceiveActor
{
    private readonly IActorRef _targetDiscovery;
    private readonly IActorRef _modeObserver;

    public NavigationCoordinator(
        IActorRef targetDiscovery,
        IActorRef modeObserver)
    {
        _targetDiscovery = targetDiscovery;
        _modeObserver = modeObserver;

        Receive<KeyboardModeObserved>(_modeObserver.Tell);
        Receive<KeyboardModeUnavailable>(_modeObserver.Tell);
        Receive<GestureObserved>(Handle);
    }

    public static Props CreateProps(
        IActorRef targetDiscovery,
        IActorRef modeObserver)
    {
        ArgumentNullException.ThrowIfNull(targetDiscovery);
        ArgumentNullException.ThrowIfNull(modeObserver);
        return Akka.Actor.Props.Create(
            () => new NavigationCoordinator(targetDiscovery, modeObserver));
    }

    private void Handle(GestureObserved observed)
    {
        if (observed.Token is { Context: "pointer", Key: "f" })
        {
            _targetDiscovery.Tell(
                new DiscoverTargets(TargetDiscoveryRequestId.New()));
            return;
        }

        Unhandled(observed);
    }
}
