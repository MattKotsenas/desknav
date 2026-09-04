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

    public WpfOverlayRenderer(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
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
                    new TargetScene(visible.Snapshot)),
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

    private static Window CreateWindow() =>
        new()
        {
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Focusable = false,
            Height = Math.Max(1, SystemParameters.VirtualScreenHeight),
            IsHitTestVisible = false,
            Left = SystemParameters.VirtualScreenLeft,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Top = SystemParameters.VirtualScreenTop,
            Topmost = true,
            Width = Math.Max(1, SystemParameters.VirtualScreenWidth),
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
    TargetScene view)
    : WpfPreparedScene(renderer)
{
    internal TargetScene View { get; } = view;
}

internal sealed class WpfHiddenScene(WpfOverlayRenderer renderer)
    : WpfPreparedScene(renderer);

internal sealed class TargetScene(TargetSnapshot snapshot) : Canvas
{
    internal TargetSnapshot Snapshot { get; } = snapshot;
}