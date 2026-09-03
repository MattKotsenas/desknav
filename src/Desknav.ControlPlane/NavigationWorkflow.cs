using System.Collections.Immutable;

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
        var report = new NavigationEffect.ReportKeyboardLayer(observed);
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
            new NavigationEffect.ReportKeyboardLayerUnavailable(unavailable));

    public static NavigationDecision Decide(
        NavigationWorkflowState state,
        GestureObserved observed,
        Func<TargetDiscoveryRequestId> nextRequestId)
    {
        var reportInput =
            new NavigationEffect.ReportCommandInput(observed.Token);

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
        if (state.TargetDiscovery is not TargetDiscoveryLifecycle.Active active
            || active.RequestId != completed.Snapshot.RequestId)
        {
            return Decision(state);
        }

        var nextRevision = state.Presentation.Revision.Increment();
        var presentation = new TargetPresentation.Visible(
            completed.Snapshot);
        return Decision(
            state with
            {
                TargetDiscovery =
                    new TargetDiscoveryLifecycle.Idle(active.Generation),
                Presentation = new PresentationLifecycle.Applying(
                    nextRevision,
                    presentation),
            },
            new NavigationEffect.ApplyTargetPresentation(
                nextRevision,
                presentation));
    }

    public static NavigationDecision Decide(
        NavigationWorkflowState state,
        TargetDiscoveryFailed failed)
    {
        if (state.TargetDiscovery is not TargetDiscoveryLifecycle.Active active
            || active.RequestId != failed.RequestId)
        {
            return Decision(state);
        }

        return Decision(
            state with
            {
                TargetDiscovery =
                    new TargetDiscoveryLifecycle.Idle(active.Generation),
            });
    }

    public static NavigationDecision Decide(
        NavigationWorkflowState state,
        TargetPresentationApplied applied)
    {
        if (state.Presentation is not PresentationLifecycle.Applying applying
            || applying.Revision != applied.Revision)
        {
            return Decision(state);
        }

        return Decision(
            state with
            {
                Presentation = new PresentationLifecycle.Stable(
                    applying.Revision,
                    applying.Presentation),
            });
    }

    private static NavigationDecision StartTargetDiscovery(
        NavigationWorkflowState state,
        TargetDiscoveryRequestId nextRequestId,
        NavigationEffect.ReportCommandInput reportInput)
    {
        var previousRequestId = state.TargetDiscovery
            is TargetDiscoveryLifecycle.Active active
                ? active.RequestId
                : (TargetDiscoveryRequestId?)null;
        var invalidated = InvalidatePresentation(state.Presentation);
        var highWatermark = GenerationHighWatermark(
            state.TargetDiscovery,
            invalidated.Presentation);
        var nextGeneration = highWatermark is { } previousGeneration
            ? WorkflowGeneration.From(previousGeneration.Value + 1)
            : WorkflowGeneration.From(1);
        var nextState = state with
        {
            CommandProgress = CommandProgress.Command,
            TargetDiscovery = new TargetDiscoveryLifecycle.Active(
                nextGeneration,
                nextRequestId),
            Presentation = invalidated.Presentation,
        };

        if (previousRequestId is { } previous)
        {
            return Decision(
                nextState,
                reportInput,
                invalidated.Effects,
                new NavigationEffect.CancelDiscovery(previous),
                new NavigationEffect.RequestTargetDiscovery(nextRequestId));
        }

        return Decision(
            nextState,
            reportInput,
            invalidated.Effects,
            new NavigationEffect.RequestTargetDiscovery(nextRequestId));
    }

    private static NavigationDecision EndCommandSession(
        NavigationWorkflowState state,
        NavigationEffect report)
    {
        if (state.CommandProgress == CommandProgress.Inactive)
        {
            return Decision(state, report);
        }

        var invalidated = InvalidatePresentation(state.Presentation);
        var ended = state with
        {
            CommandProgress = CommandProgress.Inactive,
            TargetDiscovery = new TargetDiscoveryLifecycle.Idle(
                GenerationHighWatermark(
                    state.TargetDiscovery,
                    invalidated.Presentation)),
            Presentation = invalidated.Presentation,
        };
        if (state.TargetDiscovery is TargetDiscoveryLifecycle.Active active)
        {
            return Decision(
                ended,
                report,
                invalidated.Effects,
                new NavigationEffect.CancelDiscovery(active.RequestId),
                new NavigationEffect.ReportCommandSessionEnded());
        }

        return Decision(
            ended,
            report,
            invalidated.Effects,
            new NavigationEffect.ReportCommandSessionEnded());
    }

    private static PresentationInvalidation InvalidatePresentation(
        PresentationLifecycle presentation)
    {
        if (presentation.Presentation is TargetPresentation.Hidden)
        {
            return new PresentationInvalidation(presentation, []);
        }

        if (presentation.Presentation is not TargetPresentation.Visible)
        {
            throw new InvalidOperationException(
                "Unknown target presentation.");
        }

        var revision = presentation.Revision.Increment();
        var hidden = new TargetPresentation.Hidden();
        return new PresentationInvalidation(
            new PresentationLifecycle.Applying(revision, hidden),
            [new NavigationEffect.ApplyTargetPresentation(revision, hidden)]);
    }

    private static WorkflowGeneration? GenerationHighWatermark(
        TargetDiscoveryLifecycle discovery,
        PresentationLifecycle presentation)
    {
        var discoveryValue = discovery switch
        {
            TargetDiscoveryLifecycle.Active active =>
                active.Generation.Value,
            TargetDiscoveryLifecycle.Idle
                { LastGeneration: { } generation } =>
                generation.Value,
            TargetDiscoveryLifecycle.Idle => 0,
            _ => throw new InvalidOperationException(
                "Unknown target discovery lifecycle."),
        };
        var lastValue = Math.Max(
            discoveryValue,
            presentation.Revision.Value);
        return lastValue == 0
            ? null
            : WorkflowGeneration.From(lastValue);
    }

    private static NavigationDecision Decision(
        NavigationWorkflowState state,
        NavigationEffect first,
        ImmutableArray<NavigationEffect> additional,
        params NavigationEffect[] remaining) =>
        new(state, [first, .. additional, .. remaining]);

    private static NavigationDecision Decision(
        NavigationWorkflowState state,
        params NavigationEffect[] effects) =>
        new(state, [.. effects]);

    private sealed record PresentationInvalidation(
        PresentationLifecycle Presentation,
        ImmutableArray<NavigationEffect> Effects);
}
