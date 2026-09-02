using Akka.Actor;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class NavigationCoordinatorTargetDiscoveryTests
{
    private static readonly TimeSpan TestTimeout =
        TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PresentsOnlyCurrentSuccessfulDiscovery()
    {
        using var timeout =
            new CancellationTokenSource(TestTimeout);
        var presentations = RecordingActor.CreateChannel(2);
        var discovery = new ControllableTargetDiscovery(timeout.Token);
        var system = ActorSystem.Create(
            $"navigation-target-discovery-{Guid.NewGuid():N}");

        try
        {
            var overlayOwner = system.ActorOf(
                Props.Create(
                    () => new RecordingActor(presentations.Writer)));
            var coordinator = system.ActorOf(
                NavigationCoordinator.CreateProps(
                    owner => TargetDiscoveryActor.CreateProps(
                        owner,
                        discovery,
                        TimeSpan.FromHours(1)),
                    ActorRefs.Nobody,
                    overlayOwner));
            var targetDiscoveryOwner =
                await ActorTestHelpers
                    .ResolveTargetDiscoveryAsync(
                        system,
                        coordinator,
                        timeout.Token);
            var connectionId = KanataConnectionId.New();

            ObserveTargetCommand(coordinator, connectionId, 1);
            var obsolete = await discovery.ReadStartedCallAsync();

            ObserveTargetCommand(coordinator, connectionId, 3);
            var (obsoleteCancellation, superseded) =
                await discovery.ReadCancellationAndStartAsync();
            Assert.Same(obsolete, obsoleteCancellation.Call);

            obsolete.Complete();
            ObserveTargetCommand(coordinator, connectionId, 5);
            var (supersededCancellation, current) =
                await discovery.ReadCancellationAndStartAsync();
            Assert.Same(superseded, supersededCancellation.Call);
            await ActorTestHelpers.FlushAsync(
                targetDiscoveryOwner,
                timeout.Token);
            await ActorTestHelpers.FlushAsync(
                coordinator,
                timeout.Token);
            await ActorTestHelpers.FlushAsync(
                overlayOwner,
                timeout.Token);
            Assert.False(presentations.Reader.TryRead(out _));

            current.Complete();

            var presentation = Assert.IsType<PresentTargets>(
                await presentations.Reader.ReadAsync(timeout.Token));
            Assert.Equal(
                PresentationRevision.From(1),
                presentation.Revision);

            overlayOwner.Tell(PoisonPill.Instance);
            await presentations.Reader.Completion.WaitAsync(timeout.Token);
            superseded.Complete();
        }
        finally
        {
            await TerminateAsync(system);
        }
    }

    [Fact]
    public async Task PropsFailureTerminatesActorSystem()
    {
        using var timeout =
            new CancellationTokenSource(TestTimeout);
        var system = ActorSystem.Create(
            $"target-discovery-props-failure-{Guid.NewGuid():N}");

        try
        {
            system.ActorOf(
                NavigationCoordinator.CreateProps(
                    _ => throw new InvalidOperationException(
                        "Target discovery props failed."),
                    ActorRefs.Nobody,
                    ActorRefs.Nobody));

            await system.WhenTerminated.WaitAsync(timeout.Token);
        }
        finally
        {
            await TerminateAsync(system);
        }
    }

    [Fact]
    public async Task OwnerFailureTerminatesActorSystem()
    {
        using var timeout =
            new CancellationTokenSource(TestTimeout);
        var system = ActorSystem.Create(
            $"target-discovery-owner-failure-{Guid.NewGuid():N}");

        try
        {
            system.ActorOf(
                NavigationCoordinator.CreateProps(
                    _ => Props.Create(() => new FailingActor()),
                    ActorRefs.Nobody,
                    ActorRefs.Nobody));

            await system.WhenTerminated.WaitAsync(timeout.Token);
        }
        finally
        {
            await TerminateAsync(system);
        }
    }

    private static async Task TerminateAsync(ActorSystem system)
    {
        using var timeout =
            new CancellationTokenSource(TestTimeout);
        await system.Terminate().WaitAsync(timeout.Token);
    }

    private static void ObserveTargetCommand(
        IActorRef coordinator,
        KanataConnectionId connectionId,
        long firstSequence)
    {
        coordinator.Tell(
            new GestureObserved(
                connectionId,
                KanataFrameSequence.From(firstSequence),
                new GestureToken("command", "spc")));
        coordinator.Tell(
            new GestureObserved(
                connectionId,
                KanataFrameSequence.From(firstSequence + 1),
                new GestureToken("pointer", "f")));
    }

    private sealed class FailingActor : ReceiveActor
    {
        public FailingActor()
        {
            ReceiveAny(
                _ => throw new InvalidOperationException(
                    "Target discovery owner failed."));
        }

        protected override void PreStart() =>
            Self.Tell(new object());
    }
}
