using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using Desknav.ControlPlane;

namespace Desknav.UI.Wpf;

/// <summary>
/// Builds and atomically activates overlay scenes on one WPF dispatcher.
/// </summary>
public sealed class WpfOverlayRenderer : IOverlayRenderer
{
    private const string CloseFailureMessage =
        "One or more overlay windows failed to close.";

    private readonly Dispatcher _dispatcher;
    private readonly Func<ImmutableArray<PhysicalMonitor>> _getMonitors;
    private readonly Func<nint, PhysicalMonitor, DpiScale> _getScale;
    // Cleanup-resistant HWNDs stay owned so release can retry them.
    private readonly Dictionary<nint, Window> _retainedWindows = [];
    private Dictionary<nint, Window> _hostWindows = [];

    public WpfOverlayRenderer(Dispatcher dispatcher)
        : this(
            dispatcher,
            MonitorTopology.GetCurrent,
            static (window, _) => WindowDpi.GetScale(window))
    {
    }

    internal WpfOverlayRenderer(
        Dispatcher dispatcher,
        ImmutableArray<PhysicalMonitor> monitors,
        Func<nint, PhysicalMonitor, DpiScale> getScale)
        : this(
            dispatcher,
            () => monitors,
            getScale)
    {
        PhysicalMonitor.Validate(monitors);
    }

    private WpfOverlayRenderer(
        Dispatcher dispatcher,
        Func<ImmutableArray<PhysicalMonitor>> getMonitors,
        Func<nint, PhysicalMonitor, DpiScale> getScale)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(getMonitors);
        ArgumentNullException.ThrowIfNull(getScale);

        _dispatcher = dispatcher;
        _getMonitors = getMonitors;
        _getScale = getScale;
    }

    internal IReadOnlyDictionary<nint, Window> HostWindows =>
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
                        _getMonitors())),
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
        var replacements = new Dictionary<nint, Window>();
        try
        {
            foreach (var monitor in scene.Monitors)
            {
                var host = CreateWindow();
                replacements.Add(monitor.Monitor.Handle, host);
                var windowHandle = PositionWindow(
                    host,
                    monitor.Monitor.Bounds);
                var scale = _getScale(windowHandle, monitor.Monitor);
                ValidateScale(scale);
                host.Content = new TargetScene(
                    monitor.Monitor,
                    scale,
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
        ImmutableArray<PhysicalMonitor> monitors)
    {
        var orderedMonitors = monitors
            .OrderBy(static monitor => monitor.Bounds.Top)
            .ThenBy(static monitor => monitor.Bounds.Left)
            .ThenBy(static monitor => monitor.Handle)
            .ToImmutableArray();
        var targets = orderedMonitors
            .Select(
                static _ =>
                    ImmutableArray.CreateBuilder<LabeledTarget>())
            .ToArray();

        foreach (var target in map.Targets)
        {
            var monitorIndex = FindMonitor(
                target.Target.Bounds,
                orderedMonitors);
            if (monitorIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Target {target.Target.Id} does not intersect"
                    + " a current display.");
            }

            targets[monitorIndex].Add(target);
        }

        return orderedMonitors
            .Select(
                (monitor, index) =>
                    new MonitorScene(
                        monitor,
                        targets[index].ToImmutable()))
            .ToImmutableArray();
    }

    private static int FindMonitor(
        TargetBounds target,
        ImmutableArray<PhysicalMonitor> monitors)
    {
        for (var index = 0; index < monitors.Length; index++)
        {
            if (monitors[index].Bounds.Contains(
                    target.Left,
                    target.Top))
            {
                return index;
            }
        }

        var selectedIndex = -1;
        long selectedArea = 0;
        for (var index = 0; index < monitors.Length; index++)
        {
            var area = monitors[index].Bounds.IntersectionArea(target);
            if (area > selectedArea)
            {
                selectedIndex = index;
                selectedArea = area;
            }
        }

        return selectedIndex;
    }

    private static nint PositionWindow(
        Window window,
        PhysicalMonitorBounds bounds) =>
        DpiAwarenessContext.RunPerMonitorAware(
            () =>
            {
                var handle =
                    new WindowInteropHelper(window).EnsureHandle();
                if (!NativeMethods.SetWindowPos(
                        handle,
                        0,
                        bounds.Left,
                        bounds.Top,
                        bounds.Width,
                        bounds.Height,
                        SetWindowPositionFlags.NoActivate
                            | SetWindowPositionFlags.NoZOrder))
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError());
                }

                return handle;
            });

    private static void ValidateScale(DpiScale scale)
    {
        if (!double.IsFinite(scale.DpiScaleX)
            || scale.DpiScaleX <= 0
            || !double.IsFinite(scale.DpiScaleY)
            || scale.DpiScaleY <= 0)
        {
            throw new InvalidOperationException(
                "Windows reported an invalid overlay DPI scale.");
        }
    }

    private void RetainWindows(IEnumerable<Window> windows)
    {
        foreach (var window in windows)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != 0 && NativeMethods.IsWindow(handle))
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

    private static void CloseOwnedWindows(
        IDictionary<nint, Window> windows,
        ref List<Exception>? failures)
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
        var handle = new WindowInteropHelper(window).Handle;
        try
        {
            window.Content = null;
            window.Close();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (handle != 0
            && NativeMethods.IsWindow(handle)
            && !NativeMethods.DestroyWindow(handle))
        {
            (failures ??= []).Add(
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return handle == 0 || !NativeMethods.IsWindow(handle);
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
        DpiScale scale,
        ImmutableArray<LabeledTarget> targets)
    {
        Monitor = monitor;
        Scale = scale;
        Width = monitor.Bounds.Width / scale.DpiScaleX;
        Height = monitor.Bounds.Height / scale.DpiScaleY;
        IsHitTestVisible = false;

        foreach (var target in targets)
        {
            var badge = new TargetBadge(target);
            SetLeft(
                badge,
                (Math.Max(
                        target.Target.Bounds.Left,
                        monitor.Bounds.Left)
                    - monitor.Bounds.Left)
                / scale.DpiScaleX);
            SetTop(
                badge,
                (Math.Max(
                        target.Target.Bounds.Top,
                        monitor.Bounds.Top)
                    - monitor.Bounds.Top)
                / scale.DpiScaleY);
            Children.Add(badge);
        }
    }

    internal PhysicalMonitor Monitor { get; }

    internal DpiScale Scale { get; }
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