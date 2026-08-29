using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

using Akka.Actor;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class ProgressiveGestureIngressTests
{
    [Fact]
    public async Task CapSpaceFRequestsTargetDiscoveryAfterProgressiveModes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observedModes = Channel.CreateUnbounded<object>();
        var observedDiscoveries = Channel.CreateUnbounded<object>();
        var system = ActorSystem.Create("progressive-gesture-ingress-test");
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            var modeObserver = system.ActorOf(
                Props.Create(() => new RecordingActor(observedModes.Writer)));
            var targetDiscovery = system.ActorOf(
                Props.Create(
                    () => new RecordingActor(observedDiscoveries.Writer)));
            var coordinator = system.ActorOf(
                NavigationCoordinator.CreateProps(
                    targetDiscovery,
                    modeObserver));
            var kanataActor = system.ActorOf(
                KanataActor.CreateProps(coordinator));
            listener.Start();
            var ingress = new KanataTcpIngress(
                (IPEndPoint)listener.LocalEndpoint,
                new KanataFrameParser());

            var server = WriteFramesAsync(listener, timeout.Token);

            await ingress.RunAsync(kanataActor, timeout.Token);
            await server;

            var commandMode = await ReadAsync<KeyboardModeObserved>(
                observedModes.Reader,
                timeout.Token);
            var pointerMode = await ReadAsync<KeyboardModeObserved>(
                observedModes.Reader,
                timeout.Token);
            var baseMode = await ReadAsync<KeyboardModeObserved>(
                observedModes.Reader,
                timeout.Token);
            var unavailable = await ReadAsync<KeyboardModeUnavailable>(
                observedModes.Reader,
                timeout.Token);

            Assert.Equal("wm", commandMode.Layer.Value);
            Assert.Equal("pointer", pointerMode.Layer.Value);
            Assert.Equal("base", baseMode.Layer.Value);
            Assert.Equal(baseMode.ConnectionId, unavailable.ConnectionId);

            modeObserver.Tell(PoisonPill.Instance);
            targetDiscovery.Tell(PoisonPill.Instance);
            var extraModeMessages = await ReadRemainingAsync(
                observedModes.Reader,
                timeout.Token);
            var discoveries = await ReadRemainingAsync(
                observedDiscoveries.Reader,
                timeout.Token);

            Assert.Empty(extraModeMessages);
            var discovery = Assert.Single(discoveries);
            Assert.NotEqual(
                Guid.Empty,
                Assert.IsType<DiscoverTargets>(discovery).RequestId.Value);
        }
        finally
        {
            listener.Stop();
            await system.Terminate();
        }
    }

    [Fact]
    public async Task NonGestureMessagePushIsRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            listener.Start();
            var ingress = new KanataTcpIngress(
                (IPEndPoint)listener.LocalEndpoint,
                new KanataFrameParser());
            var server = WriteFramesAsync(
                listener,
                """{"MessagePush":{"message":["wm.focus.left"]}}""",
                timeout.Token);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => ingress.RunAsync(ActorRefs.Nobody, timeout.Token));
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteFramesAsync(
        TcpListener listener,
        CancellationToken cancellationToken) =>
        await WriteFramesAsync(
            listener,
            """
            {"LayerChange":{"new":"wm"}}
            {"LayerChange":{"new":"pointer"}}
            {"MessagePush":{"message":["gesture","pointer","f"]}}
            {"LayerChange":{"new":"base"}}
            """,
            cancellationToken);

    private static async Task WriteFramesAsync(
        TcpListener listener,
        string frames,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();

        await stream.WriteAsync(
            Encoding.UTF8.GetBytes(frames + '\n'),
            cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(
        ChannelReader<object> reader,
        CancellationToken cancellationToken)
    {
        var message = await reader.ReadAsync(cancellationToken);
        return Assert.IsType<T>(message);
    }

    private static async Task<IReadOnlyList<object>> ReadRemainingAsync(
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
                        "The test recorder rejected a control-plane message.");
                }
            });
        }

        protected override void PostStop() => _writer.TryComplete();
    }
}
