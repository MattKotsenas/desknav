namespace Desknav.ControlPlane;

public sealed record PointAtTarget;

public static class PointerUiCommands
{
    public sealed record ExecutePointAtTarget(ActionId ActionId);
}

public sealed record PointAtTargetExecuted(ActionId ActionId);

public sealed record PointAtTargetCancelled(ActionId ActionId);

public static class KanataCommands
{
    public sealed record RestoreBaseLayer;
}

public sealed record BaseLayerActive;

public abstract record PointAtTargetResult;

public sealed record PointAtTargetAlreadyActive : PointAtTargetResult;

public sealed record PointAtTargetCompleted(
    ActionId ActionId,
    PointAtTargetOutcome Outcome)
    : PointAtTargetResult;

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