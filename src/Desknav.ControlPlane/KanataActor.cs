using Akka.Actor;

namespace Desknav.ControlPlane;

public sealed class KanataActor : ReceiveActor
{
    private readonly IActorRef _coordinator;
    private KanataConnectionId? _connectionId;
    private long _sequenceHighWatermark;

    public KanataActor(IActorRef coordinator)
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
            () => new KanataActor(coordinator));
    }

    private void Handle(KanataConnectionOpened opened)
    {
        if (_connectionId is { } previous)
        {
            _coordinator.Tell(new KeyboardModeUnavailable(previous));
        }

        _connectionId = opened.ConnectionId;
        _sequenceHighWatermark = 0;
    }

    private void Handle(KanataFrameReceived received)
    {
        if (_connectionId != received.ConnectionId
            || received.Sequence.Value <= _sequenceHighWatermark)
        {
            return;
        }

        _sequenceHighWatermark = received.Sequence.Value;
        switch (received.Frame)
        {
            case KanataLayerChanged layerChanged:
                _coordinator.Tell(
                    new KeyboardModeObserved(
                        received.ConnectionId,
                        received.Sequence,
                        layerChanged.Layer));
                break;
            case KanataGesturePushed gesturePushed:
                _coordinator.Tell(
                    new GestureObserved(
                        received.ConnectionId,
                        received.Sequence,
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
        _sequenceHighWatermark = 0;
        _coordinator.Tell(new KeyboardModeUnavailable(closed.ConnectionId));
    }
}
