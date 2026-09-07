using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using Desknav.ControlPlane;

using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Desknav.UI.Wpf;

/// <summary>
/// Builds and atomically activates overlay scenes on one WPF dispatcher.
/// </summary>
public sealed class WpfOverlayRenderer : IOverlayRenderer
{
    private const string CloseFailureMessage =
        "One or more overlay windows failed to close.";

    private readonly Dispatcher _dispatcher;
    private readonly Func<PhysicalDesktop> _getDesktop;
    private readonly WindowDpiReader _readDpi;
    // Cleanup-resistant HWNDs stay owned so release can retry them.
    private readonly Dictionary<HWND, Window> _retainedWindows = [];
    private Dictionary<HMONITOR, Window> _hostWindows = [];

    public WpfOverlayRenderer(Dispatcher dispatcher)
        : this(
            dispatcher,
            MonitorTopology.GetCurrent,
            WindowDpi.GetScale)
    {
    }

    internal WpfOverlayRenderer(
        Dispatcher dispatcher,
        PhysicalDesktop desktop,
        WindowDpiReader readDpi)
        : this(
            dispatcher,
            () => desktop,
            readDpi)
    {
        ArgumentNullException.ThrowIfNull(desktop);
    }

    private WpfOverlayRenderer(
        Dispatcher dispatcher,
        Func<PhysicalDesktop> getDesktop,
        WindowDpiReader readDpi)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(getDesktop);
        ArgumentNullException.ThrowIfNull(readDpi);

        _dispatcher = dispatcher;
        _getDesktop = getDesktop;
        _readDpi = readDpi;
    }

    internal IReadOnlyDictionary<HMONITOR, Window> HostWindows =>
        _hostWindows;

    internal WpfPreparedScene? ActiveScene { get; private set; }

    public Task<IPreparedScene> PrepareAsync(
        TargetPresentation presentation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return InvokeAsync(
            () => (IPreparedScene)Prepare(presentation),
            cancellationToken);
    }

    public async Task ActivateAsync(IPreparedScene scene)
    {
        if (scene is not WpfPreparedScene prepared
            || !ReferenceEquals(prepared.Renderer, this))
        {
            throw new ArgumentException(
                "The prepared scene belongs to another renderer.",
                nameof(scene));
        }

        await InvokeAsync(
                () =>
                {
                    prepared.ThrowIfDisposed();
                    switch (prepared)
                    {
                        case WpfVisibleScene visible:
                            ActivateVisible(visible);
                            break;
                        case WpfHiddenScene:
                            foreach (var host in _hostWindows.Values)
                            {
                                host.Content = null;
                                host.Hide();
                            }
                            ActiveScene = prepared;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(
                                nameof(scene),
                                scene,
                                "Unknown WPF overlay scene.");
                    }
                })
            .ConfigureAwait(false);
        await _dispatcher
            .InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle)
            .Task
            .ConfigureAwait(false);
    }

    internal ValueTask ReleaseAsync(WpfPreparedScene scene)
    {
        if (!scene.TryMarkDisposed())
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(
            InvokeAsync(
                () =>
                {
                    List<Exception>? failures = null;
                    CloseOwnedWindows(_retainedWindows, ref failures);
                    if (ReferenceEquals(ActiveScene, scene))
                    {
                        CloseOwnedWindows(_hostWindows, ref failures);
                        ActiveScene = null;
                    }

                    if (failures is not null)
                    {
                        throw new AggregateException(
                            CloseFailureMessage,
                            failures);
                    }
                }));
    }

    private WpfPreparedScene Prepare(TargetPresentation presentation) =>
        presentation switch
        {
            TargetPresentation.Visible visible =>
                new WpfVisibleScene(
                    this,
                    visible.Map,
                    CreateMonitorScenes(
                        visible.Map,
                        _getDesktop())),
            TargetPresentation.Hidden => new WpfHiddenScene(this),
            _ => throw new ArgumentOutOfRangeException(
                nameof(presentation),
                presentation,
                "Unknown target presentation."),
        };

    private Task<T> InvokeAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        ThrowIfDispatcherUnavailable();
        if (_dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }

        return _dispatcher
            .InvokeAsync(
                action,
                DispatcherPriority.Normal,
                cancellationToken)
            .Task;
    }

    private Task InvokeAsync(Action action)
    {
        ThrowIfDispatcherUnavailable();
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private void ThrowIfDispatcherUnavailable()
    {
        if (_dispatcher.HasShutdownStarted
            || _dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException(
                "The overlay dispatcher is shutting down.");
        }
    }

    private void ActivateVisible(WpfVisibleScene scene)
    {
        var replacements = new Dictionary<HMONITOR, Window>();
        try
        {
            foreach (var monitor in scene.Monitors)
            {
                var host = CreateWindow();
                replacements.Add(monitor.Monitor.Handle, host);
                var windowHandle = OverlayWindow.Position(
                    host,
                    monitor.Monitor.Bounds);
                var projection = new MonitorProjection(
                    monitor.Monitor.Bounds,
                    _readDpi(windowHandle));
                host.Content = new TargetScene(
                    monitor.Monitor,
                    projection,
                    monitor.Targets);
            }

            foreach (var host in replacements.Values)
            {
                host.Show();
            }
        }
        catch (Exception activationFailure)
        {
            try
            {
                CloseWindows(replacements.Values);
            }
            catch (Exception cleanupFailure)
            {
                RetainWindows(replacements.Values);
                throw new AggregateException(
                    "Overlay activation and staged-window cleanup failed.",
                    activationFailure,
                    cleanupFailure);
            }

            throw;
        }

        var replaced = _hostWindows;
        _hostWindows = replacements;
        ActiveScene = scene;
        try
        {
            CloseWindows(replaced.Values);
        }
        catch
        {
            RetainWindows(replaced.Values);
            throw;
        }
    }

    private static ImmutableArray<MonitorScene> CreateMonitorScenes(
        TargetMap map,
        PhysicalDesktop desktop)
    {
        var targets = desktop.Monitors.ToDictionary(
            static monitor => monitor.Handle,
            static _ => ImmutableArray.CreateBuilder<LabeledTarget>());

        foreach (var target in map.Targets)
        {
            var monitor = desktop.FindMonitor(target.Target);
            targets[monitor.Handle].Add(target);
        }

        return desktop.Monitors
            .Select(
                monitor =>
                    new MonitorScene(
                        monitor,
                        targets[monitor.Handle].ToImmutable()))
            .ToImmutableArray();
    }

    private void RetainWindows(IEnumerable<Window> windows)
    {
        foreach (var window in windows)
        {
            if (OverlayWindow.GetHandle(window) is { } handle
                && OverlayWindow.IsOpen(handle))
            {
                _retainedWindows[handle] = window;
            }
        }
    }

    private static void CloseWindows(IEnumerable<Window> windows)
    {
        List<Exception>? failures = null;
        foreach (var window in windows)
        {
            CloseWindow(window, ref failures);
        }

        if (failures is not null)
        {
            throw new AggregateException(
                CloseFailureMessage,
                failures);
        }
    }

    private static void CloseOwnedWindows<TKey>(
        IDictionary<TKey, Window> windows,
        ref List<Exception>? failures)
        where TKey : notnull
    {
        foreach (var (key, window) in windows.ToArray())
        {
            if (CloseWindow(window, ref failures))
            {
                windows.Remove(key);
            }
        }
    }

    private static bool CloseWindow(
        Window window,
        ref List<Exception>? failures)
    {
        var handle = OverlayWindow.GetHandle(window);
        try
        {
            window.Content = null;
            window.Close();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (handle is { } openHandle
            && OverlayWindow.IsOpen(openHandle))
        {
            try
            {
                OverlayWindow.Destroy(openHandle);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        return handle is null || !OverlayWindow.IsOpen(handle.Value);
    }

    private static Window CreateWindow() =>
        new()
        {
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Focusable = false,
            IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
        };
}

internal abstract class WpfPreparedScene(
    WpfOverlayRenderer renderer)
    : IPreparedScene
{
    private int _isDisposed;

    internal WpfOverlayRenderer Renderer { get; } = renderer;

    private bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    public ValueTask DisposeAsync() => Renderer.ReleaseAsync(this);

    internal bool TryMarkDisposed() =>
        Interlocked.Exchange(ref _isDisposed, 1) == 0;

    internal void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(IsDisposed, this);
}

internal sealed class WpfVisibleScene(
    WpfOverlayRenderer renderer,
    TargetMap map,
    ImmutableArray<MonitorScene> monitors)
    : WpfPreparedScene(renderer)
{
    internal TargetMap Map { get; } = map;

    internal ImmutableArray<MonitorScene> Monitors { get; } = monitors;
}

internal sealed class WpfHiddenScene(WpfOverlayRenderer renderer)
    : WpfPreparedScene(renderer);

internal sealed record MonitorScene(
    PhysicalMonitor Monitor,
    ImmutableArray<LabeledTarget> Targets);

internal sealed class TargetScene : Canvas
{
    internal TargetScene(
        PhysicalMonitor monitor,
        MonitorProjection projection,
        ImmutableArray<LabeledTarget> targets)
    {
        Monitor = monitor;
        Projection = projection;
        Width = projection.Size.Width;
        Height = projection.Size.Height;
        IsHitTestVisible = false;

        foreach (var target in targets)
        {
            var badge = new TargetBadge(target);
            var origin = projection.Project(
                target.Target.Bounds.Origin);
            SetLeft(badge, origin.X);
            SetTop(badge, origin.Y);
            Children.Add(badge);
        }
    }

    internal PhysicalMonitor Monitor { get; }

    internal MonitorProjection Projection { get; }
}

internal sealed class TargetBadge : Border
{
    internal TargetBadge(LabeledTarget target)
    {
        Target = target;
        Background = Brushes.Black;
        BorderBrush = Brushes.White;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(3);
        Padding = new Thickness(4, 2, 4, 2);
        SnapsToDevicePixels = true;
        Child = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Text = target.Label.Value,
        };
    }

    internal LabeledTarget Target { get; }
}