using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Akka.Actor;

using Desknav.ControlPlane;

namespace Desknav.UI.Wpf.Tests;

public sealed class WpfOverlayRendererTests
{
    private static readonly VirtualDesktopBounds TestDesktop =
        new(-1920, -1080, 3840, 2160);

    [Fact]
    public async Task VisiblePreparationCreatesDispatcherOwnedScene()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = Renderer(dispatcher.Dispatcher);
        var map = Map();

        var scene = Assert.IsType<WpfVisibleScene>(
            await Task.Run(
                () => renderer.PrepareAsync(
                    new TargetPresentation.Visible(map),
                    CancellationToken.None)));
        Assert.Same(map, scene.View.Map);
        Assert.Same(dispatcher.Dispatcher, scene.View.Dispatcher);
        Assert.Null(
            await dispatcher.InvokeAsync(() => renderer.HostWindow));

        await scene.DisposeAsync();
    }

    [Fact]
    public async Task HiddenPreparationCreatesHiddenScene()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = Renderer(dispatcher.Dispatcher);

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
        var renderer = Renderer(dispatcher.Dispatcher);

        var preparation = renderer.PrepareAsync(
            new TargetPresentation.Visible(Map()),
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
        var renderer = Renderer(dispatcher.Dispatcher);
        var visible = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Map()),
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
        var renderer = Renderer(dispatcher.Dispatcher);
        var scene = await renderer.PrepareAsync(
            new TargetPresentation.Visible(Map()),
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
        var renderer = Renderer(dispatcher.Dispatcher);
        var superseded = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Map()),
                CancellationToken.None));
        var active = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Map()),
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
        var renderer = Renderer(dispatcher.Dispatcher);
        var active = await renderer.PrepareAsync(
            new TargetPresentation.Visible(Map()),
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
        var owner = Renderer(dispatcher.Dispatcher);
        var other = Renderer(dispatcher.Dispatcher);
        var scene = await owner.PrepareAsync(
            new TargetPresentation.Visible(Map()),
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
        var renderer = Renderer(dispatcher.Dispatcher);
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
                    new TargetPresentation.Visible(Map())),
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

    [Fact]
    public async Task VisibleSceneManifestUsesSuppliedLabelsAndVirtualOrigin()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = Renderer(dispatcher.Dispatcher);
        var first = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000001"),
            new TargetBounds(-1000, -500, 300, 200));
        var second = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000002"),
            new TargetBounds(100, 200, 400, 300));
        var map = new TargetMap(
            TargetDiscoveryRequestId.New(),
            [
                new LabeledTarget(TargetLabel.From("lk"), first),
                new LabeledTarget(TargetLabel.From("df"), second),
            ]);
        var scene = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(map),
                TestContext.Current.CancellationToken));

        var manifest = await dispatcher.InvokeAsync(
            () => SceneManifest(scene.View));

        Assert.Equal(
            [
                new RenderedBadge(first.Id, "lk", 920, 580),
                new RenderedBadge(second.Id, "df", 2020, 1280),
            ],
            manifest);
        await scene.DisposeAsync();
    }

    private static WpfOverlayRenderer Renderer(Dispatcher dispatcher) =>
        new(dispatcher, TestDesktop);

    private static TargetMap Map() =>
        new(
            TargetDiscoveryRequestId.New(),
            [
                new LabeledTarget(
                    TargetLabel.From("f"),
                    new DesktopTarget(
                        TargetId.New(),
                        new TargetBounds(100, 200, 800, 600))),
            ]);

    private static RenderedBadge[] SceneManifest(TargetScene scene)
    {
        scene.Measure(new Size(scene.Width, scene.Height));
        scene.Arrange(new Rect(0, 0, scene.Width, scene.Height));
        scene.UpdateLayout();

        return scene.Children
            .Cast<TargetBadge>()
            .Select(
                badge =>
                {
                    Assert.True(badge.ActualWidth > 0);
                    Assert.True(badge.ActualHeight > 0);
                    var label = Assert.IsType<TextBlock>(badge.Child);
                    var origin = badge.TranslatePoint(new Point(), scene);
                    return new RenderedBadge(
                        badge.Target.Target.Id,
                        label.Text,
                        origin.X,
                        origin.Y);
                })
            .ToArray();
    }

    private sealed record RenderedBadge(
        TargetId TargetId,
        string Label,
        double Left,
        double Top);

    private sealed class RecordingActor : ReceiveActor
    {
        public RecordingActor(
            TaskCompletionSource<TargetPresentationApplied> applied)
        {
            Receive<TargetPresentationApplied>(applied.SetResult);
        }
    }
}