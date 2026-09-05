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

    private static readonly TargetSnapshot SecondSnapshot =
        new(
            SecondRequestId,
            [
                new DesktopTarget(
                    TargetId.Parse("00000000-0000-0000-0000-000000000004"),
                    new TargetBounds(50, 60, 700, 800)),
            ]);

    private static readonly TargetMap FirstMap =
        new(
            FirstRequestId,
            [
                new LabeledTarget(
                    TargetLabel.From("f"),
                    FirstSnapshot.Targets[0]),
            ]);

    private static readonly TargetMap SecondMap =
        new(
            SecondRequestId,
            [
                new LabeledTarget(
                    TargetLabel.From("f"),
                    SecondSnapshot.Targets[0]),
            ]);

    [Fact]
    public void CommandLayerResetsOnlyCommandProgress()
    {
        var active = new TargetDiscoveryLifecycle.Active(
            WorkflowGeneration.From(3),
            FirstRequestId);
        var presentation = new PresentationLifecycle.Stable(
            PresentationRevision.From(2),
            new TargetPresentation.Hidden());
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
            Presentation = new PresentationLifecycle.Stable(
                PresentationRevision.From(2),
                new TargetPresentation.Hidden()),
        };
        Assert.Equal(CommandProgress.Command, state.CommandProgress);
        Assert.IsType<TargetDiscoveryLifecycle.Active>(state.TargetDiscovery);

        var decision = EndCommandSession(state, exit);

        Assert.Equal(CommandProgress.Inactive, decision.State.CommandProgress);
        Assert.Equal(
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(3)),
            decision.State.TargetDiscovery);
        Assert.Equal(
            new PresentationLifecycle.Stable(
                PresentationRevision.From(2),
                new TargetPresentation.Hidden()),
            decision.State.Presentation);
        AssertExitEffects(
            decision.Effects,
            exit,
            new NavigationEffect.CancelDiscovery(FirstRequestId));
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
            WorkflowGeneration.From(1));
        var revision = PresentationRevision.From(1);
        var visible = new TargetPresentation.Visible(FirstMap);
        PresentationLifecycle presentation = confirmed
            ? new PresentationLifecycle.Stable(revision, visible)
            : new PresentationLifecycle.Applying(revision, visible);
        var state = new NavigationWorkflowState(
            CommandProgress.Command,
            discovery,
            presentation);

        var decision = EndCommandSession(state, exit);

        Assert.Equal(CommandProgress.Inactive, decision.State.CommandProgress);
        Assert.Equal(
            new PresentationLifecycle.Applying(
                PresentationRevision.From(2),
                new TargetPresentation.Hidden()),
            decision.State.Presentation);
        Assert.Equal(
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(2)),
            decision.State.TargetDiscovery);
        AssertExitEffects(
            decision.Effects,
            exit,
            new NavigationEffect.ApplyTargetPresentation(
                PresentationRevision.From(2),
                new TargetPresentation.Hidden()));

        var lateConfirmation = NavigationWorkflow.Decide(
            decision.State,
            new TargetPresentationApplied(revision));
        Assert.Equal(decision.State, lateConfirmation.State);
        Assert.Empty(lateConfirmation.Effects);

        var hiddenApplied = NavigationWorkflow.Decide(
            decision.State,
            new TargetPresentationApplied(PresentationRevision.From(2)));
        Assert.Equal(
            new PresentationLifecycle.Stable(
                PresentationRevision.From(2),
                new TargetPresentation.Hidden()),
            hiddenApplied.State.Presentation);
        Assert.Empty(hiddenApplied.Effects);
    }

    [Fact]
    public void StaleResultLeavesWorkflowUnchanged()
    {
        var state = ActiveState(SecondRequestId);

        var decision = NavigationWorkflow.Decide(
            state,
            DiscoverySucceeded(
                new TargetSnapshot(FirstRequestId, [])));

        Assert.Equal(state, decision.State);
        Assert.Empty(decision.Effects);
    }

    [Fact]
    public void CurrentDiscoveryFailureEndsDiscovery()
    {
        var state = ActiveState(FirstRequestId, generation: 5) with
        {
            Presentation = new PresentationLifecycle.Stable(
                PresentationRevision.From(4),
                new TargetPresentation.Hidden()),
        };

        var decision = NavigationWorkflow.Decide(
            state,
            DiscoveryFailed(FirstRequestId));

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
            DiscoveryFailed(FirstRequestId));

        Assert.Equal(state, decision.State);
        Assert.Empty(decision.Effects);
    }

    [Fact]
    public void CurrentResultAwaitsPresentationAndBecomesDuplicate()
    {
        var state = ActiveState(FirstRequestId, generation: 5) with
        {
            Presentation = new PresentationLifecycle.Stable(
                PresentationRevision.From(4),
                new TargetPresentation.Hidden()),
        };
        var completed = DiscoverySucceeded(FirstSnapshot);

        var accepted = NavigationWorkflow.Decide(state, completed);

        Assert.Equal(
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(5)),
            accepted.State.TargetDiscovery);
        Assert.Equal(
            new PresentationLifecycle.Applying(
                PresentationRevision.From(5),
                new TargetPresentation.Visible(FirstMap)),
            accepted.State.Presentation);
        Assert.Equal(
            new NavigationEffect.ApplyTargetPresentation(
                PresentationRevision.From(5),
                new TargetPresentation.Visible(FirstMap)),
            Assert.Single(accepted.Effects));

        var duplicate = NavigationWorkflow.Decide(
            accepted.State,
            completed);
        Assert.Equal(accepted.State, duplicate.State);
        Assert.Empty(duplicate.Effects);
    }

    [Fact]
    public void CurrentResultAssignsFixedWidthLabelsInSpatialOrder()
    {
        var first = Target(
            "00000000-0000-0000-0000-000000000011",
            300,
            300);
        var second = Target(
            "00000000-0000-0000-0000-000000000012",
            -1000,
            -500);
        var third = Target(
            "00000000-0000-0000-0000-000000000013",
            100,
            0);
        var fourth = Target(
            "00000000-0000-0000-0000-000000000014",
            -1500,
            0);
        var fifth = Target(
            "00000000-0000-0000-0000-000000000015",
            50,
            100);
        var sixth = Target(
            "00000000-0000-0000-0000-000000000016",
            400,
            100);
        var seventh = Target(
            "00000000-0000-0000-0000-000000000017",
            0,
            300);
        var snapshot = new TargetSnapshot(
            FirstRequestId,
            [
                first,
                second,
                third,
                fourth,
                fifth,
                sixth,
                seventh,
            ]);

        var decision = NavigationWorkflow.Decide(
            ActiveState(FirstRequestId),
            DiscoverySucceeded(snapshot));

        var visible = Assert.IsType<TargetPresentation.Visible>(
            decision.State.Presentation.Presentation);
        Assert.Equal(FirstRequestId, visible.Map.RequestId);
        Assert.Equal(
            ["ff", "fd", "fh", "fj", "fk", "fl", "df"],
            visible.Map.Targets
                .Select(static target => target.Label.Value)
                .ToArray());
        Assert.Equal(
            [
                second.Id,
                fourth.Id,
                third.Id,
                fifth.Id,
                sixth.Id,
                seventh.Id,
                first.Id,
            ],
            visible.Map.Targets
                .Select(static target => target.Target.Id)
                .ToArray());

        var labels = visible.Map.Targets
            .Select(static target => target.Label.Value)
            .ToArray();
        Assert.Equal(labels.Length, labels.Distinct().Count());
        for (var index = 0; index < labels.Length; index++)
        {
            for (var other = 0; other < labels.Length; other++)
            {
                if (index == other)
                {
                    continue;
                }

                Assert.False(
                    labels[other].StartsWith(
                        labels[index],
                        StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void SpatialOrderingUsesBoundsThenTargetIdentity()
    {
        var first = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000004"),
            new TargetBounds(10, 20, 100, 100));
        var second = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000003"),
            new TargetBounds(10, 20, 50, 100));
        var third = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000002"),
            new TargetBounds(10, 20, 100, 50));
        var fourth = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000001"),
            new TargetBounds(10, 20, 100, 100));
        var snapshot = new TargetSnapshot(
            FirstRequestId,
            [first, second, third, fourth]);

        var decision = NavigationWorkflow.Decide(
            ActiveState(FirstRequestId),
            DiscoverySucceeded(snapshot));

        var visible = Assert.IsType<TargetPresentation.Visible>(
            decision.State.Presentation.Presentation);
        Assert.Equal(
            [second.Id, third.Id, fourth.Id, first.Id],
            visible.Map.Targets
                .Select(static target => target.Target.Id)
                .ToArray());
        Assert.Equal(
            ["f", "d", "h", "j"],
            visible.Map.Targets
                .Select(static target => target.Label.Value)
                .ToArray());
    }

    [Fact]
    public void LabelShapedGestureDoesNotSelectVisibleTarget()
    {
        var state = new NavigationWorkflowState(
            CommandProgress.Command,
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(1)),
            new PresentationLifecycle.Stable(
                PresentationRevision.From(1),
                new TargetPresentation.Visible(FirstMap)));
        var observed = Gesture("command", "f");

        var decision = NavigationWorkflow.Decide(
            state,
            observed,
            () => throw new InvalidOperationException());

        Assert.Equal(state, decision.State);
        Assert.Collection(
            decision.Effects,
            effect => Assert.Equal(
                observed.Token,
                Assert.IsType<NavigationEffect.ReportCommandInput>(
                    effect).Token));
    }

    [Fact]
    public void EmptyCurrentResultEndsDiscoveryWithoutPresenting()
    {
        var state = ActiveState(FirstRequestId);
        Assert.Equal(
            new PresentationLifecycle.Stable(
                PresentationRevision.Initial,
                new TargetPresentation.Hidden()),
            state.Presentation);
        var snapshot = new TargetSnapshot(FirstRequestId, []);
        var completed = DiscoverySucceeded(snapshot);

        var decision = NavigationWorkflow.Decide(state, completed);

        Assert.Equal(
            state with
            {
                TargetDiscovery = new TargetDiscoveryLifecycle.Idle(
                    WorkflowGeneration.From(1)),
            },
            decision.State);
        Assert.Empty(decision.Effects);
    }

    [Fact]
    public void OnlyMatchingPresentationConfirmationMakesPresentationStable()
    {
        var revision = PresentationRevision.From(3);
        var applying = new PresentationLifecycle.Applying(
            revision,
            new TargetPresentation.Visible(FirstMap));
        var state = new NavigationWorkflowState(
            CommandProgress.Command,
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(3)),
            applying);

        var stale = NavigationWorkflow.Decide(
            state,
            new TargetPresentationApplied(PresentationRevision.From(1)));

        Assert.Equal(state, stale.State);
        Assert.Empty(stale.Effects);

        var confirmed = NavigationWorkflow.Decide(
            stale.State,
            new TargetPresentationApplied(revision));

        Assert.Equal(
            new PresentationLifecycle.Stable(
                revision,
                new TargetPresentation.Visible(FirstMap)),
            confirmed.State.Presentation);
        Assert.Empty(confirmed.Effects);

        var duplicate = NavigationWorkflow.Decide(
            confirmed.State,
            new TargetPresentationApplied(revision));

        Assert.Equal(confirmed.State, duplicate.State);
        Assert.Empty(duplicate.Effects);
    }

    [Fact]
    public void VisibleHiddenVisibleUsesOneRevisionSequence()
    {
        var firstStarted = NavigationWorkflow.Decide(
            NavigationWorkflow.Decide(
                NavigationWorkflowState.Initial,
                Gesture("command", "spc"),
                () => throw new InvalidOperationException()).State,
            Gesture("pointer", "f"),
            () => FirstRequestId);
        var firstPresented = NavigationWorkflow.Decide(
            firstStarted.State,
            DiscoverySucceeded(FirstSnapshot));
        Assert.Equal(
            new PresentationLifecycle.Applying(
                PresentationRevision.From(1),
                new TargetPresentation.Visible(FirstMap)),
            firstPresented.State.Presentation);
        Assert.Equal(
            new NavigationEffect.ApplyTargetPresentation(
                PresentationRevision.From(1),
                new TargetPresentation.Visible(FirstMap)),
            Assert.Single(firstPresented.Effects));
        var firstStable = NavigationWorkflow.Decide(
            firstPresented.State,
            new TargetPresentationApplied(PresentationRevision.From(1)));
        Assert.Equal(
            new PresentationLifecycle.Stable(
                PresentationRevision.From(1),
                new TargetPresentation.Visible(FirstMap)),
            firstStable.State.Presentation);

        var secondPrefix = NavigationWorkflow.Decide(
            firstStable.State,
            Gesture("command", "spc"),
            () => throw new InvalidOperationException());
        var secondStarted = NavigationWorkflow.Decide(
            secondPrefix.State,
            Gesture("pointer", "f"),
            () => SecondRequestId);

        Assert.Equal(
            new PresentationLifecycle.Applying(
                PresentationRevision.From(2),
                new TargetPresentation.Hidden()),
            secondStarted.State.Presentation);
        Assert.Equal(
            WorkflowGeneration.From(3),
            Assert.IsType<TargetDiscoveryLifecycle.Active>(
                secondStarted.State.TargetDiscovery).Generation);
        Assert.Collection(
            secondStarted.Effects,
            effect => Assert.IsType<NavigationEffect.ReportCommandInput>(
                effect),
            effect => Assert.Equal(
                new NavigationEffect.ApplyTargetPresentation(
                    PresentationRevision.From(2),
                    new TargetPresentation.Hidden()),
                effect),
            effect => Assert.Equal(
                SecondRequestId,
                Assert.IsType<NavigationEffect.RequestTargetDiscovery>(
                    effect).RequestId));

        var secondPresented = NavigationWorkflow.Decide(
            secondStarted.State,
            DiscoverySucceeded(SecondSnapshot));
        Assert.Equal(
            new PresentationLifecycle.Applying(
                PresentationRevision.From(3),
                new TargetPresentation.Visible(SecondMap)),
            secondPresented.State.Presentation);
        Assert.Equal(
            new NavigationEffect.ApplyTargetPresentation(
                PresentationRevision.From(3),
                new TargetPresentation.Visible(SecondMap)),
            Assert.Single(secondPresented.Effects));
        Assert.Equal(
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(3)),
            secondPresented.State.TargetDiscovery);

        var stable = NavigationWorkflow.Decide(
            secondPresented.State,
            new TargetPresentationApplied(PresentationRevision.From(3)));
        var expectedStable = new PresentationLifecycle.Stable(
            PresentationRevision.From(3),
            new TargetPresentation.Visible(SecondMap));
        Assert.Equal(expectedStable, stable.State.Presentation);

        var stalePresentation = NavigationWorkflow.Decide(
            stable.State,
            new TargetPresentationApplied(PresentationRevision.From(1)));
        var staleCleanup = NavigationWorkflow.Decide(
            stalePresentation.State,
            new TargetPresentationApplied(PresentationRevision.From(2)));

        Assert.Equal(stable.State, stalePresentation.State);
        Assert.Equal(stalePresentation.State, staleCleanup.State);
        Assert.Empty(stalePresentation.Effects);
        Assert.Empty(staleCleanup.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartingTargetDiscoveryInvalidatesPresentation(bool confirmed)
    {
        var revision = PresentationRevision.From(1);
        var visible = new TargetPresentation.Visible(FirstMap);
        PresentationLifecycle presentation = confirmed
            ? new PresentationLifecycle.Stable(revision, visible)
            : new PresentationLifecycle.Applying(revision, visible);
        var state = new NavigationWorkflowState(
            CommandProgress.PointerPrefix,
            new TargetDiscoveryLifecycle.Idle(WorkflowGeneration.From(1)),
            presentation);

        var decision = NavigationWorkflow.Decide(
            state,
            Gesture("pointer", "f"),
            () => SecondRequestId);

        Assert.Equal(
            new PresentationLifecycle.Applying(
                PresentationRevision.From(2),
                new TargetPresentation.Hidden()),
            decision.State.Presentation);
        Assert.Equal(
            WorkflowGeneration.From(3),
            Assert.IsType<TargetDiscoveryLifecycle.Active>(
                decision.State.TargetDiscovery).Generation);
        Assert.Collection(
            decision.Effects,
            effect => Assert.IsType<NavigationEffect.ReportCommandInput>(
                effect),
            effect => Assert.Equal(
                new NavigationEffect.ApplyTargetPresentation(
                    PresentationRevision.From(2),
                    new TargetPresentation.Hidden()),
                effect),
            effect => Assert.Equal(
                SecondRequestId,
                Assert.IsType<NavigationEffect.RequestTargetDiscovery>(
                    effect).RequestId));

        var lateConfirmation = NavigationWorkflow.Decide(
            decision.State,
            new TargetPresentationApplied(revision));
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
        var completed = DiscoverySucceeded(FirstSnapshot);

        var decision = NavigationWorkflow.Decide(state, completed);

        Assert.Equal(
            CommandProgress.PointerPrefix,
            decision.State.CommandProgress);
        Assert.IsType<TargetDiscoveryLifecycle.Idle>(
            decision.State.TargetDiscovery);
        Assert.Equal(
            new PresentationLifecycle.Applying(
                PresentationRevision.From(1),
                new TargetPresentation.Visible(FirstMap)),
            decision.State.Presentation);
        Assert.Equal(
            new NavigationEffect.ApplyTargetPresentation(
                PresentationRevision.From(1),
                new TargetPresentation.Visible(FirstMap)),
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
            new PresentationLifecycle.Stable(
                PresentationRevision.Initial,
                new TargetPresentation.Hidden()));
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
            new PresentationLifecycle.Stable(
                PresentationRevision.Initial,
                new TargetPresentation.Hidden()));
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
            new PresentationLifecycle.Stable(
                PresentationRevision.Initial,
                new TargetPresentation.Hidden()));

    private static TargetDiscoveryCompleted DiscoverySucceeded(
        TargetSnapshot snapshot) =>
        new(
            snapshot.RequestId,
            new TargetDiscoveryResult.Succeeded(snapshot.Targets));

    private static TargetDiscoveryCompleted DiscoveryFailed(
        TargetDiscoveryRequestId requestId) =>
        new(requestId, new TargetDiscoveryResult.Failed());

    private static DesktopTarget Target(
        string id,
        int left,
        int top) =>
        new(
            TargetId.Parse(id),
            new TargetBounds(left, top, 100, 100));

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
        NavigationEffect boundaryEffect)
    {
        Assert.Equal(3, effects.Count);
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

        Assert.Equal(boundaryEffect, effects[1]);
        Assert.IsType<NavigationEffect.ReportCommandSessionEnded>(effects[2]);
    }

    public enum CommandSessionExit
    {
        Escape,
        BaseLayer,
        Disconnect,
    }
}