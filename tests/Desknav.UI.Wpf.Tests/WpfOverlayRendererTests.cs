using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Akka.Actor;

using Desknav.ControlPlane;

using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Desknav.UI.Wpf.Tests;

public sealed class WpfOverlayRendererTests
{
    private static readonly PhysicalDesktop TestDesktop =
        new(
            [
                new PhysicalMonitor(
                    (HMONITOR)new nint(1),
                    new PhysicalRect(
                        -1920,
                        -1080,
                        3840,
                        2160)),
            ]);

    [Fact]
    public void PhysicalDesktopOwnsTargetsByTopLeftThenIntersection()
    {
        var left = new PhysicalMonitor(
            (HMONITOR)new nint(1),
            new PhysicalRect(-1920, 0, 1920, 1080));
        var right = new PhysicalMonitor(
            (HMONITOR)new nint(2),
            new PhysicalRect(0, 0, 2560, 1440));
        var desktop = new PhysicalDesktop([right, left]);

        Assert.Equal(
            left,
            desktop.FindMonitor(
                new PhysicalRect(-100, 600, 300, 200)));
        Assert.Equal(
            right,
            desktop.FindMonitor(
                new PhysicalRect(-200, -100, 500, 300)));
    }

    [Fact]
    public void MonitorProjectionConvertsPhysicalRectsToLocalDips()
    {
        var projection = new MonitorProjection(
            new PhysicalRect(0, 0, 2560, 1440),
            new DpiScale(1.5, 1.5));

        Assert.Equal(
            new Size(2560d / 1.5, 1440d / 1.5),
            projection.Size);
        Assert.Equal(
            new Point(100, 200),
            projection.Project(
                new PhysicalPoint(150, 300)));
        Assert.Equal(
            new Point(),
            projection.Project(
                new PhysicalPoint(-200, -100)));
    }

    [Fact]
    public void PhysicalDesktopRejectsDuplicateMonitorHandles()
    {
        var handle = (HMONITOR)new nint(1);

        Assert.Throws<InvalidOperationException>(
            () => new PhysicalDesktop(
                [
                    new PhysicalMonitor(
                        handle,
                        new PhysicalRect(0, 0, 400, 300)),
                    new PhysicalMonitor(
                        handle,
                        new PhysicalRect(400, 0, 400, 300)),
                ]));
    }

    [Fact]
    public void PhysicalDesktopRejectsTargetsOutsideEveryMonitor()
    {
        var desktop = new PhysicalDesktop(
            [
                new PhysicalMonitor(
                    (HMONITOR)new nint(1),
                    new PhysicalRect(0, 0, 400, 300)),
            ]);

        Assert.Throws<InvalidOperationException>(
            () => desktop.FindMonitor(
                new PhysicalRect(500, 500, 100, 100)));
    }

    [Fact]
    public async Task VisiblePreparationSnapshotsMonitorAssignments()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = Renderer(dispatcher.Dispatcher);
        var map = Map();

        var scene = Assert.IsType<WpfVisibleScene>(
            await Task.Run(
                () => renderer.PrepareAsync(
                    new TargetPresentation.Visible(map),
                    CancellationToken.None)));
        Assert.Same(map, scene.Map);
        var monitor = Assert.Single(scene.Monitors);
        Assert.Equal(
            (HMONITOR)new nint(1),
            monitor.Monitor.Handle);
        Assert.Equal(map.Targets, monitor.Targets);
        Assert.Empty(
            await dispatcher.InvokeAsync(() => renderer.HostWindows));

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
        Assert.Empty(
            await dispatcher.InvokeAsync(() => renderer.HostWindows));

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
            () =>
            {
                var host = Assert.Single(renderer.HostWindows).Value;
                return (
                    host.IsVisible,
                    host.Content,
                    renderer.ActiveScene);
            });
        Assert.True(visibleState.IsVisible);
        Assert.IsType<TargetScene>(visibleState.Content);
        Assert.Same(visible, visibleState.ActiveScene);

        var hidden = Assert.IsType<WpfHiddenScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Hidden(),
                CancellationToken.None));
        await renderer.ActivateAsync(hidden);

        var hiddenState = await dispatcher.InvokeAsync(
            () =>
            {
                var host = Assert.Single(renderer.HostWindows).Value;
                return (
                    host.IsVisible,
                    host.Content,
                    renderer.ActiveScene);
            });
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
            () =>
            {
                var host = Assert.Single(renderer.HostWindows).Value;
                return (
                    host.IsVisible,
                    host.Content,
                    renderer.ActiveScene);
            });
        Assert.True(state.IsVisible);
        Assert.IsType<TargetScene>(state.Content);
        Assert.Same(active, state.ActiveScene);

        await active.DisposeAsync();
    }

    [Fact]
    public async Task DisposingActiveSceneClosesHostWindows()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = Renderer(dispatcher.Dispatcher);
        var active = await renderer.PrepareAsync(
            new TargetPresentation.Visible(Map()),
            CancellationToken.None);
        await renderer.ActivateAsync(active);

        await active.DisposeAsync();

        Assert.Empty(
            await dispatcher.InvokeAsync(() => renderer.HostWindows));
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
                    () => Assert
                        .Single(renderer.HostWindows)
                        .Value
                        .IsVisible));

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
    public async Task VisibleSceneManifestUsesSuppliedLabelsAndMonitorOrigin()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = Renderer(dispatcher.Dispatcher);
        var first = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000001"),
            new PhysicalRect(-1000, -500, 300, 200));
        var second = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000002"),
            new PhysicalRect(100, 200, 400, 300));
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
        await renderer.ActivateAsync(scene);

        var manifest = await dispatcher.InvokeAsync(
            () => SceneManifest(
                Assert.IsType<TargetScene>(
                    Assert.Single(renderer.HostWindows)
                        .Value
                        .Content)));

        Assert.Equal(
            [
                new RenderedBadge(first.Id, "lk", 920, 580),
                new RenderedBadge(second.Id, "df", 2020, 1280),
            ],
            manifest);
        await scene.DisposeAsync();
    }

    [Fact]
    public async Task VisibleSceneProjectsTargetsIntoMonitorLocalDips()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var desktop = new PhysicalDesktop(
            [
                new PhysicalMonitor(
                    (HMONITOR)new nint(1),
                    new PhysicalRect(-1920, 0, 1920, 1080)),
                new PhysicalMonitor(
                    (HMONITOR)new nint(2),
                    new PhysicalRect(0, 0, 2560, 1440)),
            ]);
        var scales = new Queue<DpiScale>(
            [
                new DpiScale(1, 1),
                new DpiScale(1.5, 1.5),
            ]);
        var renderer = new WpfOverlayRenderer(
            dispatcher.Dispatcher,
            desktop,
            _ => scales.Dequeue());
        var left = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000001"),
            new PhysicalRect(-1000, 300, 300, 200));
        var right = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000002"),
            new PhysicalRect(150, 300, 400, 300));
        var crossing = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000003"),
            new PhysicalRect(-100, 600, 300, 200));
        var largestIntersection = new DesktopTarget(
            TargetId.Parse("00000000-0000-0000-0000-000000000004"),
            new PhysicalRect(-200, -100, 500, 300));
        var map = new TargetMap(
            TargetDiscoveryRequestId.New(),
            [
                new LabeledTarget(TargetLabel.From("f"), left),
                new LabeledTarget(TargetLabel.From("d"), right),
                new LabeledTarget(TargetLabel.From("s"), crossing),
                new LabeledTarget(
                    TargetLabel.From("a"),
                    largestIntersection),
            ]);

        var scene = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(map),
                TestContext.Current.CancellationToken));
        await renderer.ActivateAsync(scene);

        var views = await dispatcher.InvokeAsync(
            () => renderer.HostWindows
                .Select(
                    pair =>
                    {
                        var view = Assert.IsType<TargetScene>(
                            pair.Value.Content);
                        return (
                            pair.Key,
                            Manifest: SceneManifest(view));
                    })
                .OrderBy(
                    static view =>
                        ((nint)view.Key).ToInt64())
                .ToArray());
        Assert.Collection(
            views,
            view =>
            {
                Assert.Equal(
                    (HMONITOR)new nint(1),
                    view.Key);
                Assert.Equal(
                    [
                        new RenderedBadge(left.Id, "f", 920, 300),
                        new RenderedBadge(crossing.Id, "s", 1820, 600),
                    ],
                    view.Manifest);
            },
            view =>
            {
                Assert.Equal(
                    (HMONITOR)new nint(2),
                    view.Key);
                Assert.Equal(
                    [
                        new RenderedBadge(right.Id, "d", 100, 200),
                        new RenderedBadge(
                            largestIntersection.Id,
                            "a",
                            0,
                            0),
                    ],
                    view.Manifest);
            });
        await scene.DisposeAsync();
    }

    [Fact]
    public async Task FailedReplacementPreservesActiveWindowsAndClosesStagedOnes()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var desktop = TwoMonitorDesktop();
        var stagedHandles = new List<HWND>();
        var failReplacement = false;
        var replacementDpiReads = 0;
        var renderer = new WpfOverlayRenderer(
            dispatcher.Dispatcher,
            desktop,
            window =>
            {
                if (failReplacement)
                {
                    stagedHandles.Add(window);
                    replacementDpiReads++;
                }

                return replacementDpiReads == 2
                    ? throw new InvalidOperationException(
                        "DPI lookup failed.")
                    : new DpiScale(1, 1);
            });
        var first = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Map()),
                TestContext.Current.CancellationToken));
        await renderer.ActivateAsync(first);
        var originalWindows = await dispatcher.InvokeAsync(
            () => renderer.HostWindows.ToDictionary());
        var replacement = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Map()),
                TestContext.Current.CancellationToken));
        failReplacement = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => renderer.ActivateAsync(replacement));

        var remaining = await dispatcher.InvokeAsync(
            () => renderer.HostWindows.ToDictionary());
        Assert.Equal(originalWindows.Keys, remaining.Keys);
        Assert.All(
            originalWindows,
            pair => Assert.Same(pair.Value, remaining[pair.Key]));
        Assert.Same(first, renderer.ActiveScene);
        Assert.Equal(2, replacementDpiReads);
        Assert.All(
            stagedHandles,
            static handle => Assert.False(
                OverlayWindow.IsOpen(handle)));

        await replacement.DisposeAsync();
        await first.DisposeAsync();
    }

    [Fact]
    public async Task ActivationOwnsOneHostWindowPerMonitor()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = new WpfOverlayRenderer(
            dispatcher.Dispatcher,
            TwoMonitorDesktop(),
            static _ => new DpiScale(1, 1));
        var scene = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Map()),
                TestContext.Current.CancellationToken));

        await renderer.ActivateAsync(scene);

        var hosts = await dispatcher.InvokeAsync(
            () => renderer.HostWindows
                .OrderBy(
                    static pair =>
                        ((nint)pair.Key).ToInt64())
                .Select(
                    static pair => (
                        pair.Key,
                        pair.Value.IsVisible,
                        View: Assert.IsType<TargetScene>(
                            pair.Value.Content)))
                .ToArray());
        Assert.Collection(
            hosts,
            host =>
            {
                Assert.Equal(
                    (HMONITOR)new nint(1),
                    host.Key);
                Assert.True(host.IsVisible);
                Assert.Equal(
                    (HMONITOR)new nint(1),
                    host.View.Monitor.Handle);
            },
            host =>
            {
                Assert.Equal(
                    (HMONITOR)new nint(2),
                    host.Key);
                Assert.True(host.IsVisible);
                Assert.Equal(
                    (HMONITOR)new nint(2),
                    host.View.Monitor.Handle);
            });

        await scene.DisposeAsync();
    }

    [Fact]
    public async Task ActivationCompletionCoversEveryMonitorSurface()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var renderer = new WpfOverlayRenderer(
            dispatcher.Dispatcher,
            TwoMonitorDesktop(),
            static _ => new DpiScale(1, 1));
        var first = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Map()),
                TestContext.Current.CancellationToken));
        var second = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(Map()),
                TestContext.Current.CancellationToken));
        await renderer.ActivateAsync(first);
        var firstViews = await dispatcher.InvokeAsync(
            () => renderer.HostWindows
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.Content));

        await renderer.ActivateAsync(second);

        var active = await dispatcher.InvokeAsync(
            () =>
            {
                var views = renderer.HostWindows.ToDictionary(
                    static pair => pair.Key,
                    static pair =>
                        Assert.IsType<TargetScene>(
                            pair.Value.Content));
                var targetIds = views.Values
                    .SelectMany(SceneManifest)
                    .Select(static badge => badge.TargetId)
                    .ToArray();
                return (Views: views, TargetIds: targetIds);
            });
        Assert.All(
            active.Views,
            pair => Assert.NotSame(
                firstViews[pair.Key],
                pair.Value));
        Assert.Equal(
            second.Map.Targets
                .Select(static target => target.Target.Id),
            active.TargetIds);
        Assert.Same(second, renderer.ActiveScene);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task DefaultRendererActivatesEveryCurrentMonitor()
    {
        await using var dispatcher = new WpfDispatcherThread();
        var desktop = await dispatcher.InvokeAsync(
            MonitorTopology.GetCurrent);
        var first = desktop.Monitors[0];
        var map = new TargetMap(
            TargetDiscoveryRequestId.New(),
            [
                new LabeledTarget(
                    TargetLabel.From("f"),
                    new DesktopTarget(
                        TargetId.New(),
                        first.Bounds)),
            ]);
        var renderer = new WpfOverlayRenderer(dispatcher.Dispatcher);
        var scene = Assert.IsType<WpfVisibleScene>(
            await renderer.PrepareAsync(
                new TargetPresentation.Visible(map),
                TestContext.Current.CancellationToken));

        await renderer.ActivateAsync(scene);

        var active = await dispatcher.InvokeAsync(
            () => renderer.HostWindows
                .Values
                .Select(
                    static host =>
                        Assert.IsType<TargetScene>(host.Content))
                .ToArray());
        Assert.Equal(scene.Monitors.Length, active.Length);
        Assert.All(
            active,
            static view =>
            {
                Assert.NotEqual(default, view.Monitor.Handle);
                Assert.True(
                    view.Projection.Scale.DpiScaleX > 0);
                Assert.True(
                    view.Projection.Scale.DpiScaleY > 0);
            });

        await scene.DisposeAsync();
    }

    private static WpfOverlayRenderer Renderer(Dispatcher dispatcher) =>
        new(
            dispatcher,
            TestDesktop,
            static _ => new DpiScale(1, 1));

    private static PhysicalDesktop TwoMonitorDesktop() =>
        new(
            [
                new PhysicalMonitor(
                    (HMONITOR)new nint(1),
                    new PhysicalRect(0, 0, 400, 300)),
                new PhysicalMonitor(
                    (HMONITOR)new nint(2),
                    new PhysicalRect(400, 0, 400, 300)),
            ]);

    private static TargetMap Map() =>
        new(
            TargetDiscoveryRequestId.New(),
            [
                new LabeledTarget(
                    TargetLabel.From("f"),
                    new DesktopTarget(
                        TargetId.New(),
                        new PhysicalRect(100, 200, 800, 600))),
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