using System.Collections.Immutable;

using Vogen;

namespace Desknav.ControlPlane;

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

internal abstract record TargetDiscoveryLifecycle;

internal sealed record IdleTargetDiscovery(
    WorkflowGeneration? LastGeneration)
    : TargetDiscoveryLifecycle;

internal sealed record ActiveTargetDiscovery(
    WorkflowGeneration Generation,
    TargetDiscoveryRequestId RequestId)
    : TargetDiscoveryLifecycle;

internal sealed record NavigationDecision(
    NavigationWorkflowState State,
    ImmutableArray<NavigationEffect> Effects);

internal abstract record NavigationEffect;

internal sealed record ReportKeyboardLayer(
    KeyboardLayerObserved Observation)
    : NavigationEffect;

internal sealed record ReportKeyboardLayerUnavailable(
    KeyboardLayerUnavailable Observation)
    : NavigationEffect;

internal sealed record ReportCommandInput(GestureToken Token)
    : NavigationEffect;

internal sealed record ReportCommandSessionEnded
    : NavigationEffect;

internal sealed record CancelActiveDiscovery(
    TargetDiscoveryRequestId RequestId)
    : NavigationEffect;

internal sealed record RequestTargetDiscovery(
    TargetDiscoveryRequestId RequestId)
    : NavigationEffect;

internal sealed record PresentTargetSnapshot(
    PresentationRevision Revision,
    TargetSnapshot Snapshot)
    : NavigationEffect;

internal enum CommandProgress
{
    Inactive,
    Command,
    PointerPrefix,
}

[ValueObject<long>(conversions: Conversions.None)]
internal readonly partial struct WorkflowGeneration
{
    private static Validation Validate(long value) =>
        value <= 0
            ? Validation.Invalid("A workflow generation must be positive.")
            : Validation.Ok;
}
