namespace Desknav.ControlPlane.Tests;

public sealed class PointAtTargetLifecycleTests
{
    [Fact]
    public void StartWhileActiveRejectsWithoutChangingState()
    {
        var active = PointAtTargetLifecycle.Initial.Start().Lifecycle;
        Assert.IsType<PointAtTargetLifecycle.States.AwaitingResult>(
            active.Phase);

        var overlap = active.Start();

        Assert.Same(active, overlap.Lifecycle);
        _ = Assert.IsType<
            PointAtTargetLifecycle.Effects.RejectAlreadyActive>(
                overlap.Effect);
    }

    [Fact]
    public void StaleResultLeavesTheCurrentActionUnchanged()
    {
        var lifecycle = PointAtTargetLifecycle.Initial;
        Assert.IsType<PointAtTargetLifecycle.States.Idle>(lifecycle.Phase);

        var firstStart = lifecycle.Start();
        var firstExecute =
            Assert.IsType<PointAtTargetLifecycle.Effects.ExecutePointAtTarget>(
                firstStart.Effect);
        var firstResult = firstStart.Lifecycle.Pointed(firstExecute.ActionId);
        _ = Assert.IsType<PointAtTargetLifecycle.Effects.RestoreBaseLayer>(
            firstResult.Effect);
        var firstCompletion = firstResult.Lifecycle.BaseLayerActive();
        _ = Assert.IsType<PointAtTargetLifecycle.Effects.Complete>(
            firstCompletion.Effect);

        var secondStart = firstCompletion.Lifecycle.Start();
        var secondExecute =
            Assert.IsType<PointAtTargetLifecycle.Effects.ExecutePointAtTarget>(
                secondStart.Effect);
        Assert.NotEqual(firstExecute.ActionId, secondExecute.ActionId);
        var beforeStaleResult = secondStart.Lifecycle;

        var staleResult =
            beforeStaleResult.Cancelled(firstExecute.ActionId);

        Assert.Equal(beforeStaleResult, staleResult.Lifecycle);
        Assert.Null(staleResult.Effect);

        var currentResult =
            staleResult.Lifecycle.Pointed(secondExecute.ActionId);
        _ = Assert.IsType<PointAtTargetLifecycle.Effects.RestoreBaseLayer>(
            currentResult.Effect);
        var restoring =
            Assert.IsType<PointAtTargetLifecycle.States.RestoringBaseLayer>(
                currentResult.Lifecycle.Phase);
        Assert.Equal(secondExecute.ActionId, restoring.ActionId);
        Assert.Equal(PointAtTargetOutcome.Pointed, restoring.Outcome);
    }
}