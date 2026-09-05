using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using Desknav.ControlPlane;

namespace Desknav.UI.Wpf;

/// <summary>
/// Builds and atomically activates overlay scenes on one WPF dispatcher.
/// </summary>
public sealed class WpfOverlayRenderer : IOverlayRenderer
{
    private readonly Dispatcher _dispatcher;
    private readonly VirtualDesktopBounds _desktopBounds;

    public WpfOverlayRenderer(
        Dispatcher dispatcher,
        VirtualDesktopBounds desktopBounds)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (desktopBounds == default)
        {
            throw new ArgumentException(
                "Virtual desktop bounds must be initialized.",
                nameof(desktopBounds));
        }

        _dispatcher = dispatcher;
        _desktopBounds = desktopBounds;
    }

    internal Window? HostWindow { get; private set; }

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
                            var window = HostWindow ??= CreateWindow();
                            window.Content = visible.View;
                            if (!window.IsVisible)
                            {
                                window.Show();
                            }
                            break;
                        case WpfHiddenScene:
                            if (HostWindow is { } host)
                            {
                                host.Content = null;
                                host.Hide();
                            }
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(
                                nameof(scene),
                                scene,
                                "Unknown WPF overlay scene.");
                    }

                    ActiveScene = prepared;
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
                    if (ReferenceEquals(ActiveScene, scene))
                    {
                        ActiveScene = null;
                        if (HostWindow is { } host)
                        {
                            host.Content = null;
                            host.Close();
                            HostWindow = null;
                        }
                    }
                }));
    }

    private WpfPreparedScene Prepare(TargetPresentation presentation) =>
        presentation switch
        {
            TargetPresentation.Visible visible =>
                new WpfVisibleScene(
                    this,
                    new TargetScene(visible.Map, _desktopBounds)),
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

    private Window CreateWindow() =>
        new()
        {
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Focusable = false,
            Height = _desktopBounds.Height,
            IsHitTestVisible = false,
            Left = _desktopBounds.Left,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Top = _desktopBounds.Top,
            Topmost = true,
            Width = _desktopBounds.Width,
            WindowStyle = WindowStyle.None,
        };
}

public readonly record struct VirtualDesktopBounds
{
    public VirtualDesktopBounds(
        double left,
        double top,
        double width,
        double height)
    {
        if (!double.IsFinite(left))
        {
            throw new ArgumentOutOfRangeException(nameof(left));
        }

        if (!double.IsFinite(top))
        {
            throw new ArgumentOutOfRangeException(nameof(top));
        }

        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public static VirtualDesktopBounds Current =>
        new(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }
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
    TargetScene view)
    : WpfPreparedScene(renderer)
{
    internal TargetScene View { get; } = view;
}

internal sealed class WpfHiddenScene(WpfOverlayRenderer renderer)
    : WpfPreparedScene(renderer);

internal sealed class TargetScene : Canvas
{
    internal TargetScene(
        TargetMap map,
        VirtualDesktopBounds desktopBounds)
    {
        Map = map;
        Width = desktopBounds.Width;
        Height = desktopBounds.Height;
        IsHitTestVisible = false;

        foreach (var target in map.Targets)
        {
            var badge = new TargetBadge(target);
            SetLeft(
                badge,
                target.Target.Bounds.Left - desktopBounds.Left);
            SetTop(
                badge,
                target.Target.Bounds.Top - desktopBounds.Top);
            Children.Add(badge);
        }
    }

    internal TargetMap Map { get; }
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