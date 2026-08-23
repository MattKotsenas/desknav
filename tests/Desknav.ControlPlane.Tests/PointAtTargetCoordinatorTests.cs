using Akka.Actor;
using Akka.Pattern;

namespace Desknav.ControlPlane.Tests;

public sealed class PointAtTargetCoordinatorTests
{
    [Fact]
    public async Task ReportsAlreadyActiveToASecondRequest()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;

        var firstResult = harness.Coordinator.Ask<PointAtTargetResult>(
            new PointAtTarget(),
            cancellationToken);
        var first =
            await harness.PointerUi.ExpectMsgAsync<
                PointerUiCommands.ExecutePointAtTarget>(
                cancellationToken: cancellationToken);

        var secondResult = await harness.Coordinator.Ask<PointAtTargetResult>(
            new PointAtTarget(),
            cancellationToken);

        _ = Assert.IsType<PointAtTargetAlreadyActive>(secondResult);
        Assert.False(firstResult.IsCompleted);

        harness.Coordinator.Tell(
            new PointAtTargetCancelled(first.ActionId),
            harness.PointerUi.Ref);
        _ = await harness.Kanata.ExpectMsgAsync<
            KanataCommands.RestoreBaseLayer>(
                cancellationToken: cancellationToken);
        harness.Coordinator.Tell(new BaseLayerActive(), harness.Kanata.Ref);
        _ = Assert.IsType<PointAtTargetCompleted>(await firstResult);
    }

    [Fact]
    public async Task PointedOutcomeCompletesOnlyAfterBaseLayerIsActive()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = harness.Coordinator.Ask<PointAtTargetResult>(
            new PointAtTarget(),
            cancellationToken);
        var execute =
            await harness.PointerUi.ExpectMsgAsync<
                PointerUiCommands.ExecutePointAtTarget>(
                    cancellationToken: cancellationToken);

        harness.Coordinator.Tell(
            new PointAtTargetExecuted(execute.ActionId),
            harness.PointerUi.Ref);

        _ = await harness.Kanata.ExpectMsgAsync<
            KanataCommands.RestoreBaseLayer>(
                cancellationToken: cancellationToken);
        Assert.False(result.IsCompleted);

        harness.Coordinator.Tell(new BaseLayerActive(), harness.Kanata.Ref);

        var completed = Assert.IsType<PointAtTargetCompleted>(await result);
        Assert.Equal(execute.ActionId, completed.ActionId);
        Assert.Equal(PointAtTargetOutcome.Pointed, completed.Outcome);
    }

    [Fact]
    public async Task CancellationCompletesOnlyAfterBaseLayerIsActive()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = harness.Coordinator.Ask<PointAtTargetResult>(
            new PointAtTarget(),
            cancellationToken);
        var execute =
            await harness.PointerUi.ExpectMsgAsync<
                PointerUiCommands.ExecutePointAtTarget>(
                    cancellationToken: cancellationToken);

        harness.Coordinator.Tell(
            new PointAtTargetCancelled(execute.ActionId),
            harness.PointerUi.Ref);

        _ = await harness.Kanata.ExpectMsgAsync<
            KanataCommands.RestoreBaseLayer>(
                cancellationToken: cancellationToken);
        Assert.False(result.IsCompleted);

        harness.Coordinator.Tell(new BaseLayerActive(), harness.Kanata.Ref);

        var completed = Assert.IsType<PointAtTargetCompleted>(await result);
        Assert.Equal(execute.ActionId, completed.ActionId);
        Assert.Equal(PointAtTargetOutcome.Cancelled, completed.Outcome);
    }

    [Fact]
    public async Task StaleResultCannotCompleteTheNextAction()
    {
        await using var harness = CoordinatorHarness.Create();
        var cancellationToken = TestContext.Current.CancellationToken;

        var firstResult = harness.Coordinator.Ask<PointAtTargetResult>(
            new PointAtTarget(),
            cancellationToken);
        var first =
            await harness.PointerUi.ExpectMsgAsync<
                PointerUiCommands.ExecutePointAtTarget>(
                    cancellationToken: cancellationToken);
        harness.Coordinator.Tell(
            new PointAtTargetExecuted(first.ActionId),
            harness.PointerUi.Ref);
        _ = await harness.Kanata.ExpectMsgAsync<
            KanataCommands.RestoreBaseLayer>(
                cancellationToken: cancellationToken);
        harness.Coordinator.Tell(new BaseLayerActive(), harness.Kanata.Ref);
        _ = Assert.IsType<PointAtTargetCompleted>(await firstResult);

        var secondResult = harness.Coordinator.Ask<PointAtTargetResult>(
            new PointAtTarget(),
            cancellationToken);
        var second =
            await harness.PointerUi.ExpectMsgAsync<
                PointerUiCommands.ExecutePointAtTarget>(
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

        _ = await harness.PointerUi.ExpectMsgAsync<PointAtTargetAlreadyActive>(
            cancellationToken: cancellationToken);
        _ = await harness.Kanata.ExpectMsgAsync<
            KanataCommands.RestoreBaseLayer>(
                cancellationToken: cancellationToken);
        await harness.Kanata.ExpectNoMsgAsync(
            TimeSpan.Zero,
            cancellationToken);

        harness.Coordinator.Tell(new BaseLayerActive(), harness.Kanata.Ref);

        var completed =
            Assert.IsType<PointAtTargetCompleted>(await secondResult);
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
        _ = await harness.PointerUi.ExpectMsgAsync<
            PointerUiCommands.ExecutePointAtTarget>(
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
        _ = await harness.PointerUi.ExpectMsgAsync<
            PointerUiCommands.ExecutePointAtTarget>(
                cancellationToken: cancellationToken);

        harness.Coordinator.Tell(Kill.Instance);

        await harness.ActorSystem.WhenTerminated.WaitAsync(
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }
}