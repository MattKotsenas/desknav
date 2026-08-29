using Akka.Actor;

namespace Desknav.ControlPlane;

public sealed class KanataBoundaryActor : ReceiveActor
{
    private readonly IActorRef _coordinator;
    private KanataConnectionId? _connectionId;
    private long _lastOrdinal;

    public KanataBoundaryActor(IActorRef coordinator)
    {
        _coordinator = coordinator;

        Receive<KanataConnectionOpened>(Handle);
        Receive<KanataFrameReceived>(Handle);
        Receive<KanataConnectionClosed>(Handle);
    }

    public static Props CreateProps(IActorRef coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return Akka.Actor.Props.Create(
            () => new KanataBoundaryActor(coordinator));
    }

    private void Handle(KanataConnectionOpened opened)
    {
        if (_connectionId is { } previous)
        {
            _coordinator.Tell(new KeyboardModeUnavailable(previous));
        }

        _connectionId = opened.ConnectionId;
        _lastOrdinal = 0;
    }

    private void Handle(KanataFrameReceived received)
    {
        if (_connectionId != received.ConnectionId
            || received.Ordinal.Value <= _lastOrdinal)
        {
            return;
        }

        _lastOrdinal = received.Ordinal.Value;
        switch (received.Frame)
        {
            case KanataLayerChanged layerChanged:
                _coordinator.Tell(
                    new KeyboardModeObserved(
                        received.ConnectionId,
                        received.Ordinal,
                        layerChanged.Layer));
                break;
            case KanataGesturePushed gesturePushed:
                _coordinator.Tell(
                    new GestureObserved(
                        received.ConnectionId,
                        received.Ordinal,
                        gesturePushed.Token));
                break;
        }
    }

    private void Handle(KanataConnectionClosed closed)
    {
        if (_connectionId != closed.ConnectionId)
        {
            return;
        }

        _connectionId = null;
        _lastOrdinal = 0;
        _coordinator.Tell(new KeyboardModeUnavailable(closed.ConnectionId));
    }
}
