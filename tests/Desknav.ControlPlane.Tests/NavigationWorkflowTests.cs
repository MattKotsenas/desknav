using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class NavigationWorkflowTests
{
    private static readonly TargetDiscoveryRequestId FirstRequestId =
        TargetDiscoveryRequestId.From(
            Guid.Parse("00000000-0000-0000-0000-000000000001"));

    private static readonly TargetDiscoveryRequestId SecondRequestId =
        TargetDiscoveryRequestId.From(
            Guid.Parse("00000000-0000-0000-0000-000000000002"));

    [Fact]
    public void CommandLayerResetsOnlyCommandProgress()
    {
        var active = new ActiveTargetDiscovery(
            WorkflowGeneration.From(1),
            FirstRequestId);
        var state = new NavigationWorkflowState(
            CommandProgress.PointerPrefix,
            active,
            LastPresentationRevision: null);
        var observed = LayerObserved("command");

        var decision = NavigationWorkflow.Decide(state, observed);

        Assert.Equal(CommandProgress.Command, decision.State.CommandProgress);
        Assert.Equal(active, decision.State.TargetDiscovery);
        Assert.Collection(
            decision.Effects,
            effect => Assert.Equal(
                observed,
                Assert.IsType<ReportKeyboardLayer>(effect).Observation));
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
                Assert.IsType<ReportCommandInput>(effect).Token));
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
        var active = Assert.IsType<ActiveTargetDiscovery>(
            decision.State.TargetDiscovery);
        Assert.Equal(WorkflowGeneration.From(2), active.Generation);
        Assert.Equal(SecondRequestId, active.RequestId);
        Assert.Collection(
            decision.Effects,
            effect => Assert.Equal(
                observed.Token,
                Assert.IsType<ReportCommandInput>(effect).Token),
            effect => Assert.Equal(
                FirstRequestId,
                Assert.IsType<CancelActiveDiscovery>(effect).RequestId),
            effect => Assert.Equal(
                SecondRequestId,
                Assert.IsType<RequestTargetDiscovery>(effect).RequestId));
    }

    [Theory]
    [InlineData(CommandSessionExit.Escape)]
    [InlineData(CommandSessionExit.BaseLayer)]
    [InlineData(CommandSessionExit.Disconnect)]
    public void CommandSessionExitCancelsActiveDiscovery(
        CommandSessionExit exit)
    {
        var state = ActiveState(FirstRequestId);
        Assert.Equal(CommandProgress.Command, state.CommandProgress);
        Assert.IsType<ActiveTargetDiscovery>(state.TargetDiscovery);

        var decision = EndCommandSession(state, exit);

        Assert.Equal(CommandProgress.Inactive, decision.State.CommandProgress);
        Assert.Equal(
            new IdleTargetDiscovery(WorkflowGeneration.From(1)),
            decision.State.TargetDiscovery);
        AssertExitEffects(decision.Effects, exit);
    }

    [Fact]
    public void StaleResultLeavesWorkflowUnchanged()
    {
        var state = ActiveState(SecondRequestId);

        var decision = NavigationWorkflow.Decide(
            state,
            new TargetDiscoveryCompleted(
                new TargetSnapshot(FirstRequestId)));

        Assert.Equal(state, decision.State);
        Assert.Empty(decision.Effects);
    }

    [Fact]
    public void CurrentResultAdvancesPresentationAndBecomesDuplicate()
    {
        var state = ActiveState(FirstRequestId) with
        {
            LastPresentationRevision = PresentationRevision.From(4),
        };
        var completed = new TargetDiscoveryCompleted(
            new TargetSnapshot(FirstRequestId));

        var accepted = NavigationWorkflow.Decide(state, completed);

        Assert.Equal(
            new IdleTargetDiscovery(WorkflowGeneration.From(1)),
            accepted.State.TargetDiscovery);
        Assert.Equal(
            PresentationRevision.From(5),
            accepted.State.LastPresentationRevision);
        var effect = Assert.IsType<PresentTargetSnapshot>(
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
                Assert.IsType<ReportCommandInput>(effect).Token));
    }

    [Fact]
    public void DiscoveryAfterIdleGenerationUsesNextGeneration()
    {
        var state = new NavigationWorkflowState(
            CommandProgress.PointerPrefix,
            new IdleTargetDiscovery(WorkflowGeneration.From(1)),
            LastPresentationRevision: null);
        var observed = Gesture("pointer", "f");

        var decision = NavigationWorkflow.Decide(
            state,
            observed,
            () => SecondRequestId);

        var active = Assert.IsType<ActiveTargetDiscovery>(
            decision.State.TargetDiscovery);
        Assert.Equal(WorkflowGeneration.From(2), active.Generation);
    }

    private static NavigationWorkflowState ActiveState(
        TargetDiscoveryRequestId requestId) =>
        new(
            CommandProgress.Command,
            new ActiveTargetDiscovery(
                WorkflowGeneration.From(1),
                requestId),
            LastPresentationRevision: null);

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
        CommandSessionExit exit)
    {
        Assert.Equal(3, effects.Count);
        switch (exit)
        {
            case CommandSessionExit.Escape:
                Assert.Equal(
                    "esc",
                    Assert.IsType<ReportCommandInput>(effects[0]).Token.Key);
                break;
            case CommandSessionExit.BaseLayer:
                Assert.Equal(
                    "base",
                    Assert.IsType<ReportKeyboardLayer>(
                        effects[0]).Observation.Layer.Value);
                break;
            case CommandSessionExit.Disconnect:
                Assert.IsType<ReportKeyboardLayerUnavailable>(effects[0]);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(exit),
                    exit,
                    null);
        }

        Assert.Equal(
            FirstRequestId,
            Assert.IsType<CancelActiveDiscovery>(
                effects[1]).RequestId);
        Assert.IsType<ReportCommandSessionEnded>(effects[2]);
    }
}
