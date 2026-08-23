namespace Desknav.ControlPlane;

internal sealed record PointAtTargetLifecycle(
    long LastActionNumber,
    PointAtTargetPhase Phase)
{
    public static PointAtTargetLifecycle Initial { get; } =
        new(0, new IdlePointAtTarget());

    public PointAtTargetTransition Start()
    {
        if (Phase is not IdlePointAtTarget)
        {
            return new(this, new RefusePointAtTargetBusyEffect());
        }

        var actionId = new ActionId(checked(LastActionNumber + 1));
        return new(
            this with
            {
                LastActionNumber = actionId.Value,
                Phase = new AwaitingPointAtTargetResult(actionId),
            },
            new ExecutePointAtTargetEffect(actionId));
    }

    public PointAtTargetTransition Pointed(ActionId actionId) =>
        FinishSelection(actionId, PointAtTargetOutcome.Pointed);

    public PointAtTargetTransition Cancelled(ActionId actionId) =>
        FinishSelection(actionId, PointAtTargetOutcome.Cancelled);

    public PointAtTargetTransition BaseLayerActive() =>
        Phase is RestoringPointAtTarget restoring
            ? new(
                this with { Phase = new IdlePointAtTarget() },
                new CompletePointAtTargetEffect(
                    restoring.ActionId,
                    restoring.Outcome))
            : new(this, null);

    private PointAtTargetTransition FinishSelection(
        ActionId actionId,
        PointAtTargetOutcome outcome) =>
        Phase is AwaitingPointAtTargetResult awaiting
            && awaiting.ActionId == actionId
                ? new(
                    this with
                    {
                        Phase = new RestoringPointAtTarget(
                            actionId,
                            outcome),
                    },
                    new RestoreBaseLayerEffect())
                : new(this, null);
}

internal sealed record PointAtTargetTransition(
    PointAtTargetLifecycle Lifecycle,
    PointAtTargetEffect? Effect);

internal abstract record PointAtTargetPhase;

internal sealed record IdlePointAtTarget : PointAtTargetPhase;

internal sealed record AwaitingPointAtTargetResult(ActionId ActionId)
    : PointAtTargetPhase;

internal sealed record RestoringPointAtTarget(
    ActionId ActionId,
    PointAtTargetOutcome Outcome)
    : PointAtTargetPhase;

internal abstract record PointAtTargetEffect;

internal sealed record ExecutePointAtTargetEffect(ActionId ActionId)
    : PointAtTargetEffect;

internal sealed record RefusePointAtTargetBusyEffect
    : PointAtTargetEffect;

internal sealed record RestoreBaseLayerEffect : PointAtTargetEffect;

internal sealed record CompletePointAtTargetEffect(
    ActionId ActionId,
    PointAtTargetOutcome Outcome)
    : PointAtTargetEffect;