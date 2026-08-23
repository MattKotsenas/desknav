namespace Desknav.ControlPlane;

/// <summary>
/// Defines the pure policy for one PointAtTarget action at a time.
/// </summary>
/// <remarks>
/// Methods accept domain events rather than requested states. Each event
/// returns the next immutable lifecycle and at most one boundary effect.
/// Starting while an action is active leaves the state unchanged and returns
/// an explicit rejection; stale boundary observations leave the state
/// unchanged without an effect. States and effects are nested so this
/// vocabulary remains scoped to the policy that owns it.
/// </remarks>
internal sealed record PointAtTargetLifecycle(
    long LastActionNumber,
    PointAtTargetLifecycle.State Phase)
{
    public static PointAtTargetLifecycle Initial { get; } =
        new(0, new States.Idle());

    public PointAtTargetTransition Start()
    {
        if (Phase is not States.Idle)
        {
            return new(this, new Effects.RejectAlreadyActive());
        }

        var actionId = new ActionId(checked(LastActionNumber + 1));
        return new(
            this with
            {
                LastActionNumber = actionId.Value,
                Phase = new States.AwaitingResult(actionId),
            },
            new Effects.ExecutePointAtTarget(actionId));
    }

    public PointAtTargetTransition Pointed(ActionId actionId) =>
        FinishSelection(actionId, PointAtTargetOutcome.Pointed);

    public PointAtTargetTransition Cancelled(ActionId actionId) =>
        FinishSelection(actionId, PointAtTargetOutcome.Cancelled);

    public PointAtTargetTransition BaseLayerActive() =>
        Phase is States.RestoringBaseLayer restoring
            ? new(
                this with { Phase = new States.Idle() },
                new Effects.Complete(
                    restoring.ActionId,
                    restoring.Outcome))
            : new(this, null);

    private PointAtTargetTransition FinishSelection(
        ActionId actionId,
        PointAtTargetOutcome outcome) =>
        Phase is States.AwaitingResult awaiting
            && awaiting.ActionId == actionId
                ? new(
                    this with
                    {
                        Phase = new States.RestoringBaseLayer(
                            actionId,
                            outcome),
                    },
                    new Effects.RestoreBaseLayer())
                : new(this, null);

    internal abstract record State;

    internal static class States
    {
        internal sealed record Idle : State;

        internal sealed record AwaitingResult(ActionId ActionId)
            : State;

        internal sealed record RestoringBaseLayer(
            ActionId ActionId,
            PointAtTargetOutcome Outcome)
            : State;
    }

    internal abstract record Effect;

    internal static class Effects
    {
        internal sealed record ExecutePointAtTarget(ActionId ActionId)
            : Effect;

        internal sealed record RejectAlreadyActive : Effect;

        internal sealed record RestoreBaseLayer : Effect;

        internal sealed record Complete(
            ActionId ActionId,
            PointAtTargetOutcome Outcome)
            : Effect;
    }
}

internal sealed record PointAtTargetTransition(
    PointAtTargetLifecycle Lifecycle,
    PointAtTargetLifecycle.Effect? Effect);