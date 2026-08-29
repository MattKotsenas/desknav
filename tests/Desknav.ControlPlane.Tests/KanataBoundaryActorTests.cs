using System.Threading.Channels;

using Akka.Actor;
using Akka.Pattern;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class KanataBoundaryActorTests
{
    [Fact]
    public async Task RejectsStaleConnectionsAndNonIncreasingOrdinals()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = Channel.CreateUnbounded<object>();
        var system = ActorSystem.Create("kanata-boundary-test");

        try
        {
            var recorder = system.ActorOf(
                Props.Create(() => new RecordingActor(observed.Writer)));
            var boundary = system.ActorOf(
                KanataBoundaryActor.CreateProps(recorder));
            var oldConnection = KanataConnectionId.New();
            var currentConnection = KanataConnectionId.New();
            var gesture = new KanataGesturePushed(
                new GestureToken("pointer", "f"));

            boundary.Tell(new KanataConnectionOpened(oldConnection));
            boundary.Tell(
                new KanataFrameReceived(
                    oldConnection,
                    new KanataIngressOrdinal(1),
                    new KanataLayerChanged(new KeyboardLayer("wm"))));
            boundary.Tell(new KanataConnectionOpened(currentConnection));
            boundary.Tell(
                new KanataFrameReceived(
                    oldConnection,
                    new KanataIngressOrdinal(2),
                    gesture));
            boundary.Tell(new KanataConnectionClosed(oldConnection));
            boundary.Tell(
                new KanataFrameReceived(
                    currentConnection,
                    new KanataIngressOrdinal(1),
                    new KanataLayerChanged(new KeyboardLayer("pointer"))));
            boundary.Tell(
                new KanataFrameReceived(
                    currentConnection,
                    new KanataIngressOrdinal(2),
                    gesture));
            boundary.Tell(
                new KanataFrameReceived(
                    currentConnection,
                    new KanataIngressOrdinal(2),
                    gesture));
            boundary.Tell(
                new KanataFrameReceived(
                    currentConnection,
                    new KanataIngressOrdinal(1),
                    gesture));
            boundary.Tell(new KanataConnectionClosed(currentConnection));

            Assert.True(
                await boundary.GracefulStop(
                    TimeSpan.FromSeconds(3),
                    PoisonPill.Instance));
            recorder.Tell(PoisonPill.Instance);
            var messages = await ReadAllAsync(observed.Reader, timeout.Token);

            Assert.Collection(
                messages,
                message =>
                {
                    var mode = Assert.IsType<KeyboardModeObserved>(message);
                    Assert.Equal(oldConnection, mode.ConnectionId);
                    Assert.Equal("wm", mode.Layer.Value);
                },
                message => Assert.Equal(
                    oldConnection,
                    Assert.IsType<KeyboardModeUnavailable>(message).ConnectionId),
                message =>
                {
                    var mode = Assert.IsType<KeyboardModeObserved>(message);
                    Assert.Equal(currentConnection, mode.ConnectionId);
                    Assert.Equal("pointer", mode.Layer.Value);
                },
                message =>
                {
                    var received = Assert.IsType<GestureObserved>(message);
                    Assert.Equal(currentConnection, received.ConnectionId);
                    Assert.Equal(new KanataIngressOrdinal(2), received.Ordinal);
                },
                message => Assert.Equal(
                    currentConnection,
                    Assert.IsType<KeyboardModeUnavailable>(message).ConnectionId));
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<IReadOnlyList<object>> ReadAllAsync(
        ChannelReader<object> reader,
        CancellationToken cancellationToken)
    {
        var messages = new List<object>();
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            messages.Add(message);
        }

        return messages;
    }

    private sealed class RecordingActor : ReceiveActor
    {
        private readonly ChannelWriter<object> _writer;

        public RecordingActor(ChannelWriter<object> writer)
        {
            _writer = writer;
            ReceiveAny(message =>
            {
                if (!_writer.TryWrite(message))
                {
                    throw new InvalidOperationException(
                        "The test recorder rejected a boundary message.");
                }
            });
        }

        protected override void PostStop() => _writer.TryComplete();
    }
}
