using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

using Akka.Actor;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class CommandGestureIngressTests
{
    [Fact]
    public async Task CapSpaceFRequestsTargetDiscoveryAndReturnsToCommand()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observedInput = CreateRecorderChannel(7);
        var observedDiscoveries = CreateRecorderChannel(1);
        var system = ActorSystem.Create("command-gesture-ingress-test");
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            var inputObserver = system.ActorOf(
                Props.Create(() => new RecordingActor(observedInput.Writer)));
            var targetDiscovery = system.ActorOf(
                Props.Create(
                    () => new RecordingActor(observedDiscoveries.Writer)));
            var coordinator = system.ActorOf(
                NavigationCoordinator.CreateProps(
                    targetDiscovery,
                    inputObserver));
            var kanataActor = system.ActorOf(
                KanataActor.CreateProps(coordinator));
            listener.Start();
            var ingress = new KanataTcpIngress(
                (IPEndPoint)listener.LocalEndpoint,
                new KanataFrameParser());

            var server = WriteFramesAsync(listener, timeout.Token);

            await ingress.RunAsync(kanataActor, timeout.Token);
            await server;

            var commandLayer = await ReadAsync<KeyboardLayerObserved>(
                observedInput.Reader,
                timeout.Token);
            var commandSpace = await ReadAsync<CommandInputObserved>(
                observedInput.Reader,
                timeout.Token);
            var pointerLayer = await ReadAsync<KeyboardLayerObserved>(
                observedInput.Reader,
                timeout.Token);
            var pointerTarget = await ReadAsync<CommandInputObserved>(
                observedInput.Reader,
                timeout.Token);
            var resumedCommandLayer = await ReadAsync<KeyboardLayerObserved>(
                observedInput.Reader,
                timeout.Token);
            var unavailable = await ReadAsync<KeyboardLayerUnavailable>(
                observedInput.Reader,
                timeout.Token);
            Assert.IsType<CommandSessionEnded>(
                await observedInput.Reader.ReadAsync(timeout.Token));

            Assert.Equal("command", commandLayer.Layer.Value);
            Assert.Equal("spc", commandSpace.Token.Key);
            Assert.Equal("pointer", pointerLayer.Layer.Value);
            Assert.Equal("f", pointerTarget.Token.Key);
            Assert.Equal("command", resumedCommandLayer.Layer.Value);
            Assert.Equal(
                resumedCommandLayer.ConnectionId,
                unavailable.ConnectionId);

            inputObserver.Tell(PoisonPill.Instance);
            targetDiscovery.Tell(PoisonPill.Instance);
            await WaitForCompletionAsync(observedInput.Reader, timeout.Token);
            var discovery = await ReadAsync<DiscoverTargets>(
                observedDiscoveries.Reader,
                timeout.Token);
            await WaitForCompletionAsync(
                observedDiscoveries.Reader,
                timeout.Token);

            Assert.False(observedInput.Reader.TryRead(out _));
            Assert.False(observedDiscoveries.Reader.TryRead(out _));
            Assert.NotEqual(
                Guid.Empty,
                discovery.RequestId.Value);
        }
        finally
        {
            listener.Stop();
            await system.Terminate();
        }
    }

    [Fact]
    public async Task CommandKeysReachCoordinatorInOrder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = CreateRecorderChannel(11);
        var system = ActorSystem.Create("command-token-stream-test");
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            var recorder = system.ActorOf(
                Props.Create(() => new RecordingActor(observed.Writer)));
            var coordinator = system.ActorOf(
                NavigationCoordinator.CreateProps(ActorRefs.Nobody, recorder));
            var kanataActor = system.ActorOf(
                KanataActor.CreateProps(coordinator));
            listener.Start();
            var ingress = new KanataTcpIngress(
                (IPEndPoint)listener.LocalEndpoint,
                new KanataFrameParser());
            var server = WriteFramesAsync(
                listener,
                """
                {"LayerChange":{"new":"command"}}
                {"MessagePush":{"message":["gesture","command","f"]}}
                {"MessagePush":{"message":["gesture","command","d"]}}
                {"MessagePush":{"message":["gesture","command","f"]}}
                {"MessagePush":{"message":["gesture","command","d"]}}
                {"MessagePush":{"message":["gesture","command","f"]}}
                {"MessagePush":{"message":["gesture","command","l"]}}
                {"MessagePush":{"message":["gesture","command","esc"]}}
                {"LayerChange":{"new":"base"}}
                """,
                timeout.Token);

            await ingress.RunAsync(kanataActor, timeout.Token);
            await server;

            Assert.Equal(
                "command",
                (await ReadAsync<KeyboardLayerObserved>(
                    observed.Reader,
                    timeout.Token)).Layer.Value);

            var expected = new[] { "f", "d", "f", "d", "f", "l" };
            foreach (var expectedKey in expected)
            {
                var token = await ReadAsync<CommandInputObserved>(
                    observed.Reader,
                    timeout.Token);
                Assert.Equal(expectedKey, token.Token.Key);
            }

            Assert.Equal(
                "esc",
                (await ReadAsync<CommandInputObserved>(
                    observed.Reader,
                    timeout.Token)).Token.Key);
            Assert.IsType<CommandSessionEnded>(
                await observed.Reader.ReadAsync(timeout.Token));
            Assert.Equal(
                "base",
                (await ReadAsync<KeyboardLayerObserved>(
                    observed.Reader,
                    timeout.Token)).Layer.Value);
            Assert.IsType<KeyboardLayerUnavailable>(
                await observed.Reader.ReadAsync(timeout.Token));

            recorder.Tell(PoisonPill.Instance);
            await WaitForCompletionAsync(observed.Reader, timeout.Token);
            Assert.False(observed.Reader.TryRead(out _));
        }
        finally
        {
            listener.Stop();
            await system.Terminate();
        }
    }

    [Fact]
    public async Task PointerTargetWithoutCurrentSpacePrefixIsRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observedInput = CreateRecorderChannel(3);
        var observedDiscoveries = CreateRecorderChannel(0);
        var system = ActorSystem.Create("pointer-prefix-rejection-test");
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            var inputObserver = system.ActorOf(
                Props.Create(() => new RecordingActor(observedInput.Writer)));
            var targetDiscovery = system.ActorOf(
                Props.Create(
                    () => new RecordingActor(observedDiscoveries.Writer)));
            var coordinator = system.ActorOf(
                NavigationCoordinator.CreateProps(
                    targetDiscovery,
                    inputObserver));
            var kanataActor = system.ActorOf(
                KanataActor.CreateProps(coordinator));
            listener.Start();
            var ingress = new KanataTcpIngress(
                (IPEndPoint)listener.LocalEndpoint,
                new KanataFrameParser());
            var server = WriteFramesAsync(
                listener,
                """
                {"LayerChange":{"new":"pointer"}}
                {"MessagePush":{"message":["gesture","pointer","f"]}}
                """,
                timeout.Token);

            await ingress.RunAsync(kanataActor, timeout.Token);
            await server;

            Assert.Equal(
                "pointer",
                (await ReadAsync<KeyboardLayerObserved>(
                    observedInput.Reader,
                    timeout.Token)).Layer.Value);
            Assert.Equal(
                "f",
                (await ReadAsync<CommandInputObserved>(
                    observedInput.Reader,
                    timeout.Token)).Token.Key);
            Assert.IsType<KeyboardLayerUnavailable>(
                await observedInput.Reader.ReadAsync(timeout.Token));

            inputObserver.Tell(PoisonPill.Instance);
            targetDiscovery.Tell(PoisonPill.Instance);

            await WaitForCompletionAsync(observedInput.Reader, timeout.Token);
            await WaitForCompletionAsync(
                observedDiscoveries.Reader,
                timeout.Token);
            Assert.False(observedInput.Reader.TryRead(out _));
            Assert.False(observedDiscoveries.Reader.TryRead(out _));
        }
        finally
        {
            listener.Stop();
            await system.Terminate();
        }
    }

    [Fact]
    public async Task ReturnToCommandClearsIncompletePointerPrefix()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observedInput = CreateRecorderChannel(8);
        var observedDiscoveries = CreateRecorderChannel(0);
        var system = ActorSystem.Create("pointer-prefix-reset-test");
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            var inputObserver = system.ActorOf(
                Props.Create(() => new RecordingActor(observedInput.Writer)));
            var targetDiscovery = system.ActorOf(
                Props.Create(
                    () => new RecordingActor(observedDiscoveries.Writer)));
            var coordinator = system.ActorOf(
                NavigationCoordinator.CreateProps(
                    targetDiscovery,
                    inputObserver));
            var kanataActor = system.ActorOf(
                KanataActor.CreateProps(coordinator));
            listener.Start();
            var ingress = new KanataTcpIngress(
                (IPEndPoint)listener.LocalEndpoint,
                new KanataFrameParser());
            var server = WriteFramesAsync(
                listener,
                """
                {"LayerChange":{"new":"command"}}
                {"MessagePush":{"message":["gesture","command","spc"]}}
                {"LayerChange":{"new":"pointer"}}
                {"LayerChange":{"new":"command"}}
                {"LayerChange":{"new":"pointer"}}
                {"MessagePush":{"message":["gesture","pointer","f"]}}
                """,
                timeout.Token);

            await ingress.RunAsync(kanataActor, timeout.Token);
            await server;

            for (var index = 0; index < 7; index++)
            {
                await observedInput.Reader.ReadAsync(timeout.Token);
            }
            Assert.IsType<CommandSessionEnded>(
                await observedInput.Reader.ReadAsync(timeout.Token));

            inputObserver.Tell(PoisonPill.Instance);
            targetDiscovery.Tell(PoisonPill.Instance);
            await WaitForCompletionAsync(observedInput.Reader, timeout.Token);
            await WaitForCompletionAsync(
                observedDiscoveries.Reader,
                timeout.Token);

            Assert.False(observedInput.Reader.TryRead(out _));
            Assert.False(observedDiscoveries.Reader.TryRead(out _));
        }
        finally
        {
            listener.Stop();
            await system.Terminate();
        }
    }

    [Fact]
    public async Task EachSpacePointerFTupleRequestsDiscovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observedInput = CreateRecorderChannel(11);
        var observedDiscoveries = CreateRecorderChannel(2);
        var system = ActorSystem.Create("repeated-target-discovery-test");
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            var inputObserver = system.ActorOf(
                Props.Create(() => new RecordingActor(observedInput.Writer)));
            var targetDiscovery = system.ActorOf(
                Props.Create(
                    () => new RecordingActor(observedDiscoveries.Writer)));
            var coordinator = system.ActorOf(
                NavigationCoordinator.CreateProps(
                    targetDiscovery,
                    inputObserver));
            var kanataActor = system.ActorOf(
                KanataActor.CreateProps(coordinator));
            listener.Start();
            var ingress = new KanataTcpIngress(
                (IPEndPoint)listener.LocalEndpoint,
                new KanataFrameParser());
            var server = WriteFramesAsync(
                listener,
                """
                {"LayerChange":{"new":"command"}}
                {"MessagePush":{"message":["gesture","command","spc"]}}
                {"LayerChange":{"new":"pointer"}}
                {"MessagePush":{"message":["gesture","pointer","f"]}}
                {"LayerChange":{"new":"command"}}
                {"MessagePush":{"message":["gesture","command","spc"]}}
                {"LayerChange":{"new":"pointer"}}
                {"MessagePush":{"message":["gesture","pointer","f"]}}
                {"LayerChange":{"new":"command"}}
                """,
                timeout.Token);

            await ingress.RunAsync(kanataActor, timeout.Token);
            await server;

            for (var index = 0; index < 9; index++)
            {
                await observedInput.Reader.ReadAsync(timeout.Token);
            }
            Assert.IsType<KeyboardLayerUnavailable>(
                await observedInput.Reader.ReadAsync(timeout.Token));
            Assert.IsType<CommandSessionEnded>(
                await observedInput.Reader.ReadAsync(timeout.Token));

            inputObserver.Tell(PoisonPill.Instance);
            targetDiscovery.Tell(PoisonPill.Instance);
            await WaitForCompletionAsync(observedInput.Reader, timeout.Token);
            var first = await ReadAsync<DiscoverTargets>(
                observedDiscoveries.Reader,
                timeout.Token);
            var second = await ReadAsync<DiscoverTargets>(
                observedDiscoveries.Reader,
                timeout.Token);
            await WaitForCompletionAsync(
                observedDiscoveries.Reader,
                timeout.Token);

            Assert.NotEqual(first.RequestId, second.RequestId);
            Assert.False(observedDiscoveries.Reader.TryRead(out _));
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
                """{"MessagePush":{"message":["status","ready"]}}""",
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
            {"LayerChange":{"new":"command"}}
            {"MessagePush":{"message":["gesture","command","spc"]}}
            {"LayerChange":{"new":"pointer"}}
            {"MessagePush":{"message":["gesture","pointer","f"]}}
            {"LayerChange":{"new":"command"}}
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

    private static Channel<object> CreateRecorderChannel(int expectedMessages) =>
        Channel.CreateBounded<object>(
            new BoundedChannelOptions(expectedMessages + 1)
            {
                SingleReader = true,
                SingleWriter = true,
            });

    private static async Task WaitForCompletionAsync(
        ChannelReader<object> reader,
        CancellationToken cancellationToken) =>
        await reader.Completion.WaitAsync(cancellationToken);

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
