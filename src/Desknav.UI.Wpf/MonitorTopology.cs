using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;

using Desknav.ControlPlane;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Desknav.UI.Wpf;

internal readonly record struct PhysicalMonitor
{
    internal PhysicalMonitor(
        HMONITOR handle,
        PhysicalRect bounds)
    {
        if (handle.IsNull)
        {
            throw new ArgumentException(
                "A monitor handle must be initialized.",
                nameof(handle));
        }

        if (bounds == default)
        {
            throw new ArgumentException(
                "Monitor bounds must be initialized.",
                nameof(bounds));
        }

        Handle = handle;
        Bounds = bounds;
    }

    internal HMONITOR Handle { get; }

    internal PhysicalRect Bounds { get; }
}

internal sealed class PhysicalDesktop
{
    internal PhysicalDesktop(
        ImmutableArray<PhysicalMonitor> monitors)
    {
        if (monitors.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                "The overlay requires at least one display.");
        }

        if (monitors.Any(static monitor => monitor == default))
        {
            throw new InvalidOperationException(
                "Every overlay display must be initialized.");
        }

        if (monitors
                .Select(static monitor => monitor.Handle)
                .Distinct()
                .Count()
            != monitors.Length)
        {
            throw new InvalidOperationException(
                "The overlay cannot contain duplicate displays.");
        }

        Monitors = monitors
            .OrderBy(static monitor => monitor.Bounds)
            .ThenBy(
                static monitor =>
                    ((nint)monitor.Handle).ToInt64())
            .ToImmutableArray();
    }

    internal ImmutableArray<PhysicalMonitor> Monitors { get; }

    internal PhysicalMonitor FindMonitor(DesktopTarget target)
    {
        foreach (var monitor in Monitors)
        {
            if (monitor.Bounds.Contains(target.Bounds.Origin))
            {
                return monitor;
            }
        }

        PhysicalMonitor? selected = null;
        long selectedArea = 0;
        foreach (var monitor in Monitors)
        {
            var area = monitor.Bounds.IntersectionArea(target.Bounds);
            if (area > selectedArea)
            {
                selected = monitor;
                selectedArea = area;
            }
        }

        return selected
            ?? throw new InvalidOperationException(
                $"Target {target.Id} does not intersect"
                + " a current display.");
    }
}

internal readonly record struct MonitorProjection
{
    internal MonitorProjection(
        PhysicalRect monitorBounds,
        DpiScale scale)
    {
        if (monitorBounds == default)
        {
            throw new ArgumentException(
                "Monitor bounds must be initialized.",
                nameof(monitorBounds));
        }

        if (!double.IsFinite(scale.DpiScaleX)
            || scale.DpiScaleX <= 0
            || !double.IsFinite(scale.DpiScaleY)
            || scale.DpiScaleY <= 0)
        {
            throw new InvalidOperationException(
                "Windows reported an invalid overlay DPI scale.");
        }

        MonitorBounds = monitorBounds;
        Scale = scale;
    }

    internal PhysicalRect MonitorBounds { get; }

    internal DpiScale Scale { get; }

    internal Size Size =>
        new(
            MonitorBounds.Width / Scale.DpiScaleX,
            MonitorBounds.Height / Scale.DpiScaleY);

    internal Point Project(PhysicalPoint point)
    {
        var offset =
            point.ClampToMinimum(MonitorBounds.Origin)
            - MonitorBounds.Origin;
        return new Point(
            offset.X / Scale.DpiScaleX,
            offset.Y / Scale.DpiScaleY);
    }
}

internal static unsafe class MonitorTopology
{
    private static readonly MONITORENUMPROC Callback =
        AddMonitor;

    internal static PhysicalDesktop GetCurrent() =>
        PerMonitorDpi.Run(GetCurrentCore);

    private static PhysicalDesktop GetCurrentCore()
    {
        var state = new MonitorEnumerationState();
        var stateHandle = GCHandle.Alloc(state);
        try
        {
            if (!PInvoke.EnumDisplayMonitors(
                    default,
                    null,
                    Callback,
                    GCHandle.ToIntPtr(stateHandle)))
            {
                if (state.Failure is { } failure)
                {
                    throw failure;
                }

                throw new Win32Exception(
                    Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            stateHandle.Free();
        }

        return new PhysicalDesktop(state.Monitors.ToImmutable());
    }

    private static BOOL AddMonitor(
        HMONITOR monitor,
        HDC deviceContext,
        RECT* bounds,
        LPARAM statePointer)
    {
        _ = deviceContext;
        try
        {
            var state = (MonitorEnumerationState?)
                GCHandle.FromIntPtr(statePointer).Target
                ?? throw new InvalidOperationException(
                    "Monitor enumeration lost its capture state.");
            state.Monitors.Add(
                new PhysicalMonitor(
                    monitor,
                    new PhysicalRect(
                        bounds->left,
                        bounds->top,
                        checked(bounds->right - bounds->left),
                        checked(bounds->bottom - bounds->top))));
            return true;
        }
        catch (Exception exception)
        {
            var state = (MonitorEnumerationState?)
                GCHandle.FromIntPtr(statePointer).Target;
            if (state is not null)
            {
                state.Failure = exception;
            }

            return false;
        }
    }

    private sealed class MonitorEnumerationState
    {
        internal ImmutableArray<PhysicalMonitor>.Builder Monitors { get; } =
            ImmutableArray.CreateBuilder<PhysicalMonitor>();

        internal Exception? Failure { get; set; }
    }
}

internal delegate DpiScale WindowDpiReader(HWND window);

internal static class WindowDpi
{
    private const double DefaultDpi = 96;

    internal static DpiScale GetScale(HWND window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            throw new PlatformNotSupportedException(
                "Per-monitor overlay DPI requires Windows 10 version 1607.");
        }

        var dpi = PInvoke.GetDpiForWindow(window);
        return dpi == 0
            ? throw new InvalidOperationException(
                "Windows could not determine the overlay window DPI.")
            : new DpiScale(dpi / DefaultDpi, dpi / DefaultDpi);
    }
}

internal static class OverlayWindow
{
    internal static HWND Position(
        Window window,
        PhysicalRect bounds) =>
        PerMonitorDpi.Run(
            () =>
            {
                var handle = (HWND)new WindowInteropHelper(window)
                    .EnsureHandle();
                if (!PInvoke.SetWindowPos(
                        handle,
                        default,
                        bounds.Left,
                        bounds.Top,
                        bounds.Width,
                        bounds.Height,
                        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
                            | SET_WINDOW_POS_FLAGS.SWP_NOZORDER))
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError());
                }

                return handle;
            });

    internal static HWND? GetHandle(Window window)
    {
        var value = new WindowInteropHelper(window).Handle;
        return value == 0 ? null : (HWND)value;
    }

    internal static bool IsOpen(HWND window) =>
        PInvoke.IsWindow(window);

    internal static void Destroy(HWND window)
    {
        if (!PInvoke.DestroyWindow(window))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}

internal static class PerMonitorDpi
{
    internal static T Run<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            throw new PlatformNotSupportedException(
                "Per-monitor overlays require Windows 10 version 1607.");
        }

        var previous = PInvoke.SetThreadDpiAwarenessContext(
            DPI_AWARENESS_CONTEXT
                .DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previous.IsNull)
        {
            throw new InvalidOperationException(
                "Windows refused the overlay thread's per-monitor"
                + " DPI awareness context.");
        }

        T result;
        try
        {
            result = operation();
        }
        catch (Exception operationFailure)
        {
            var restorationFailure = Restore(previous);
            if (restorationFailure is null)
            {
                throw;
            }

            throw new AggregateException(
                "The per-monitor DPI operation and context"
                + " restoration both failed.",
                operationFailure,
                restorationFailure);
        }

        var failure = Restore(previous);
        return failure is null
            ? result
            : throw failure;
    }

    [SupportedOSPlatform("windows10.0.14393")]
    private static Exception? Restore(
        DPI_AWARENESS_CONTEXT previous)
    {
        if (!PInvoke
                .SetThreadDpiAwarenessContext(previous)
                .IsNull)
        {
            return null;
        }

        return new InvalidOperationException(
            "Windows failed to restore the overlay thread's"
            + " DPI awareness context.");
    }
}