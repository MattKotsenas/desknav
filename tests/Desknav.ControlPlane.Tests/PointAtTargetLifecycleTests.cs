namespace Desknav.ControlPlane.Tests;

public sealed class PointAtTargetLifecycleTests
{
    [Fact]
    public void StaleResultLeavesTheCurrentActionUnchanged()
    {
        var lifecycle = PointAtTargetLifecycle.Initial;
        Assert.IsType<IdlePointAtTarget>(lifecycle.Phase);

        var firstStart = lifecycle.Start();
        var firstExecute =
            Assert.IsType<ExecutePointAtTargetEffect>(firstStart.Effect);
        var firstResult = firstStart.Lifecycle.Pointed(firstExecute.ActionId);
        _ = Assert.IsType<RestoreBaseLayerEffect>(firstResult.Effect);
        var firstCompletion = firstResult.Lifecycle.BaseLayerActive();
        _ = Assert.IsType<CompletePointAtTargetEffect>(
            firstCompletion.Effect);

        var secondStart = firstCompletion.Lifecycle.Start();
        var secondExecute =
            Assert.IsType<ExecutePointAtTargetEffect>(secondStart.Effect);
        Assert.NotEqual(firstExecute.ActionId, secondExecute.ActionId);
        var beforeStaleResult = secondStart.Lifecycle;

        var staleResult =
            beforeStaleResult.Cancelled(firstExecute.ActionId);

        Assert.Equal(beforeStaleResult, staleResult.Lifecycle);
        Assert.Null(staleResult.Effect);

        var currentResult =
            staleResult.Lifecycle.Pointed(secondExecute.ActionId);
        _ = Assert.IsType<RestoreBaseLayerEffect>(currentResult.Effect);
        var restoring =
            Assert.IsType<RestoringPointAtTarget>(
                currentResult.Lifecycle.Phase);
        Assert.Equal(secondExecute.ActionId, restoring.ActionId);
        Assert.Equal(PointAtTargetOutcome.Pointed, restoring.Outcome);
    }
}