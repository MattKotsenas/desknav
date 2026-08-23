namespace Desknav.ControlPlane;

public sealed record PointAtTarget;

public sealed record ExecutePointAtTarget(ActionId ActionId);

public sealed record PointAtTargetExecuted(ActionId ActionId);

public sealed record PointAtTargetCancelled(ActionId ActionId);

public sealed record RestoreBaseLayer;

public sealed record BaseLayerActive;

public sealed record PointAtTargetBusy;

public sealed record PointAtTargetCompleted(
    ActionId ActionId,
    PointAtTargetOutcome Outcome);

public enum PointAtTargetOutcome
{
    Pointed,
    Cancelled,
}

public readonly record struct ActionId
{
    public ActionId(long value)
    {
        Value = Validate(value);
    }

    public long Value { get; }

    internal void EnsureValid() => _ = Validate(Value);

    private static long Validate(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}