using System.Threading.Channels;

using Akka.Actor;

using Desknav.ControlPlane;

namespace Desknav.UI.Tests;

public sealed class OverlayActorTests
{
    [Fact]
    public async Task NewerVisibleSceneActivatesWhileOlderPreparationUnwinds()
    {
        await using var harness = new ActorHarness();
        var firstRevision = PresentationRevision.From(1);
        var secondRevision = PresentationRevision.From(2);

        harness.Apply(firstRevision, VisiblePresentation());
        var first =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        Assert.IsType<TargetPresentation.Visible>(first.Presentation);

        harness.Apply(secondRevision, VisiblePresentation());
        var (canceled, second) =
            await harness.Renderer.ReadCancellationAndStartAsync();
        Assert.Same(first, canceled.Call);
        Assert.IsType<TargetPresentation.Visible>(second.Presentation);
        Assert.True(first.CancellationToken.IsCancellationRequested);

        second.Complete();
        var secondActivation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;
        secondActivation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();
        Assert.Equal(
            secondRevision,
            (await harness.ReadAppliedAsync()).Revision);

        first.Complete();
        await first.ExecutionEnded;
        var disposed =
            await harness.Renderer.ReadEventAsync<SceneDisposed>();

        Assert.Same(first.Scene, disposed.Scene);
        Assert.Same(second.Scene, harness.Renderer.CurrentScene);
        Assert.Equal([second.Scene], harness.Renderer.Activations);
    }

    [Fact]
    public async Task SupersededVisiblePreparationCannotActivateWhileHiddenIsInFlight()
    {
        await using var harness = new ActorHarness();
        var hiddenRevision = PresentationRevision.From(2);

        harness.Apply(
            PresentationRevision.From(1),
            VisiblePresentation());
        var visible =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        Assert.IsType<TargetPresentation.Visible>(visible.Presentation);
        harness.Apply(
            hiddenRevision,
            new TargetPresentation.Hidden());
        var (canceled, hidden) =
            await harness.Renderer.ReadCancellationAndStartAsync();
        Assert.Same(visible, canceled.Call);
        Assert.IsType<TargetPresentation.Hidden>(hidden.Presentation);

        visible.Complete();
        await visible.ExecutionEnded;
        Assert.Same(
            visible.Scene,
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene);
        Assert.Empty(harness.Renderer.Activations);

        hidden.Complete();
        var activation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;
        activation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();

        Assert.Equal(
            hiddenRevision,
            (await harness.ReadAppliedAsync()).Revision);
        Assert.Same(hidden.Scene, harness.Renderer.CurrentScene);
    }

    [Fact]
    public async Task DelayedHiddenPreparationCannotHideNewerVisibleScene()
    {
        await using var harness = new ActorHarness();
        var firstRevision = PresentationRevision.From(1);
        var hiddenRevision = PresentationRevision.From(2);
        var currentRevision = PresentationRevision.From(3);

        var first = await harness.ApplyCompletelyAsync(
            firstRevision,
            VisiblePresentation());

        harness.Apply(
            hiddenRevision,
            new TargetPresentation.Hidden());
        var hidden =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        Assert.IsType<TargetPresentation.Hidden>(hidden.Presentation);

        harness.Apply(currentRevision, VisiblePresentation());
        var (canceled, current) =
            await harness.Renderer.ReadCancellationAndStartAsync();
        Assert.Same(hidden, canceled.Call);
        Assert.IsType<TargetPresentation.Visible>(current.Presentation);

        current.Complete();
        var currentActivation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;
        currentActivation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();
        Assert.Equal(
            currentRevision,
            (await harness.ReadAppliedAsync()).Revision);
        Assert.Same(
            first.Scene,
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene);

        hidden.Complete();
        await hidden.ExecutionEnded;
        Assert.Same(
            hidden.Scene,
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene);

        Assert.Same(current.Scene, harness.Renderer.CurrentScene);
        Assert.Equal(
            [first.Scene, current.Scene],
            harness.Renderer.Activations);
    }

    [Fact]
    public async Task ActivationsAreSerialized()
    {
        await using var harness = new ActorHarness();
        var firstRevision = PresentationRevision.From(1);
        var secondRevision = PresentationRevision.From(2);

        harness.Apply(firstRevision, VisiblePresentation());
        var first =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        first.Complete();
        var firstActivation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;

        harness.Apply(secondRevision, VisiblePresentation());
        var second =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        second.Complete();
        await second.ExecutionEnded;
        await harness.FlushActorAsync();
        Assert.False(harness.System.WhenTerminated.IsCompleted);

        firstActivation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();
        Assert.Equal(
            firstRevision,
            (await harness.ReadAppliedAsync()).Revision);

        var secondActivation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;
        secondActivation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();
        Assert.Equal(
            secondRevision,
            (await harness.ReadAppliedAsync()).Revision);

        Assert.Equal(
            [first.Scene, second.Scene],
            harness.Renderer.Activations);
    }

    [Fact]
    public async Task SupersededReadySceneIsNeverActivated()
    {
        await using var harness = new ActorHarness();
        var firstRevision = PresentationRevision.From(1);
        var secondRevision = PresentationRevision.From(2);
        var currentRevision = PresentationRevision.From(3);

        harness.Apply(firstRevision, VisiblePresentation());
        var first =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        first.Complete();
        var firstActivation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;

        harness.Apply(secondRevision, VisiblePresentation());
        var second =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        second.Complete();
        await second.ExecutionEnded;
        await harness.FlushActorAsync();

        harness.Apply(currentRevision, VisiblePresentation());
        var events = new OverlayEvent[]
        {
            await harness.Renderer.ReadEventAsync(),
            await harness.Renderer.ReadEventAsync(),
        };
        var current = Assert.Single(
            events.OfType<PreparationStarted>()).Call;
        Assert.Same(
            second.Scene,
            Assert.Single(events.OfType<SceneDisposed>()).Scene);

        current.Complete();
        await current.ExecutionEnded;
        await harness.FlushActorAsync();
        firstActivation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();
        Assert.Equal(
            firstRevision,
            (await harness.ReadAppliedAsync()).Revision);

        var currentActivation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;
        currentActivation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();
        Assert.Equal(
            currentRevision,
            (await harness.ReadAppliedAsync()).Revision);
        Assert.Same(
            first.Scene,
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene);

        Assert.Equal(
            [first.Scene, current.Scene],
            harness.Renderer.Activations);
    }

    [Fact]
    public async Task StaleRevisionIsIgnoredAndCurrentRevisionIsReacknowledged()
    {
        await using var harness = new ActorHarness();
        var currentRevision = PresentationRevision.From(3);
        await harness.ApplyCompletelyAsync(
            currentRevision,
            VisiblePresentation());
        var preparationCount = harness.Renderer.Preparations.Count;

        harness.Apply(
            PresentationRevision.From(2),
            new TargetPresentation.Hidden());
        harness.Apply(currentRevision, VisiblePresentation());

        Assert.Equal(
            currentRevision,
            (await harness.ReadAppliedAsync()).Revision);
        await harness.FlushActorAsync();
        Assert.Equal(preparationCount, harness.Renderer.Preparations.Count);
        Assert.False(harness.TryTakePendingAcknowledgement());
    }

    [Fact]
    public async Task PreparationFailureTerminatesActorSystem()
    {
        await using var harness = new ActorHarness();

        harness.Apply(
            PresentationRevision.From(1),
            VisiblePresentation());
        var preparation =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        preparation.Fail(
            new InvalidOperationException("Preparation failed."));

        await harness.System.WhenTerminated.WaitAsync(harness.TimeoutToken);
    }

    [Fact]
    public async Task ActivationFailureTerminatesActorSystem()
    {
        await using var harness = new ActorHarness();

        harness.Apply(
            PresentationRevision.From(1),
            VisiblePresentation());
        var preparation =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        preparation.Complete();
        var activation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;
        activation.Fail(
            new InvalidOperationException("Activation failed."));

        await harness.System.WhenTerminated.WaitAsync(harness.TimeoutToken);
    }

    [Fact]
    public async Task CanceledPreparationFailureDoesNotTerminateActorSystem()
    {
        await using var harness = new ActorHarness();
        var currentRevision = PresentationRevision.From(2);

        harness.Apply(
            PresentationRevision.From(1),
            VisiblePresentation());
        var canceled =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        harness.Apply(currentRevision, VisiblePresentation());
        var (_, current) =
            await harness.Renderer.ReadCancellationAndStartAsync();

        canceled.Fail(
            new InvalidOperationException("Canceled preparation unwound."));
        current.Complete();
        var activation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;
        activation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();

        Assert.Equal(
            currentRevision,
            (await harness.ReadAppliedAsync()).Revision);
        Assert.False(harness.System.WhenTerminated.IsCompleted);
    }

    [Fact]
    public async Task SceneDisposalFailureTerminatesActorSystem()
    {
        await using var harness = new ActorHarness();
        var first = await harness.ApplyCompletelyAsync(
            PresentationRevision.From(1),
            VisiblePresentation());
        first.Scene.FailDisposal(
            new InvalidOperationException("Scene disposal failed."));

        await harness.ApplyCompletelyAsync(
            PresentationRevision.From(2),
            VisiblePresentation());
        await harness.Renderer.ReadEventAsync<SceneDisposed>();

        await harness.System.WhenTerminated.WaitAsync(harness.TimeoutToken);
    }

    [Fact]
    public async Task StoppingActorCancelsPreparationAndDisposesItsScene()
    {
        await using var harness = new ActorHarness();

        harness.Apply(
            PresentationRevision.From(1),
            VisiblePresentation());
        var preparation =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;

        harness.Actor.Tell(PoisonPill.Instance);
        await harness.Renderer.ReadEventAsync<PreparationCanceled>();
        preparation.Complete();
        await preparation.ExecutionEnded;

        Assert.Same(
            preparation.Scene,
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene);
    }

    [Fact]
    public async Task StoppingActorWaitsForActivationBeforeDisposingItsScene()
    {
        await using var harness = new ActorHarness();

        harness.Apply(
            PresentationRevision.From(1),
            VisiblePresentation());
        var preparation =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        preparation.Complete();
        var activation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;

        Assert.True(
            await harness.Actor.GracefulStop(
                TimeSpan.FromSeconds(3),
                PoisonPill.Instance));
        activation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();

        Assert.Same(
            preparation.Scene,
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene);
    }

    [Fact]
    public async Task StoppingActorWaitsBeforeDisposingOutgoingScene()
    {
        await using var harness = new ActorHarness();
        var active = await harness.ApplyCompletelyAsync(
            PresentationRevision.From(1),
            VisiblePresentation());

        harness.Apply(
            PresentationRevision.From(2),
            VisiblePresentation());
        var next =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        next.Complete();
        var activation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;

        Assert.True(
            await harness.Actor.GracefulStop(
                TimeSpan.FromSeconds(3),
                PoisonPill.Instance));
        activation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();
        var disposed = new[]
        {
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene,
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene,
        };

        Assert.Contains(active.Scene, disposed);
        Assert.Contains(next.Scene, disposed);
    }

    [Fact]
    public async Task ShutdownDisposalFailureDoesNotSkipOutgoingScene()
    {
        await using var harness = new ActorHarness();
        var active = await harness.ApplyCompletelyAsync(
            PresentationRevision.From(1),
            VisiblePresentation());

        harness.Apply(
            PresentationRevision.From(2),
            VisiblePresentation());
        var next =
            (await harness.Renderer.ReadEventAsync<PreparationStarted>()).Call;
        next.Complete();
        next.Scene.FailDisposal(
            new InvalidOperationException("Incoming disposal failed."));
        var activation =
            (await harness.Renderer.ReadEventAsync<ActivationStarted>()).Call;

        Assert.True(
            await harness.Actor.GracefulStop(
                TimeSpan.FromSeconds(3),
                PoisonPill.Instance));
        activation.Complete();
        await harness.Renderer.ReadEventAsync<ActivationCompleted>();
        var disposed = new[]
        {
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene,
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene,
        };

        Assert.Contains(active.Scene, disposed);
        Assert.Contains(next.Scene, disposed);
    }

    [Fact]
    public async Task StoppingActorDisposesActiveScene()
    {
        await using var harness = new ActorHarness();
        var active = await harness.ApplyCompletelyAsync(
            PresentationRevision.From(1),
            VisiblePresentation());

        harness.Actor.Tell(PoisonPill.Instance);

        Assert.Same(
            active.Scene,
            (await harness.Renderer.ReadEventAsync<SceneDisposed>()).Scene);
    }

    private static TargetPresentation.Visible VisiblePresentation() =>
        new(
            new TargetSnapshot(
                TargetDiscoveryRequestId.New(),
                []));

    private sealed class ActorHarness : IAsyncDisposable
    {
        private readonly Channel<object> _acknowledgements =
            Channel.CreateBounded<object>(
                new BoundedChannelOptions(8)
                {
                    SingleReader = true,
                    SingleWriter = true,
                });
        private readonly IActorRef _coordinator;
        private readonly CancellationTokenSource _timeout =
            new(TimeSpan.FromSeconds(10));

        public ActorHarness()
        {
            System = ActorSystem.Create(
                $"overlay-owner-{Guid.NewGuid():N}");
            Renderer = new ControllableOverlayRenderer(_timeout.Token);
            _coordinator = System.ActorOf(
                Props.Create(
                    () => new RecordingActor(
                        _acknowledgements.Writer)));
            Actor = System.ActorOf(OverlayActor.CreateProps(Renderer));
        }

        public ActorSystem System { get; }

        public IActorRef Actor { get; }

        public ControllableOverlayRenderer Renderer { get; }

        public CancellationToken TimeoutToken => _timeout.Token;

        public void Apply(
            PresentationRevision revision,
            TargetPresentation presentation) =>
            Actor.Tell(
                new ApplyTargetPresentation(revision, presentation),
                _coordinator);

        public async Task<PreparationCall> ApplyCompletelyAsync(
            PresentationRevision revision,
            TargetPresentation presentation)
        {
            Apply(revision, presentation);
            var preparation =
                (await Renderer.ReadEventAsync<PreparationStarted>()).Call;
            preparation.Complete();
            var activation =
                (await Renderer.ReadEventAsync<ActivationStarted>()).Call;
            activation.Complete();
            await Renderer.ReadEventAsync<ActivationCompleted>();
            Assert.Equal(revision, (await ReadAppliedAsync()).Revision);
            return preparation;
        }

        public async Task<TargetPresentationApplied> ReadAppliedAsync() =>
            Assert.IsType<TargetPresentationApplied>(
                await _acknowledgements.Reader.ReadAsync(_timeout.Token));

        public bool TryTakePendingAcknowledgement() =>
            _acknowledgements.Reader.TryRead(out _);

        public async Task FlushActorAsync() =>
            await Actor.Ask<ActorIdentity>(
                new Identify(null),
                _timeout.Token);

        public async ValueTask DisposeAsync()
        {
            using var cleanupTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await System
                    .Terminate()
                    .WaitAsync(cleanupTimeout.Token);
            }
            finally
            {
                _timeout.Dispose();
            }
        }
    }

    private sealed class RecordingActor : ReceiveActor
    {
        public RecordingActor(ChannelWriter<object> writer)
        {
            ReceiveAny(message =>
            {
                if (!writer.TryWrite(message))
                {
                    throw new InvalidOperationException(
                        "The acknowledgement recorder rejected a message.");
                }
            });
        }
    }
}