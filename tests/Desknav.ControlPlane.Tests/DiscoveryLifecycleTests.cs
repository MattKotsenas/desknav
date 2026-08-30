using System.Threading.Channels;

using Akka.Actor;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class DiscoveryLifecycleTests
{
    [Fact]
    public async Task CurrentResultBeforeCommandLayerObservationPresents() =>
        await AssertCurrentResultPresentsAsync(
            beginIncompletePointerPrefix: false,
            observeCommandLayerBeforeResult: false);

    [Fact]
    public async Task CurrentResultAfterCommandLayerObservationPresents() =>
        await AssertCurrentResultPresentsAsync(
            beginIncompletePointerPrefix: false,
            observeCommandLayerBeforeResult: true);

    [Fact]
    public async Task ResultBeforeIncompletePointerPrefixResetPresents() =>
        await AssertCurrentResultPresentsAsync(
            beginIncompletePointerPrefix: true,
            observeCommandLayerBeforeResult: false);

    [Fact]
    public async Task ResultAfterIncompletePointerPrefixResetPresents() =>
        await AssertCurrentResultPresentsAsync(
            beginIncompletePointerPrefix: true,
            observeCommandLayerBeforeResult: true);

    [Theory]
    [InlineData(CommandSessionExit.Escape)]
    [InlineData(CommandSessionExit.BaseLayer)]
    [InlineData(CommandSessionExit.Disconnect)]
    public async Task CommandSessionExitCancelsActiveDiscoveryOnce(
        CommandSessionExit exit)
    {
        await using var harness = new CoordinatorHarness();
        var discovery = await harness.StartDiscoveryAsync();
        harness.ObserveGesture("command", "spc");

        harness.EndCommandSession(exit);
        var cancellation =
            await harness.ReadTargetDiscoveryAsync<CancelTargetDiscovery>();

        Assert.Equal(discovery.RequestId, cancellation.RequestId);

        harness.EndCommandSession(CommandSessionExit.BaseLayer);
        harness.Complete(discovery);

        var next = await harness.StartDiscoveryAsync();
        harness.Complete(next);
        var presentation = await harness.ReadPresentationAsync();
        Assert.Equal(next.RequestId, presentation.Snapshot.RequestId);
    }

    [Fact]
    public async Task ReplacementCancelsOldDiscoveryBeforeStartingNewOne()
    {
        await using var harness = new CoordinatorHarness();
        var first = await harness.StartDiscoveryAsync();

        harness.RequestDiscovery();
        var cancellation =
            await harness.ReadTargetDiscoveryAsync<CancelTargetDiscovery>();
        var second =
            await harness.ReadTargetDiscoveryAsync<DiscoverTargets>();

        Assert.Equal(first.RequestId, cancellation.RequestId);
        Assert.NotEqual(first.RequestId, second.RequestId);
    }

    [Fact]
    public async Task OnlyMatchingCurrentDiscoveryCanPresent()
    {
        await using var harness = new CoordinatorHarness();
        var first = await harness.StartDiscoveryAsync();

        harness.RequestDiscovery();
        await harness.ReadTargetDiscoveryAsync<CancelTargetDiscovery>();
        var second =
            await harness.ReadTargetDiscoveryAsync<DiscoverTargets>();

        harness.Complete(first);
        harness.Complete(second);
        var presentation = await harness.ReadPresentationAsync();
        Assert.Equal(second.RequestId, presentation.Snapshot.RequestId);

        harness.Complete(second);
        var third = await harness.StartDiscoveryAsync();
        harness.Complete(third);
        var nextPresentation = await harness.ReadPresentationAsync();
        Assert.Equal(third.RequestId, nextPresentation.Snapshot.RequestId);
    }

    [Fact]
    public async Task AcceptedDiscoveriesReceiveIncreasingPresentationRevisions()
    {
        await using var harness = new CoordinatorHarness();
        var first = await harness.StartDiscoveryAsync();
        harness.Complete(first);
        var firstPresentation = await harness.ReadPresentationAsync();

        var second = await harness.StartDiscoveryAsync();
        harness.Complete(second);
        var secondPresentation = await harness.ReadPresentationAsync();

        Assert.True(
            secondPresentation.Revision.Value
            > firstPresentation.Revision.Value);
    }

    public enum CommandSessionExit
    {
        Escape,
        BaseLayer,
        Disconnect,
    }

    private static async Task AssertCurrentResultPresentsAsync(
        bool beginIncompletePointerPrefix,
        bool observeCommandLayerBeforeResult)
    {
        await using var harness = new CoordinatorHarness();
        var discovery = await harness.StartDiscoveryAsync();

        if (beginIncompletePointerPrefix)
        {
            harness.ObserveGesture("command", "spc");
        }

        if (observeCommandLayerBeforeResult)
        {
            harness.ObserveLayer("command");
        }

        harness.Complete(discovery);
        if (!observeCommandLayerBeforeResult)
        {
            harness.ObserveLayer("command");
        }

        var presentation = await harness.ReadPresentationAsync();
        Assert.Equal(discovery.RequestId, presentation.Snapshot.RequestId);

        if (beginIncompletePointerPrefix)
        {
            var next = await harness.StartDiscoveryAsync();
            harness.Complete(next);
            var nextPresentation = await harness.ReadPresentationAsync();
            Assert.Equal(
                next.RequestId,
                nextPresentation.Snapshot.RequestId);
        }
    }

    private sealed class CoordinatorHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout =
            new(TimeSpan.FromSeconds(10));
        private readonly KanataConnectionId _connectionId =
            KanataConnectionId.New();
        private readonly ActorSystem _system;
        private readonly IActorRef _coordinator;
        private readonly Channel<object> _presentations;
        private readonly Channel<object> _targetDiscovery;
        private long _sequence;

        public CoordinatorHarness()
        {
            _targetDiscovery = CreateRecorderChannel();
            _presentations = CreateRecorderChannel();
            _system = ActorSystem.Create(
                $"discovery-lifecycle-{Guid.NewGuid():N}");
            var targetDiscovery = _system.ActorOf(
                Props.Create(
                    () => new RecordingActor(_targetDiscovery.Writer)));
            var overlayOwner = _system.ActorOf(
                Props.Create(
                    () => new RecordingActor(_presentations.Writer)));
            _coordinator = _system.ActorOf(
                NavigationCoordinator.CreateProps(
                    targetDiscovery,
                    ActorRefs.Nobody,
                    overlayOwner));
        }

        public async Task<DiscoverTargets> StartDiscoveryAsync()
        {
            RequestDiscovery();
            return await ReadTargetDiscoveryAsync<DiscoverTargets>();
        }

        public void RequestDiscovery()
        {
            ObserveGesture("command", "spc");
            ObserveGesture("pointer", "f");
        }

        public void Complete(DiscoverTargets discovery) =>
            _coordinator.Tell(
                new TargetDiscoveryCompleted(
                    new TargetSnapshot(discovery.RequestId)));

        public void ObserveLayer(string layer) =>
            _coordinator.Tell(
                new KeyboardLayerObserved(
                    _connectionId,
                    NextSequence(),
                    KeyboardLayer.From(layer)));

        public void ObserveGesture(string context, string key) =>
            _coordinator.Tell(
                new GestureObserved(
                    _connectionId,
                    NextSequence(),
                    new GestureToken(context, key)));

        public void EndCommandSession(CommandSessionExit exit)
        {
            switch (exit)
            {
                case CommandSessionExit.Escape:
                    ObserveGesture("command", "esc");
                    break;
                case CommandSessionExit.BaseLayer:
                    ObserveLayer("base");
                    break;
                case CommandSessionExit.Disconnect:
                    _coordinator.Tell(
                        new KeyboardLayerUnavailable(_connectionId));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(exit), exit, null);
            }
        }

        public async Task<T> ReadTargetDiscoveryAsync<T>() =>
            Assert.IsType<T>(
                await _targetDiscovery.Reader.ReadAsync(_timeout.Token));

        public async Task<PresentTargets> ReadPresentationAsync() =>
            Assert.IsType<PresentTargets>(
                await _presentations.Reader.ReadAsync(_timeout.Token));

        public async ValueTask DisposeAsync()
        {
            await _system.Terminate();
            _timeout.Dispose();
        }

        private KanataFrameSequence NextSequence() =>
            KanataFrameSequence.From(++_sequence);

        private static Channel<object> CreateRecorderChannel() =>
            Channel.CreateBounded<object>(
                new BoundedChannelOptions(8)
                {
                    SingleReader = true,
                    SingleWriter = true,
                });
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
                        "The lifecycle recorder rejected a message.");
                }
            });
        }

        protected override void PostStop() => _writer.TryComplete();
    }
}
