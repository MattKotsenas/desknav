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
                    TargetDiscoveryActor.CreateProps(
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

            var currentTargets = new[]
            {
                new DesktopTarget(
                    TargetId.New(),
                    new TargetBounds(-1200, 40, 640, 480)),
                new DesktopTarget(
                    TargetId.New(),
                    new TargetBounds(100, 200, 800, 600)),
            };
            current.Complete(currentTargets);

            var presentation = Assert.IsType<ApplyTargetPresentation>(
                await presentations.Reader.ReadAsync(timeout.Token));
            var visible = Assert.IsType<TargetPresentation.Visible>(
                presentation.Presentation);
            Assert.Equal(
                PresentationRevision.From(1),
                presentation.Revision);
            Assert.Equal(
                currentTargets,
                visible.Map.Targets
                    .Select(static target => target.Target));

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
    public async Task OwnerConstructionFailureTerminatesActorSystem()
    {
        using var timeout =
            new CancellationTokenSource(TestTimeout);
        var system = ActorSystem.Create(
            $"target-discovery-construction-failure-{Guid.NewGuid():N}");

        try
        {
            system.ActorOf(
                NavigationCoordinator.CreateProps(
                    Props.Create(
                        () => new FailingConstructionActor()),
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
                    Props.Create(() => new FailingActor()),
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

    private sealed class FailingConstructionActor : ReceiveActor
    {
        public FailingConstructionActor() =>
            throw new InvalidOperationException(
                "Target discovery owner construction failed.");
    }
}