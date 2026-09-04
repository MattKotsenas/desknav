using Vogen;

namespace Desknav.ControlPlane;

[ValueObject<long>(conversions: Conversions.None)]
[Instance("Initial", 0L)]
public readonly partial struct PresentationRevision
{
    internal PresentationRevision Increment() =>
        From(checked(Value + 1));

    private static Validation Validate(long value) =>
        value <= 0
            ? Validation.Invalid("A presentation revision must be positive.")
            : Validation.Ok;
}

public abstract record TargetPresentation
{
    public sealed record Visible(TargetSnapshot Snapshot)
        : TargetPresentation;

    public sealed record Hidden
        : TargetPresentation;
}

public sealed record ApplyTargetPresentation(
    PresentationRevision Revision,
    TargetPresentation Presentation);

public sealed record TargetPresentationApplied(
    PresentationRevision Revision);
