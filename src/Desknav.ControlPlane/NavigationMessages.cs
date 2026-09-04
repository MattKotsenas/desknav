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

/// <summary>
/// Distinguishes observed targets from an expected inability to enumerate
/// without turning either into an exception.
/// </summary>
public abstract record TargetDiscoveryResult
{
    private TargetDiscoveryResult()
    {
    }

    public sealed record Succeeded : TargetDiscoveryResult
    {
        public Succeeded(ImmutableArray<DesktopTarget> targets)
        {
            if (targets.IsDefault)
            {
                throw new ArgumentException(
                    "Successful discovery targets must be initialized.",
                    nameof(targets));
            }

            Targets = targets;
        }

        public ImmutableArray<DesktopTarget> Targets { get; }
    }

    public sealed record Failed : TargetDiscoveryResult;
}

/// <summary>
/// Reports the terminal result of a logical request. Intentionally canceled
/// requests produce no completion.
/// </summary>
public sealed record TargetDiscoveryCompleted(
    TargetDiscoveryRequestId RequestId,
    TargetDiscoveryResult Result);
