using System.Windows.Threading;

using Akka.Actor;

using Desknav.ControlPlane;

namespace Desknav.UI.Wpf.Tests;

public sealed class WpfOverlayRendererTests
{
    [Fact]
    public async Task VisiblePreparationCreatesDispatcherOwnedScene()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = new WpfOverlayRenderer(dispatcher.Dispatcher);
        var snapshot = Snapshot();

        var scene = Assert.IsType<WpfVisibleScene>(
            await Task.Run(
                () => renderer.PrepareAsync(
                    new TargetPresentation.Visible(snapshot),
                    CancellationToken.None)));
        Assert.Same(snapshot, scene.View.Snapshot);
        Assert.Same(dispatcher.Dispatcher, scene.View.Dispatcher);
        Assert.Null(
            await dispatcher.InvokeAsync(() => renderer.HostWindow));

        await scene.DisposeAsync();
    }

    [Fact]
    public async Task HiddenPreparationCreatesHiddenScene()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = new WpfOverlayRenderer(dispatcher.Dispatcher);

        var scene = await renderer.PrepareAsync(
            new TargetPresentation.Hidden(),
            CancellationToken.None);

        Assert.IsType<WpfHiddenScene>(scene);
        Assert.Null(
            await dispatcher.InvokeAsync(() => renderer.HostWindow));

        await scene.DisposeAsync();
    }

    [Fact]
    public async Task PreparationHonorsCancellationBeforeDispatcherWork()
    {
        await using var dispatcher = new WpfDispatcherThread();
        using var cancellation = new CancellationTokenSource();
        using var releaseDispatcher = new ManualResetEventSlim();
        var dispatcherBlocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingOperation = dispatcher.Dispatcher.InvokeAsync(
            () =>
            {
                dispatcherBlocked.SetResult();
                releaseDispatcher.Wait();
            });
        await dispatcherBlocked.Task;
        var renderer = new WpfOverlayRenderer(dispatcher.Dispatcher);

        var preparation = renderer.PrepareAsync(
            new TargetPresentation.Visible(Snapshot()),
            cancellation.Token);
        cancellation.Cancel();
        releaseDispatcher.Set();
        await blockingOperation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => preparation);
    }

    [Fact]
    public async Task ActivationMapsVisibleAndHiddenPresentation()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = new WpfOverlayRenderer(dispatcher.Dispatcher);
        var visible = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Snapshot()),
                CancellationToken.None));

        await renderer.ActivateAsync(visible);

        var visibleState = await dispatcher.InvokeAsync(
            () => (
                renderer.HostWindow?.IsVisible,
                renderer.HostWindow?.Content,
                renderer.ActiveScene));
        Assert.True(visibleState.IsVisible);
        Assert.Same(visible.View, visibleState.Content);
        Assert.Same(visible, visibleState.ActiveScene);

        var hidden = Assert.IsType<WpfHiddenScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Hidden(),
                CancellationToken.None));
        await renderer.ActivateAsync(hidden);

        var hiddenState = await dispatcher.InvokeAsync(
            () => (
                renderer.HostWindow?.IsVisible,
                renderer.HostWindow?.Content,
                renderer.ActiveScene));
        Assert.False(hiddenState.IsVisible);
        Assert.Null(hiddenState.Content);
        Assert.Same(hidden, hiddenState.ActiveScene);

        await visible.DisposeAsync();
        await hidden.DisposeAsync();
    }

    [Fact]
    public async Task ActivationCompletesAfterQueuedRenderWork()
    {
        await using var dispatcher = new WpfDispatcherThread();
        using var releaseDispatcher = new ManualResetEventSlim();
        using var releaseRenderWork = new ManualResetEventSlim();
        var dispatcherBlocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renderWorkStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renderer = new WpfOverlayRenderer(dispatcher.Dispatcher);
        var scene = await renderer.PrepareAsync(
            new TargetPresentation.Visible(Snapshot()),
            TestContext.Current.CancellationToken);

        var blockingOperation = dispatcher.Dispatcher.InvokeAsync(
            () =>
            {
                dispatcherBlocked.SetResult();
                releaseDispatcher.Wait(
                    TestContext.Current.CancellationToken);
            },
            DispatcherPriority.Normal,
            TestContext.Current.CancellationToken);
        await dispatcherBlocked.Task;
        var activation = renderer.ActivateAsync(scene);
        var renderWork = dispatcher.Dispatcher.InvokeAsync(
            () =>
            {
                renderWorkStarted.SetResult();
                releaseRenderWork.Wait(
                    TestContext.Current.CancellationToken);
            },
            DispatcherPriority.Background,
            TestContext.Current.CancellationToken);
        releaseDispatcher.Set();
        await renderWorkStarted.Task;

        try
        {
            Assert.Same(scene, renderer.ActiveScene);
            Assert.False(activation.IsCompleted);
        }
        finally
        {
            releaseRenderWork.Set();
        }

        await blockingOperation;
        await renderWork;
        await activation;
        await scene.DisposeAsync();
    }

    [Fact]
    public async Task DisposingSupersededSceneDoesNotChangeActiveScene()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = new WpfOverlayRenderer(dispatcher.Dispatcher);
        var superseded = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Snapshot()),
                CancellationToken.None));
        var active = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Snapshot()),
                CancellationToken.None));

        await renderer.ActivateAsync(superseded);
        await renderer.ActivateAsync(active);
        await superseded.DisposeAsync();

        var state = await dispatcher.InvokeAsync(
            () => (
                renderer.HostWindow?.IsVisible,
                renderer.HostWindow?.Content,
                renderer.ActiveScene));
        Assert.True(state.IsVisible);
        Assert.Same(active.View, state.Content);
        Assert.Same(active, state.ActiveScene);

        await active.DisposeAsync();
    }

    [Fact]
    public async Task DisposingActiveSceneClosesHostWindow()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = new WpfOverlayRenderer(dispatcher.Dispatcher);
        var active = await renderer.PrepareAsync(
            new TargetPresentation.Visible(Snapshot()),
            CancellationToken.None);
        await renderer.ActivateAsync(active);

        await active.DisposeAsync();

        Assert.Null(
            await dispatcher.InvokeAsync(() => renderer.HostWindow));
        Assert.Null(
            await dispatcher.InvokeAsync(() => renderer.ActiveScene));
    }

    [Fact]
    public async Task RendererRejectsSceneFromAnotherRenderer()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var owner = new WpfOverlayRenderer(dispatcher.Dispatcher);
        var other = new WpfOverlayRenderer(dispatcher.Dispatcher);
        var scene = await owner.PrepareAsync(
            new TargetPresentation.Visible(Snapshot()),
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => other.ActivateAsync(scene));

        await scene.DisposeAsync();
    }

    [Fact]
    public async Task OverlayActorActivatesSceneOnWpfDispatcher()
    {
        await using var dispatcher = new WpfDispatcherThread();
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var renderer = new WpfOverlayRenderer(dispatcher.Dispatcher);
        var applied =
            new TaskCompletionSource<TargetPresentationApplied>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var system = ActorSystem.Create(
            $"wpf-overlay-{Guid.NewGuid():N}");

        try
        {
            var coordinator = system.ActorOf(
                Props.Create(() => new RecordingActor(applied)));
            var overlay = system.ActorOf(
                OverlayActor.CreateProps(renderer));
            var revision = PresentationRevision.From(1);

            overlay.Tell(
                new ApplyTargetPresentation(
                    revision,
                    new TargetPresentation.Visible(Snapshot())),
                coordinator);

            Assert.Equal(
                revision,
                (await applied.Task.WaitAsync(timeout.Token)).Revision);
            Assert.True(
                await dispatcher.InvokeAsync(
                    () => renderer.HostWindow?.IsVisible));

            Assert.True(
                await overlay.GracefulStop(
                    TimeSpan.FromSeconds(3),
                    PoisonPill.Instance));
        }
        finally
        {
            await system.Terminate().WaitAsync(timeout.Token);
        }
    }

    private static TargetSnapshot Snapshot() =>
        new(
            TargetDiscoveryRequestId.New(),
            [
                new DesktopTarget(
                    TargetId.New(),
                    new TargetBounds(100, 200, 800, 600)),
            ]);

    private sealed class RecordingActor : ReceiveActor
    {
        public RecordingActor(
            TaskCompletionSource<TargetPresentationApplied> applied)
        {
            Receive<TargetPresentationApplied>(applied.SetResult);
        }
    }
}