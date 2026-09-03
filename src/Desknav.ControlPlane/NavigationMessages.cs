using System.Collections.Immutable;

using Vogen;

namespace Desknav.ControlPlane;

public sealed record CommandInputObserved(GestureToken Token);

public sealed record CommandSessionEnded;

[ValueObject<Guid>(conversions: Conversions.None)]
public readonly partial struct TargetDiscoveryRequestId
{
    public static TargetDiscoveryRequestId New() => From(Guid.NewGuid());

    private static Validation Validate(Guid value) =>
        value == Guid.Empty
            ? Validation.Invalid(
                "A target discovery request ID cannot be empty.")
            : Validation.Ok;
}

[ValueObject<long>(conversions: Conversions.None)]
public readonly partial struct PresentationRevision
{
    private static Validation Validate(long value) =>
        value <= 0
            ? Validation.Invalid("A presentation revision must be positive.")
            : Validation.Ok;
}

[ValueObject<Guid>(conversions: Conversions.None)]
public readonly partial struct TargetId
{
    public static TargetId New() => From(Guid.NewGuid());

    private static Validation Validate(Guid value) =>
        value == Guid.Empty
            ? Validation.Invalid("A target ID cannot be empty.")
            : Validation.Ok;
}

public readonly record struct TargetBounds
{
    public TargetBounds(int left, int top, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }
}

public sealed record DesktopTarget
{
    public DesktopTarget(TargetId id, TargetBounds bounds)
    {
        if (bounds == default)
        {
            throw new ArgumentException(
                "Target bounds must be initialized.",
                nameof(bounds));
        }

        Id = id;
        Bounds = bounds;
    }

    public TargetId Id { get; }

    public TargetBounds Bounds { get; }
}

public sealed record DiscoverTargets(TargetDiscoveryRequestId RequestId);

public sealed record CancelTargetDiscovery(
    TargetDiscoveryRequestId RequestId);

public sealed record TargetSnapshot(
    TargetDiscoveryRequestId RequestId,
    ImmutableArray<DesktopTarget> Targets);

public sealed record TargetDiscoveryCompleted(TargetSnapshot Snapshot);

public sealed record TargetDiscoveryFailed(
    TargetDiscoveryRequestId RequestId);

public sealed record PresentTargets(
    PresentationRevision Revision,
    TargetSnapshot Snapshot);
