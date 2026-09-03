using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class NavigationWorkflowTests
{
    private static readonly TargetDiscoveryRequestId FirstRequestId =
        TargetDiscoveryRequestId.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly TargetDiscoveryRequestId SecondRequestId =
        TargetDiscoveryRequestId.Parse("00000000-0000-0000-0000-000000000002");

    private static readonly TargetSnapshot FirstSnapshot =
        new(
            FirstRequestId,
            [
                new DesktopTarget(
                    TargetId.Parse("00000000-0000-0000-0000-000000000003"),
                    new TargetBounds(10, 20, 300, 400)),
            ]);

    [Fact]
    public void CommandLayerResetsOnlyCommandProgress()
    {
        var active = new TargetDiscoveryLifecycle.Active(
            WorkflowGeneration.From(2),
            FirstRequestId);
        var presentation = new PresentationLifecycle.Idle(
            PresentationRevision.From(1));
        var state = new NavigationWorkflowState(
            CommandProgress.PointerPrefix,
            active,
            presentation);
        var observed = LayerObserved("command");

        var decision = NavigationWorkflow.Decide(state, observed);

        Assert.Equal(CommandProgress.Command, decision.State.CommandProgress);
        Assert.Equal(active, decision.State.TargetDiscovery);
        Assert.Equal(presentation, decision.State.Presentation);
        Assert.Collection(
            decision.Effects,
            effect => Assert.Equal(
                observed,
                Assert.IsType<NavigationEffect.ReportKeyboardLayer>(
                    effect).Observation));
    }

    [Fact]
    public void CommandSpaceLeavesActiveDiscoveryCurrent()
    {
        var state = ActiveState(FirstRequestId);
        var observed = Gesture("command", "spc");

        var decision =
            NavigationWorkflow.Decide(
                state,
                observed,
                () => SecondRequestId);

        Assert.Equal(
            CommandProgress.PointerPrefix,
            decision.State.CommandProgress);
        Assert.Equal(state.TargetDiscovery, decision.State.TargetDiscovery);
        Assert.Collection(
            decision.Effects,
            effect => Assert.Equal(
                observed.Token,
                Assert.IsType<NavigationEffect.ReportCommandInput>(
                    effect).Token));
    }

    [Fact]
    public void PointerTargetSupersessionReturnsOrderedEffects()
    {
        var state = ActiveState(FirstRequestId) with
        {
            CommandProgress = CommandProgress.PointerPrefix,
        };
        var observed = Gesture("pointer", "f");

        var decision =
            NavigationWorkflow.Decide(
                state,
                observed,
                () => SecondRequestId);

        Assert.Equal(CommandProgress.Command, decision.State.CommandProgress);
        var active = Assert.IsType<TargetDiscoveryLifecycle.Active>(
            decision.State.TargetDiscovery);
        Assert.Equal(WorkflowGeneration.From(2), active.Generation);
        Assert.Equal(SecondRequestId, active.RequestId);
        Assert.Collection(
            decision.Effects,
            effect => Assert.Equal(
                observed.Token,
                Assert.IsType<NavigationEffect.ReportCommandInput>(
                    effect).Token),
            effect => Assert.Equal(
                FirstRequestId,
                Assert.IsType<NavigationEffect.CancelDiscovery>(
                    effect).RequestId),
            effect => Assert.Equal(
                SecondRequestId,
                Assert.IsType<NavigationEffect.RequestTargetDiscovery>(
                    effect).RequestId));
    }

    [Theory]
    [InlineData(CommandSessionExit.Escape)]
    [InlineData(CommandSessionExit.BaseLayer)]
    [InlineData(CommandSessionExit.Disconnect)]
    public void CommandSessionExitCancelsActiveDiscovery(
        CommandSessionExit exit)
    {
        var state = ActiveState(FirstRequestId, generation: 3) with
        {
            Presentation = new PresentationLifecycle.Idle(
                PresentationRevision.From(2)),
        };
        Assert.Equal(CommandProgress.Command, state.CommandProgress);
        Assert.IsType<TargetDiscoveryLifecycle.Active>(state.TargetDiscovery);

        var decision = EndCommandSession(state, exit);

        Assert.Equal(CommandProgress.Inactive, decision.State.CommandProgress);
        Assert.Equal(
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(3)),
            decision.State.TargetDiscovery);
        Assert.Equal(
            new PresentationLifecycle.Idle(PresentationRevision.From(2)),
            decision.State.Presentation);
        AssertExitEffects(decision.Effects, exit, FirstRequestId);
    }

    [Theory]
    [InlineData(CommandSessionExit.Escape, false)]
    [InlineData(CommandSessionExit.Escape, true)]
    [InlineData(CommandSessionExit.BaseLayer, false)]
    [InlineData(CommandSessionExit.BaseLayer, true)]
    [InlineData(CommandSessionExit.Disconnect, false)]
    [InlineData(CommandSessionExit.Disconnect, true)]
    public void CommandSessionExitInvalidatesPresentation(
        CommandSessionExit exit,
        bool confirmed)
    {
        var discovery = new TargetDiscoveryLifecycle.Idle(
            WorkflowGeneration.From(2));
        var revision = PresentationRevision.From(2);
        PresentationLifecycle presentation = confirmed
            ? new PresentationLifecycle.Current(revision, FirstSnapshot)
            : new PresentationLifecycle.Pending(revision, FirstSnapshot);
        var state = new NavigationWorkflowState(
            CommandProgress.Command,
            discovery,
            presentation);

        var decision = EndCommandSession(state, exit);

        Assert.Equal(CommandProgress.Inactive, decision.State.CommandProgress);
        Assert.Equal(discovery, decision.State.TargetDiscovery);
        Assert.Equal(
            new PresentationLifecycle.Idle(revision),
            decision.State.Presentation);
        AssertExitEffects(decision.Effects, exit, canceledRequestId: null);

        var lateConfirmation = NavigationWorkflow.Decide(
            decision.State,
            new TargetsPresented(revision));
        Assert.Equal(decision.State, lateConfirmation.State);
        Assert.Empty(lateConfirmation.Effects);
    }

    [Fact]
    public void StaleResultLeavesWorkflowUnchanged()
    {
        var state = ActiveState(SecondRequestId);

        var decision = NavigationWorkflow.Decide(
            state,
            new TargetDiscoveryCompleted(
                new TargetSnapshot(FirstRequestId, [])));

        Assert.Equal(state, decision.State);
        Assert.Empty(decision.Effects);
    }

    [Fact]
    public void CurrentDiscoveryFailureEndsDiscovery()
    {
        var state = ActiveState(FirstRequestId, generation: 5) with
        {
            Presentation = new PresentationLifecycle.Idle(
                PresentationRevision.From(4)),
        };

        var decision = NavigationWorkflow.Decide(
            state,
            new TargetDiscoveryFailed(FirstRequestId));

        Assert.Equal(
            state with
            {
                TargetDiscovery =
                    new TargetDiscoveryLifecycle.Idle(
                        WorkflowGeneration.From(5)),
            },
            decision.State);
        Assert.Empty(decision.Effects);
    }

    [Fact]
    public void StaleDiscoveryFailureLeavesWorkflowUnchanged()
    {
        var state = ActiveState(SecondRequestId);

        var decision = NavigationWorkflow.Decide(
            state,
            new TargetDiscoveryFailed(FirstRequestId));

        Assert.Equal(state, decision.State);
        Assert.Empty(decision.Effects);
    }

    [Fact]
    public void CurrentResultAwaitsPresentationAndBecomesDuplicate()
    {
        var state = ActiveState(FirstRequestId, generation: 5) with
        {
            Presentation = new PresentationLifecycle.Idle(
                PresentationRevision.From(4)),
        };
        var completed = new TargetDiscoveryCompleted(FirstSnapshot);

        var accepted = NavigationWorkflow.Decide(state, completed);

        Assert.Equal(
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(5)),
            accepted.State.TargetDiscovery);
        Assert.Equal(
            new PresentationLifecycle.Pending(
                PresentationRevision.From(5),
                FirstSnapshot),
            accepted.State.Presentation);
        var effect = Assert.IsType<NavigationEffect.PresentTargetSnapshot>(
            Assert.Single(accepted.Effects));
        Assert.Equal(
            PresentationRevision.From(5),
            effect.Revision);
        Assert.Equal(completed.Snapshot, effect.Snapshot);

        var duplicate = NavigationWorkflow.Decide(
            accepted.State,
            completed);
        Assert.Equal(accepted.State, duplicate.State);
        Assert.Empty(duplicate.Effects);
    }

    [Fact]
    public void FirstCurrentResultUsesInitialPresentationRevision()
    {
        var state = ActiveState(FirstRequestId);
        Assert.Equal(
            new PresentationLifecycle.Idle(LastAllocatedRevision: null),
            state.Presentation);
        var completed = new TargetDiscoveryCompleted(
            new TargetSnapshot(FirstRequestId, []));

        var decision = NavigationWorkflow.Decide(state, completed);

        Assert.Equal(
            new PresentationLifecycle.Pending(
                PresentationRevision.From(1),
                completed.Snapshot),
            decision.State.Presentation);
        var effect = Assert.IsType<NavigationEffect.PresentTargetSnapshot>(
            Assert.Single(decision.Effects));
        Assert.Equal(PresentationRevision.From(1), effect.Revision);
    }

    [Fact]
    public void OnlyMatchingPresentationConfirmationMakesTargetMapCurrent()
    {
        var revision = PresentationRevision.From(2);
        var pending = new PresentationLifecycle.Pending(
            revision,
            FirstSnapshot);
        var state = new NavigationWorkflowState(
            CommandProgress.Command,
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(2)),
            pending);

        var stale = NavigationWorkflow.Decide(
            state,
            new TargetsPresented(PresentationRevision.From(1)));

        Assert.Equal(state, stale.State);
        Assert.Empty(stale.Effects);

        var confirmed = NavigationWorkflow.Decide(
            stale.State,
            new TargetsPresented(revision));

        Assert.Equal(
            new PresentationLifecycle.Current(revision, FirstSnapshot),
            confirmed.State.Presentation);
        Assert.Empty(confirmed.Effects);

        var duplicate = NavigationWorkflow.Decide(
            confirmed.State,
            new TargetsPresented(revision));

        Assert.Equal(confirmed.State, duplicate.State);
        Assert.Empty(duplicate.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartingTargetDiscoveryInvalidatesPresentation(bool confirmed)
    {
        var revision = PresentationRevision.From(2);
        PresentationLifecycle presentation = confirmed
            ? new PresentationLifecycle.Current(revision, FirstSnapshot)
            : new PresentationLifecycle.Pending(revision, FirstSnapshot);
        var state = new NavigationWorkflowState(
            CommandProgress.PointerPrefix,
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(2)),
            presentation);

        var decision = NavigationWorkflow.Decide(
            state,
            Gesture("pointer", "f"),
            () => SecondRequestId);

        Assert.Equal(
            new PresentationLifecycle.Idle(revision),
            decision.State.Presentation);
        Assert.IsType<TargetDiscoveryLifecycle.Active>(
            decision.State.TargetDiscovery);

        var lateConfirmation = NavigationWorkflow.Decide(
            decision.State,
            new TargetsPresented(revision));
        Assert.Equal(decision.State, lateConfirmation.State);
        Assert.Empty(lateConfirmation.Effects);
    }

    [Fact]
    public void CurrentResultPresentsWhileNextPrefixIsIncomplete()
    {
        var state = ActiveState(FirstRequestId) with
        {
            CommandProgress = CommandProgress.PointerPrefix,
        };
        var completed = new TargetDiscoveryCompleted(
            new TargetSnapshot(FirstRequestId, []));

        var decision = NavigationWorkflow.Decide(state, completed);

        Assert.Equal(
            CommandProgress.PointerPrefix,
            decision.State.CommandProgress);
        Assert.IsType<TargetDiscoveryLifecycle.Idle>(
            decision.State.TargetDiscovery);
        Assert.Equal(
            new PresentationLifecycle.Pending(
                PresentationRevision.From(1),
                completed.Snapshot),
            decision.State.Presentation);
        Assert.IsType<NavigationEffect.PresentTargetSnapshot>(
            Assert.Single(decision.Effects));
    }

    [Fact]
    public void PointerTargetWithoutPrefixDoesNotAllocateRequest()
    {
        var state = NavigationWorkflowState.Initial;
        var observed = Gesture("pointer", "f");
        var requestAllocated = false;

        var decision =
            NavigationWorkflow.Decide(
                state,
                observed,
                () =>
                {
                    requestAllocated = true;
                    return FirstRequestId;
                });

        Assert.Equal(state, decision.State);
        Assert.False(requestAllocated);
        Assert.Collection(
            decision.Effects,
            effect => Assert.Equal(
                observed.Token,
                Assert.IsType<NavigationEffect.ReportCommandInput>(
                    effect).Token));
    }

    [Fact]
    public void DiscoveryAfterIdleGenerationUsesNextGeneration()
    {
        var state = new NavigationWorkflowState(
            CommandProgress.PointerPrefix,
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(1)),
            new PresentationLifecycle.Idle(LastAllocatedRevision: null));
        var observed = Gesture("pointer", "f");

        var decision = NavigationWorkflow.Decide(
            state,
            observed,
            () => SecondRequestId);

        var active = Assert.IsType<TargetDiscoveryLifecycle.Active>(
            decision.State.TargetDiscovery);
        Assert.Equal(WorkflowGeneration.From(2), active.Generation);
    }

    [Fact]
    public void FirstDiscoveryUsesInitialWorkflowGeneration()
    {
        var state = new NavigationWorkflowState(
            CommandProgress.PointerPrefix,
            new TargetDiscoveryLifecycle.Idle(LastGeneration: null),
            new PresentationLifecycle.Idle(LastAllocatedRevision: null));
        var observed = Gesture("pointer", "f");

        var decision = NavigationWorkflow.Decide(
            state,
            observed,
            () => FirstRequestId);

        var active = Assert.IsType<TargetDiscoveryLifecycle.Active>(
            decision.State.TargetDiscovery);
        Assert.Equal(WorkflowGeneration.From(1), active.Generation);
    }

    private static NavigationWorkflowState ActiveState(
        TargetDiscoveryRequestId requestId,
        long generation = 1) =>
        new(
            CommandProgress.Command,
            new TargetDiscoveryLifecycle.Active(
                WorkflowGeneration.From(generation),
                requestId),
            new PresentationLifecycle.Idle(LastAllocatedRevision: null));

    private static NavigationDecision EndCommandSession(
        NavigationWorkflowState state,
        CommandSessionExit exit) =>
        exit switch
        {
            CommandSessionExit.Escape => NavigationWorkflow.Decide(
                state,
                Gesture("command", "esc"),
                () => SecondRequestId),
            CommandSessionExit.BaseLayer => NavigationWorkflow.Decide(
                state,
                LayerObserved("base")),
            CommandSessionExit.Disconnect => NavigationWorkflow.Decide(
                state,
                new KeyboardLayerUnavailable(KanataConnectionId.New())),
            _ => throw new ArgumentOutOfRangeException(nameof(exit), exit, null),
        };

    private static KeyboardLayerObserved LayerObserved(string layer) =>
        new(
            KanataConnectionId.New(),
            KanataFrameSequence.From(1),
            KeyboardLayer.From(layer));

    private static GestureObserved Gesture(
        string context,
        string key) =>
        new(
            KanataConnectionId.New(),
            KanataFrameSequence.From(1),
            new GestureToken(context, key));

    private static void AssertExitEffects(
        IReadOnlyList<NavigationEffect> effects,
        CommandSessionExit exit,
        TargetDiscoveryRequestId? canceledRequestId)
    {
        Assert.Equal(canceledRequestId is null ? 2 : 3, effects.Count);
        switch (exit)
        {
            case CommandSessionExit.Escape:
                Assert.Equal(
                    "esc",
                    Assert.IsType<NavigationEffect.ReportCommandInput>(
                        effects[0]).Token.Key);
                break;
            case CommandSessionExit.BaseLayer:
                Assert.Equal(
                    "base",
                    Assert.IsType<NavigationEffect.ReportKeyboardLayer>(
                        effects[0]).Observation.Layer.Value);
                break;
            case CommandSessionExit.Disconnect:
                Assert.IsType<NavigationEffect.ReportKeyboardLayerUnavailable>(
                    effects[0]);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(exit),
                    exit,
                    null);
        }

        if (canceledRequestId is { } requestId)
        {
            Assert.Equal(
                requestId,
                Assert.IsType<NavigationEffect.CancelDiscovery>(
                    effects[1]).RequestId);
        }

        Assert.IsType<NavigationEffect.ReportCommandSessionEnded>(effects[^1]);
    }

    public enum CommandSessionExit
    {
        Escape,
        BaseLayer,
        Disconnect,
    }
}
