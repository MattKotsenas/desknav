using System.Threading.Channels;

using Akka.Actor;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class NavigationCoordinatorRoutingTests
{
    [Fact]
    public async Task PresentationRevisionsAdvanceAcrossDiscoveryWorkflows()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var targetDiscovery = CreateRecorderChannel();
        var presentations = CreateRecorderChannel();
        var system = ActorSystem.Create(
            $"navigation-routing-{Guid.NewGuid():N}");

        try
        {
            var targetDiscoveryActor = system.ActorOf(
                Props.Create(
                    () => new RecordingActor(targetDiscovery.Writer)));
            var overlayOwner = system.ActorOf(
                Props.Create(
                    () => new RecordingActor(presentations.Writer)));
            var coordinator = system.ActorOf(
                NavigationCoordinator.CreateProps(
                    targetDiscoveryActor,
                    ActorRefs.Nobody,
                    overlayOwner));
            var connectionId = KanataConnectionId.New();

            coordinator.Tell(
                new GestureObserved(
                    connectionId,
                    KanataFrameSequence.From(1),
                    new GestureToken("command", "spc")));
            coordinator.Tell(
                new GestureObserved(
                    connectionId,
                    KanataFrameSequence.From(2),
                    new GestureToken("pointer", "f")));

            var discovery = Assert.IsType<DiscoverTargets>(
                await targetDiscovery.Reader.ReadAsync(timeout.Token));
            coordinator.Tell(
                new TargetDiscoveryCompleted(
                    new TargetSnapshot(discovery.RequestId)));

            var presentation = Assert.IsType<PresentTargets>(
                await presentations.Reader.ReadAsync(timeout.Token));
            Assert.Equal(
                discovery.RequestId,
                presentation.Snapshot.RequestId);
            Assert.Equal(
                PresentationRevision.From(1),
                presentation.Revision);

            coordinator.Tell(
                new GestureObserved(
                    connectionId,
                    KanataFrameSequence.From(3),
                    new GestureToken("command", "spc")));
            coordinator.Tell(
                new GestureObserved(
                    connectionId,
                    KanataFrameSequence.From(4),
                    new GestureToken("pointer", "f")));
            var nextDiscovery = Assert.IsType<DiscoverTargets>(
                await targetDiscovery.Reader.ReadAsync(timeout.Token));
            Assert.NotEqual(discovery.RequestId, nextDiscovery.RequestId);

            coordinator.Tell(
                new TargetDiscoveryCompleted(
                    new TargetSnapshot(nextDiscovery.RequestId)));
            var nextPresentation = Assert.IsType<PresentTargets>(
                await presentations.Reader.ReadAsync(timeout.Token));
            Assert.Equal(
                nextDiscovery.RequestId,
                nextPresentation.Snapshot.RequestId);
            Assert.Equal(
                PresentationRevision.From(2),
                nextPresentation.Revision);

            targetDiscoveryActor.Tell(PoisonPill.Instance);
            overlayOwner.Tell(PoisonPill.Instance);
            await targetDiscovery.Reader.Completion.WaitAsync(timeout.Token);
            await presentations.Reader.Completion.WaitAsync(timeout.Token);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static Channel<object> CreateRecorderChannel() =>
        Channel.CreateBounded<object>(
            new BoundedChannelOptions(2)
            {
                SingleReader = true,
                SingleWriter = true,
            });

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
                        "The routing recorder rejected a message.");
                }
            });
        }

        protected override void PostStop() => _writer.TryComplete();
    }
}
