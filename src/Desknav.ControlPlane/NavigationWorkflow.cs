using System.Collections.Immutable;

using Vogen;

namespace Desknav.ControlPlane;

/// <summary>
/// Determines each navigation transition before boundary work is dispatched.
/// </summary>
internal static class NavigationWorkflow
{
    public static NavigationDecision Decide(
        NavigationWorkflowState state,
        KeyboardLayerObserved observed)
    {
        var report = new ReportKeyboardLayer(observed);
        return observed.Layer.Value switch
        {
            "base" => EndCommandSession(state, report),
            "command" => Decision(
                state with
                {
                    CommandProgress = CommandProgress.Command,
                },
                report),
            _ => Decision(state, report),
        };
    }

    public static NavigationDecision Decide(
        NavigationWorkflowState state,
        KeyboardLayerUnavailable unavailable) =>
        EndCommandSession(
            state,
            new ReportKeyboardLayerUnavailable(unavailable));

    public static NavigationDecision Decide(
        NavigationWorkflowState state,
        GestureObserved observed,
        Func<TargetDiscoveryRequestId> nextRequestId)
    {
        var reportInput = new ReportCommandInput(observed.Token);

        if (observed.Token.Key == "esc")
        {
            return EndCommandSession(state, reportInput);
        }

        if (observed.Token is { Context: "command", Key: "spc" })
        {
            return Decision(
                state with
                {
                    CommandProgress = CommandProgress.PointerPrefix,
                },
                reportInput);
        }

        if (observed.Token is not { Context: "pointer", Key: "f" }
            || state.CommandProgress != CommandProgress.PointerPrefix)
        {
            return Decision(state, reportInput);
        }

        return StartTargetDiscovery(
            state,
            nextRequestId(),
            reportInput);
    }

    public static NavigationDecision Decide(
        NavigationWorkflowState state,
        TargetDiscoveryCompleted completed)
    {
        if (state.TargetDiscovery is not ActiveTargetDiscovery active
            || active.RequestId != completed.Snapshot.RequestId)
        {
            return Decision(state);
        }

        var nextRevision = state.LastPresentationRevision is { } previous
            ? PresentationRevision.From(previous.Value + 1)
            : PresentationRevision.From(1);
        return Decision(
            state with
            {
                TargetDiscovery =
                    new IdleTargetDiscovery(active.Generation),
                LastPresentationRevision = nextRevision,
            },
            new PresentTargetSnapshot(
                nextRevision,
                completed.Snapshot));
    }

    private static NavigationDecision StartTargetDiscovery(
        NavigationWorkflowState state,
        TargetDiscoveryRequestId nextRequestId,
        ReportCommandInput reportInput)
    {
        var previousGeneration = state.TargetDiscovery switch
        {
            ActiveTargetDiscovery active => active.Generation,
            IdleTargetDiscovery { LastGeneration: { } generation } =>
                generation,
            _ => (WorkflowGeneration?)null,
        };
        var nextGeneration = previousGeneration is { } lastGeneration
            ? WorkflowGeneration.From(lastGeneration.Value + 1)
            : WorkflowGeneration.From(1);
        var previousRequestId = state.TargetDiscovery
            is ActiveTargetDiscovery activeDiscovery
            ? activeDiscovery.RequestId
            : (TargetDiscoveryRequestId?)null;
        var nextState = state with
        {
            CommandProgress = CommandProgress.Command,
            TargetDiscovery = new ActiveTargetDiscovery(
                nextGeneration,
                nextRequestId),
        };

        if (previousRequestId is { } previous)
        {
            return Decision(
                nextState,
                reportInput,
                new CancelActiveDiscovery(previous),
                new RequestTargetDiscovery(nextRequestId));
        }

        return Decision(
            nextState,
            reportInput,
            new RequestTargetDiscovery(nextRequestId));
    }

    private static NavigationDecision EndCommandSession(
        NavigationWorkflowState state,
        NavigationEffect report)
    {
        if (state.CommandProgress == CommandProgress.Inactive)
        {
            return Decision(state, report);
        }

        var active = state.TargetDiscovery
            as ActiveTargetDiscovery;
        var nextState = state with
        {
            CommandProgress = CommandProgress.Inactive,
            TargetDiscovery = active is null
                ? state.TargetDiscovery
                : new IdleTargetDiscovery(active.Generation),
        };
        if (active is not null)
        {
            return Decision(
                nextState,
                report,
                new CancelActiveDiscovery(active.RequestId),
                new ReportCommandSessionEnded());
        }

        return Decision(
            nextState,
            report,
            new ReportCommandSessionEnded());
    }

    private static NavigationDecision Decision(
        NavigationWorkflowState state,
        params NavigationEffect[] effects) =>
        new(state, [.. effects]);
}

/// <summary>
/// Lets one decision compute a complete replacement for workflow state.
/// </summary>
internal sealed record NavigationWorkflowState(
    CommandProgress CommandProgress,
    TargetDiscoveryLifecycle TargetDiscovery,
    PresentationRevision? LastPresentationRevision)
{
    public static NavigationWorkflowState Initial { get; } =
        new(
            CommandProgress.Inactive,
            new IdleTargetDiscovery(LastGeneration: null),
            LastPresentationRevision: null);
}

/// <summary>
/// Lets each discovery phase carry only the identities valid in that phase.
/// </summary>
internal abstract record TargetDiscoveryLifecycle;

/// <summary>
/// Retains the last generation after a request ends to prevent generation
/// reuse.
/// </summary>
internal sealed record IdleTargetDiscovery(
    WorkflowGeneration? LastGeneration)
    : TargetDiscoveryLifecycle;

/// <summary>
/// Supplies the request ID used to accept and cancel in-flight discovery.
/// </summary>
internal sealed record ActiveTargetDiscovery(
    WorkflowGeneration Generation,
    TargetDiscoveryRequestId RequestId)
    : TargetDiscoveryLifecycle;

/// <summary>
/// Delays effect dispatch until the full state transition has been computed.
/// </summary>
internal sealed record NavigationDecision(
    NavigationWorkflowState State,
    ImmutableArray<NavigationEffect> Effects);

/// <summary>
/// Provides a typed vocabulary for work emitted by transition policy.
/// </summary>
internal abstract record NavigationEffect;

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
internal sealed record CancelActiveDiscovery(
    TargetDiscoveryRequestId RequestId)
    : NavigationEffect;

/// <summary>
/// Provides the request ID that discovery must echo in its snapshot.
/// </summary>
internal sealed record RequestTargetDiscovery(
    TargetDiscoveryRequestId RequestId)
    : NavigationEffect;

/// <summary>
/// Carries the revision the overlay must confirm before label activation.
/// </summary>
internal sealed record PresentTargetSnapshot(
    PresentationRevision Revision,
    TargetSnapshot Snapshot)
    : NavigationEffect;

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
