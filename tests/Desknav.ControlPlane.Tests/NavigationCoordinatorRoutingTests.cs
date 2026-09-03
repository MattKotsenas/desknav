using Akka.Actor;
using Akka.Event;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class NavigationCoordinatorRoutingTests
{
    [Fact]
    public async Task RoutesPresentationRevisionsAndDiscoveryFailure()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var targetDiscovery = RecordingActor.CreateChannel(2);
        var presentations = RecordingActor.CreateChannel(2);
        var unhandledMessages = RecordingActor.CreateChannel(1);
        var system = ActorSystem.Create(
            $"navigation-routing-{Guid.NewGuid():N}");

        try
        {
            var overlayOwner = system.ActorOf(
                Props.Create(
                    () => new RecordingActor(presentations.Writer)));
            var unhandledObserver = system.ActorOf(
                RecordingActor.CreateProps(unhandledMessages.Writer));
            system.EventStream.Subscribe(
                unhandledObserver,
                typeof(UnhandledMessage));
            var coordinator = system.ActorOf(
                NavigationCoordinator.CreateProps(
                    RecordingActor.CreateProps(
                        targetDiscovery.Writer),
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
                    new TargetSnapshot(discovery.RequestId, [])));

            var presentation = Assert.IsType<PresentTargets>(
                await presentations.Reader.ReadAsync(timeout.Token));
            Assert.Equal(
                discovery.RequestId,
                presentation.Snapshot.RequestId);
            Assert.Equal(
                PresentationRevision.From(1),
                presentation.Revision);

            coordinator.Tell(new TargetsPresented(presentation.Revision));
            await ActorTestHelpers.FlushAsync(coordinator, timeout.Token);
            await ActorTestHelpers.FlushAsync(
                unhandledObserver,
                timeout.Token);
            Assert.False(unhandledMessages.Reader.TryRead(out _));

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
                    new TargetSnapshot(nextDiscovery.RequestId, [])));
            var nextPresentation = Assert.IsType<PresentTargets>(
                await presentations.Reader.ReadAsync(timeout.Token));
            Assert.Equal(
                nextDiscovery.RequestId,
                nextPresentation.Snapshot.RequestId);
            Assert.Equal(
                PresentationRevision.From(2),
                nextPresentation.Revision);

            coordinator.Tell(
                new GestureObserved(
                    connectionId,
                    KanataFrameSequence.From(5),
                    new GestureToken("command", "spc")));
            coordinator.Tell(
                new GestureObserved(
                    connectionId,
                    KanataFrameSequence.From(6),
                    new GestureToken("pointer", "f")));
            var failedDiscovery = Assert.IsType<DiscoverTargets>(
                await targetDiscovery.Reader.ReadAsync(timeout.Token));
            coordinator.Tell(
                new TargetDiscoveryFailed(failedDiscovery.RequestId));

            coordinator.Tell(
                new GestureObserved(
                    connectionId,
                    KanataFrameSequence.From(7),
                    new GestureToken("command", "spc")));
            coordinator.Tell(
                new GestureObserved(
                    connectionId,
                    KanataFrameSequence.From(8),
                    new GestureToken("pointer", "f")));
            Assert.IsType<DiscoverTargets>(
                await targetDiscovery.Reader.ReadAsync(timeout.Token));

            await ActorTestHelpers.PoisonTargetDiscoveryAsync(
                system,
                coordinator,
                timeout.Token);
            overlayOwner.Tell(PoisonPill.Instance);
            unhandledObserver.Tell(PoisonPill.Instance);
            await targetDiscovery.Reader.Completion.WaitAsync(timeout.Token);
            await presentations.Reader.Completion.WaitAsync(timeout.Token);
            await unhandledMessages.Reader.Completion.WaitAsync(timeout.Token);
        }
        finally
        {
            await system.Terminate();
        }
    }
}
