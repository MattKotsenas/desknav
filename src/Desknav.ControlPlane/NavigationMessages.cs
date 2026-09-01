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

public sealed record DiscoverTargets(TargetDiscoveryRequestId RequestId);

public sealed record CancelTargetDiscovery(
    TargetDiscoveryRequestId RequestId);

public sealed record TargetSnapshot(TargetDiscoveryRequestId RequestId);

public sealed record TargetDiscoveryCompleted(TargetSnapshot Snapshot);

public sealed record TargetDiscoveryFailed(
    TargetDiscoveryRequestId RequestId);

public sealed record PresentTargets(
    PresentationRevision Revision,
    TargetSnapshot Snapshot);
