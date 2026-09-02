using Akka.Actor;
using Akka.Pattern;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class KanataActorTests
{
    [Fact]
    public async Task RejectsStaleConnectionsAndNonIncreasingSequences()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = RecordingActor.CreateChannel(6);
        var system = ActorSystem.Create("kanata-actor-test");

        try
        {
            var recorder = system.ActorOf(
                Props.Create(() => new RecordingActor(observed.Writer)));
            var kanataActor = system.ActorOf(
                KanataActor.CreateProps(recorder));
            var oldConnection = KanataConnectionId.New();
            var currentConnection = KanataConnectionId.New();
            var gesture = new KanataGesturePushed(
                new GestureToken("pointer", "f"));

            kanataActor.Tell(new KanataConnectionOpened(oldConnection));
            kanataActor.Tell(
                new KanataFrameReceived(
                    oldConnection,
                    KanataFrameSequence.From(1),
                    new KanataLayerChanged(KeyboardLayer.From("command"))));
            kanataActor.Tell(new KanataConnectionOpened(currentConnection));
            kanataActor.Tell(
                new KanataFrameReceived(
                    oldConnection,
                    KanataFrameSequence.From(2),
                    gesture));
            kanataActor.Tell(new KanataConnectionClosed(oldConnection));
            kanataActor.Tell(
                new KanataFrameReceived(
                    currentConnection,
                    KanataFrameSequence.From(1),
                    new KanataLayerChanged(KeyboardLayer.From("pointer"))));
            kanataActor.Tell(
                new KanataFrameReceived(
                    currentConnection,
                    KanataFrameSequence.From(2),
                    gesture));
            kanataActor.Tell(
                new KanataFrameReceived(
                    currentConnection,
                    KanataFrameSequence.From(2),
                    gesture));
            kanataActor.Tell(
                new KanataFrameReceived(
                    currentConnection,
                    KanataFrameSequence.From(1),
                    gesture));
            kanataActor.Tell(new KanataConnectionClosed(currentConnection));

            Assert.True(
                await kanataActor.GracefulStop(
                    TimeSpan.FromSeconds(3),
                    PoisonPill.Instance));
            recorder.Tell(PoisonPill.Instance);
            var messages = new object[5];
            for (var index = 0; index < messages.Length; index++)
            {
                messages[index] = await observed.Reader.ReadAsync(timeout.Token);
            }
            await observed.Reader.Completion.WaitAsync(timeout.Token);
            Assert.False(observed.Reader.TryRead(out _));

            Assert.Collection(
                messages,
                message =>
                {
                    var mode = Assert.IsType<KeyboardLayerObserved>(message);
                    Assert.Equal(oldConnection, mode.ConnectionId);
                    Assert.Equal("command", mode.Layer.Value);
                },
                message => Assert.Equal(
                    oldConnection,
                    Assert.IsType<KeyboardLayerUnavailable>(message).ConnectionId),
                message =>
                {
                    var mode = Assert.IsType<KeyboardLayerObserved>(message);
                    Assert.Equal(currentConnection, mode.ConnectionId);
                    Assert.Equal("pointer", mode.Layer.Value);
                },
                message =>
                {
                    var received = Assert.IsType<GestureObserved>(message);
                    Assert.Equal(currentConnection, received.ConnectionId);
                    Assert.Equal(
                        KanataFrameSequence.From(2),
                        received.Sequence);
                },
                message => Assert.Equal(
                    currentConnection,
                    Assert.IsType<KeyboardLayerUnavailable>(message).ConnectionId));
        }
        finally
        {
            await system.Terminate();
        }
    }
}
