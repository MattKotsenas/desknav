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

[ValueObject<string>(conversions: Conversions.None)]
public readonly partial struct TargetLabel
{
    internal const string Alphabet = "fdhjkl";

    private static Validation Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Validation.Invalid("A target label cannot be empty.");
        }

        return value.All(Alphabet.Contains)
            ? Validation.Ok
            : Validation.Invalid(
                $"A target label may contain only '{Alphabet}'.");
    }
}

public sealed record LabeledTarget
{
    internal LabeledTarget(TargetLabel label, DesktopTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrEmpty(label.Value))
        {
            throw new ArgumentException(
                "A target label must be initialized.",
                nameof(label));
        }

        Label = label;
        Target = target;
    }

    public TargetLabel Label { get; }

    public DesktopTarget Target { get; }
}

public sealed class TargetMap : IEquatable<TargetMap>
{
    internal TargetMap(
        TargetDiscoveryRequestId requestId,
        ImmutableArray<LabeledTarget> targets)
    {
        if (targets.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A target map must contain at least one target.",
                nameof(targets));
        }

        if (targets.Any(static target => target is null))
        {
            throw new ArgumentException(
                "A target map cannot contain null targets.",
                nameof(targets));
        }

        var labels = targets
            .Select(static target => target.Label.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        for (var index = 1; index < labels.Length; index++)
        {
            if (labels[index].StartsWith(
                    labels[index - 1],
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Target labels must be unique, with no label a prefix"
                    + " of another.",
                    nameof(targets));
            }
        }

        if (targets
                .Select(static target => target.Target.Id)
                .Distinct()
                .Count()
            != targets.Length)
        {
            throw new ArgumentException(
                "A target map cannot contain a target more than once.",
                nameof(targets));
        }

        RequestId = requestId;
        Targets = targets;
    }

    public TargetDiscoveryRequestId RequestId { get; }

    public ImmutableArray<LabeledTarget> Targets { get; }

    public bool Equals(TargetMap? other) =>
        other is not null
        && RequestId == other.RequestId
        && Targets.SequenceEqual(other.Targets);

    public override bool Equals(object? obj) =>
        obj is TargetMap other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RequestId);
        foreach (var target in Targets)
        {
            hash.Add(target);
        }

        return hash.ToHashCode();
    }
}

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