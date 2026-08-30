using Akka.Actor;

namespace Desknav.ControlPlane;

public sealed class NavigationCoordinator : ReceiveActor
{
    private readonly IActorRef _targetDiscovery;
    private readonly IActorRef _inputObserver;
    private CommandSessionState _commandState;

    public NavigationCoordinator(
        IActorRef targetDiscovery,
        IActorRef inputObserver)
    {
        _targetDiscovery = targetDiscovery;
        _inputObserver = inputObserver;

        Receive<KeyboardLayerObserved>(Handle);
        Receive<KeyboardLayerUnavailable>(Handle);
        Receive<GestureObserved>(Handle);
    }

    public static Props CreateProps(
        IActorRef targetDiscovery,
        IActorRef inputObserver)
    {
        ArgumentNullException.ThrowIfNull(targetDiscovery);
        ArgumentNullException.ThrowIfNull(inputObserver);
        return Akka.Actor.Props.Create(
            () => new NavigationCoordinator(targetDiscovery, inputObserver));
    }

    private void Handle(KeyboardLayerObserved observed)
    {
        _inputObserver.Tell(observed);
        if (observed.Layer.Value == "base")
        {
            EndCommandSession();
        }
        else if (observed.Layer.Value == "command")
        {
            _commandState = CommandSessionState.Command;
        }
    }

    private void Handle(KeyboardLayerUnavailable unavailable)
    {
        _inputObserver.Tell(unavailable);
        EndCommandSession();
    }

    private void Handle(GestureObserved observed)
    {
        _inputObserver.Tell(new CommandInputObserved(observed.Token));

        if (observed.Token.Key == "esc")
        {
            EndCommandSession();
            return;
        }

        if (observed.Token is { Context: "command", Key: "spc" })
        {
            _commandState = CommandSessionState.PointerPrefix;
        }
        else if (observed.Token is { Context: "pointer", Key: "f" }
                 && _commandState == CommandSessionState.PointerPrefix)
        {
            _commandState = CommandSessionState.Command;
            _targetDiscovery.Tell(
                new DiscoverTargets(TargetDiscoveryRequestId.New()));
        }
    }

    private void EndCommandSession()
    {
        if (_commandState == CommandSessionState.Inactive)
        {
            return;
        }

        _commandState = CommandSessionState.Inactive;
        _inputObserver.Tell(new CommandSessionEnded());
    }

    private enum CommandSessionState
    {
        Inactive,
        Command,
        PointerPrefix,
    }
}
