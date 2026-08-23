using Akka.Actor;

namespace Desknav.ControlPlane.Tests;

public sealed class PointAtTargetCoordinatorTests
{
    [Fact]
    public async Task RefusesASecondActionWhileOneIsActive()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstRequester = harness.CreateProbe("first-requester");
        var secondRequester = harness.CreateProbe("second-requester");

        harness.Coordinator.Tell(new PointAtTarget(), firstRequester.Ref);
        _ = await harness.PointerUi.ExpectMsgAsync<ExecutePointAtTarget>(
            cancellationToken: cancellationToken);

        harness.Coordinator.Tell(new PointAtTarget(), secondRequester.Ref);

        _ = await secondRequester.ExpectMsgAsync<PointAtTargetBusy>(
            cancellationToken: cancellationToken);
        await firstRequester.ExpectNoMsgAsync(
            TimeSpan.Zero,
            cancellationToken);
    }

    [Fact]
    public async Task PointedOutcomeCompletesOnlyAfterBaseLayerIsActive()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var requester = harness.CreateProbe("requester");

        harness.Coordinator.Tell(new PointAtTarget(), requester.Ref);
        var execute =
            await harness.PointerUi.ExpectMsgAsync<ExecutePointAtTarget>(
                cancellationToken: cancellationToken);

        harness.Coordinator.Tell(
            new PointAtTargetExecuted(execute.ActionId),
            harness.PointerUi.Ref);

        _ = await harness.Kanata.ExpectMsgAsync<RestoreBaseLayer>(
            cancellationToken: cancellationToken);
        await requester.ExpectNoMsgAsync(TimeSpan.Zero, cancellationToken);

        harness.Coordinator.Tell(new BaseLayerActive(), harness.Kanata.Ref);

        var completed =
            await requester.ExpectMsgAsync<PointAtTargetCompleted>(
                cancellationToken: cancellationToken);
        Assert.Equal(execute.ActionId, completed.ActionId);
        Assert.Equal(PointAtTargetOutcome.Pointed, completed.Outcome);
    }

    [Fact]
    public async Task CancellationCompletesOnlyAfterBaseLayerIsActive()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var requester = harness.CreateProbe("requester");

        harness.Coordinator.Tell(new PointAtTarget(), requester.Ref);
        var execute =
            await harness.PointerUi.ExpectMsgAsync<ExecutePointAtTarget>(
                cancellationToken: cancellationToken);

        harness.Coordinator.Tell(
            new PointAtTargetCancelled(execute.ActionId),
            harness.PointerUi.Ref);

        _ = await harness.Kanata.ExpectMsgAsync<RestoreBaseLayer>(
            cancellationToken: cancellationToken);
        await requester.ExpectNoMsgAsync(TimeSpan.Zero, cancellationToken);

        harness.Coordinator.Tell(new BaseLayerActive(), harness.Kanata.Ref);

        var completed =
            await requester.ExpectMsgAsync<PointAtTargetCompleted>(
                cancellationToken: cancellationToken);
        Assert.Equal(execute.ActionId, completed.ActionId);
        Assert.Equal(PointAtTargetOutcome.Cancelled, completed.Outcome);
    }

    [Fact]
    public async Task StaleResultCannotCompleteTheNextAction()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstRequester = harness.CreateProbe("first-requester");
        var secondRequester = harness.CreateProbe("second-requester");

        harness.Coordinator.Tell(new PointAtTarget(), firstRequester.Ref);
        var first =
            await harness.PointerUi.ExpectMsgAsync<ExecutePointAtTarget>(
                cancellationToken: cancellationToken);
        harness.Coordinator.Tell(
            new PointAtTargetExecuted(first.ActionId),
            harness.PointerUi.Ref);
        _ = await harness.Kanata.ExpectMsgAsync<RestoreBaseLayer>(
            cancellationToken: cancellationToken);
        harness.Coordinator.Tell(new BaseLayerActive(), harness.Kanata.Ref);
        _ = await firstRequester.ExpectMsgAsync<PointAtTargetCompleted>(
            cancellationToken: cancellationToken);

        harness.Coordinator.Tell(new PointAtTarget(), secondRequester.Ref);
        var second =
            await harness.PointerUi.ExpectMsgAsync<ExecutePointAtTarget>(
                cancellationToken: cancellationToken);
        Assert.NotEqual(first.ActionId, second.ActionId);

        harness.Coordinator.Tell(
            new PointAtTargetCancelled(first.ActionId),
            harness.PointerUi.Ref);
        harness.Coordinator.Tell(
            new PointAtTargetExecuted(second.ActionId),
            harness.PointerUi.Ref);
        harness.Coordinator.Tell(
            new PointAtTarget(),
            harness.PointerUi.Ref);

        _ = await harness.PointerUi.ExpectMsgAsync<PointAtTargetBusy>(
            cancellationToken: cancellationToken);
        _ = await harness.Kanata.ExpectMsgAsync<RestoreBaseLayer>(
            cancellationToken: cancellationToken);
        await harness.Kanata.ExpectNoMsgAsync(
            TimeSpan.Zero,
            cancellationToken);

        harness.Coordinator.Tell(new BaseLayerActive(), harness.Kanata.Ref);

        var completed =
            await secondRequester.ExpectMsgAsync<PointAtTargetCompleted>(
                cancellationToken: cancellationToken);
        Assert.Equal(second.ActionId, completed.ActionId);
        Assert.Equal(PointAtTargetOutcome.Pointed, completed.Outcome);
    }

    [Fact]
    public async Task RestartDirectedFailureStopsTheActorSystem()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var requester = harness.CreateProbe("requester");

        harness.Coordinator.Tell(new PointAtTarget(), requester.Ref);
        _ = await harness.PointerUi.ExpectMsgAsync<ExecutePointAtTarget>(
            cancellationToken: cancellationToken);

        harness.Coordinator.Tell(
            new PointAtTargetExecuted(default),
            harness.PointerUi.Ref);

        await harness.ActorSystem.WhenTerminated.WaitAsync(
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }

    [Fact]
    public async Task StopDirectedFailureStopsTheActorSystem()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        var requester = harness.CreateProbe("requester");

        harness.Coordinator.Tell(new PointAtTarget(), requester.Ref);
        _ = await harness.PointerUi.ExpectMsgAsync<ExecutePointAtTarget>(
            cancellationToken: cancellationToken);

        harness.Coordinator.Tell(Kill.Instance);

        await harness.ActorSystem.WhenTerminated.WaitAsync(
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }
}