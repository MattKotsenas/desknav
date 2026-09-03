using System.Collections.Immutable;

using Vogen;

namespace Desknav.ControlPlane;

/// <summary>
/// Lets one decision compute a complete replacement for workflow state.
/// </summary>
internal sealed record NavigationWorkflowState(
    CommandProgress CommandProgress,
    TargetDiscoveryLifecycle TargetDiscovery,
    PresentationLifecycle Presentation)
{
    public static NavigationWorkflowState Initial { get; } =
        new(
            CommandProgress.Inactive,
            new TargetDiscoveryLifecycle.Idle(LastGeneration: null),
            new PresentationLifecycle.Stable(
                PresentationRevision.Initial,
                new TargetPresentation.Hidden()));
}

/// <summary>
/// Lets each discovery phase carry only the identities valid in that phase.
/// </summary>
internal abstract record TargetDiscoveryLifecycle
{
    /// <summary>
    /// Retains the last generation after a request ends to prevent generation
    /// reuse.
    /// </summary>
    internal sealed record Idle(
        WorkflowGeneration? LastGeneration)
        : TargetDiscoveryLifecycle;

    /// <summary>
    /// Supplies the request ID used to accept and cancel in-flight discovery.
    /// </summary>
    internal sealed record Active(
        WorkflowGeneration Generation,
        TargetDiscoveryRequestId RequestId)
        : TargetDiscoveryLifecycle;
}

/// <summary>
/// Tracks whether the overlay has applied the current desired presentation.
/// </summary>
internal abstract record PresentationLifecycle(
    PresentationRevision Revision,
    TargetPresentation Presentation)
{
    /// <summary>
    /// Holds the presentation the workflow treats as applied by the overlay.
    /// </summary>
    internal sealed record Stable(
        PresentationRevision Revision,
        TargetPresentation Presentation)
        : PresentationLifecycle(Revision, Presentation);

    /// <summary>
    /// Holds the presentation awaiting confirmation from the overlay.
    /// </summary>
    internal sealed record Applying(
        PresentationRevision Revision,
        TargetPresentation Presentation)
        : PresentationLifecycle(Revision, Presentation);
}

/// <summary>
/// Delays effect dispatch until the full state transition has been computed.
/// </summary>
internal sealed record NavigationDecision(
    NavigationWorkflowState State,
    ImmutableArray<NavigationEffect> Effects);

/// <summary>
/// Provides a typed vocabulary for work emitted by transition policy.
/// </summary>
internal abstract record NavigationEffect
{
    /// <summary>
    /// Reports an observed layer even when it causes no workflow transition.
    /// </summary>
    internal sealed record ReportKeyboardLayer(
        KeyboardLayerObserved Observation)
        : NavigationEffect;

    /// <summary>
    /// Reports layer loss even when the command session is already inactive.
    /// </summary>
    internal sealed record ReportKeyboardLayerUnavailable(
        KeyboardLayerUnavailable Observation)
        : NavigationEffect;

    /// <summary>
    /// Reports every gesture token, including tokens that change no state.
    /// </summary>
    internal sealed record ReportCommandInput(GestureToken Token)
        : NavigationEffect;

    /// <summary>
    /// Reports session end even when no discovery was active.
    /// </summary>
    internal sealed record ReportCommandSessionEnded
        : NavigationEffect;

    /// <summary>
    /// Retires a discovery request the workflow no longer owns.
    /// </summary>
    internal sealed record CancelDiscovery(
        TargetDiscoveryRequestId RequestId)
        : NavigationEffect;

    /// <summary>
    /// Provides the request ID that discovery must echo in its snapshot.
    /// </summary>
    internal sealed record RequestTargetDiscovery(
        TargetDiscoveryRequestId RequestId)
        : NavigationEffect;

    /// <summary>
    /// Carries the newer desired state that the overlay must apply.
    /// </summary>
    internal sealed record ApplyTargetPresentation(
        PresentationRevision Revision,
        TargetPresentation Presentation)
        : NavigationEffect;
}

/// <summary>
/// Records how much of a command gesture has been recognized.
/// </summary>
internal enum CommandProgress
{
    Inactive,
    Command,
    PointerPrefix,
}

/// <summary>
/// Represents only allocated generations; an unallocated generation has no
/// value.
/// </summary>
[ValueObject<long>(conversions: Conversions.None)]
internal readonly partial struct WorkflowGeneration
{
    private static Validation Validate(long value) =>
        value <= 0
            ? Validation.Invalid("A workflow generation must be positive.")
            : Validation.Ok;
}
