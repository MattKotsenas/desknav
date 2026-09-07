using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

using Desknav.ControlPlane;

namespace Desknav.UI.Wpf;

internal readonly record struct MonitorHandle
    : IComparable<MonitorHandle>
{
    // HMONITOR is borrowed; Windows exposes no release operation.
    private readonly nint _value;

    internal MonitorHandle(nint value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _value = value;
    }

    internal nint ToNative() => _value;

    public int CompareTo(MonitorHandle other) =>
        _value.ToInt64().CompareTo(other._value.ToInt64());
}

internal readonly record struct WindowHandle
{
    // The WPF Window owns the HWND lifetime.
    private readonly nint _value;

    internal WindowHandle(nint value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _value = value;
    }

    internal nint ToNative() => _value;
}

internal readonly record struct PhysicalMonitor
{
    internal PhysicalMonitor(
        MonitorHandle handle,
        PhysicalRect bounds)
    {
        if (handle == default)
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

    internal MonitorHandle Handle { get; }

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
            .ThenBy(static monitor => monitor.Handle)
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

internal static partial class MonitorTopology
{
    private static readonly MonitorEnumerationCallback Callback =
        AddMonitor;

    internal static PhysicalDesktop GetCurrent() =>
        PerMonitorDpi.Run(GetCurrentCore);

    private static PhysicalDesktop GetCurrentCore()
    {
        var state = new MonitorEnumerationState();
        var stateHandle = GCHandle.Alloc(state);
        try
        {
            var callback =
                Marshal.GetFunctionPointerForDelegate(Callback);
            if (!NativeMethods.EnumDisplayMonitors(
                    0,
                    0,
                    callback,
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

    private static bool AddMonitor(
        nint monitor,
        nint deviceContext,
        ref NativeRectangle bounds,
        nint statePointer)
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
                    new MonitorHandle(monitor),
                    new PhysicalRect(
                        bounds.Left,
                        bounds.Top,
                        checked(bounds.Right - bounds.Left),
                        checked(bounds.Bottom - bounds.Top))));
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumerationCallback(
        nint monitor,
        nint deviceContext,
        ref NativeRectangle bounds,
        nint state);

    private sealed class MonitorEnumerationState
    {
        internal ImmutableArray<PhysicalMonitor>.Builder Monitors { get; } =
            ImmutableArray.CreateBuilder<PhysicalMonitor>();

        internal Exception? Failure { get; set; }
    }
}

internal delegate DpiScale WindowDpiReader(WindowHandle window);

internal static class WindowDpi
{
    private const double DefaultDpi = 96;

    internal static DpiScale GetScale(WindowHandle window)
    {
        var dpi = NativeMethods.GetDpiForWindow(window.ToNative());
        return dpi == 0
            ? throw new InvalidOperationException(
                "Windows could not determine the overlay window DPI.")
            : new DpiScale(dpi / DefaultDpi, dpi / DefaultDpi);
    }
}

internal static class OverlayWindow
{
    internal static WindowHandle Position(
        Window window,
        PhysicalRect bounds) =>
        PerMonitorDpi.Run(
            () =>
            {
                var handle = new WindowHandle(
                    new WindowInteropHelper(window).EnsureHandle());
                if (!NativeMethods.SetWindowPos(
                        handle.ToNative(),
                        0,
                        (int)bounds.Left,
                        (int)bounds.Top,
                        (int)bounds.Width,
                        (int)bounds.Height,
                        SetWindowPositionFlags.NoActivate
                            | SetWindowPositionFlags.NoZOrder))
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError());
                }

                return handle;
            });

    internal static WindowHandle? GetHandle(Window window)
    {
        var value = new WindowInteropHelper(window).Handle;
        return value == 0 ? null : new WindowHandle(value);
    }

    internal static bool IsOpen(WindowHandle window) =>
        NativeMethods.IsWindow(window.ToNative());

    internal static void Destroy(WindowHandle window)
    {
        if (!NativeMethods.DestroyWindow(window.ToNative()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}

internal static class PerMonitorDpi
{
    private static readonly nint PerMonitorAwareV2 = new(-4);

    internal static T Run<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var previous = NativeMethods.SetThreadDpiAwarenessContext(
            PerMonitorAwareV2);
        if (previous == 0)
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

    private static Exception? Restore(nint previous)
    {
        if (NativeMethods.SetThreadDpiAwarenessContext(previous) != 0)
        {
            return null;
        }

        return new InvalidOperationException(
            "Windows failed to restore the overlay thread's"
            + " DPI awareness context.");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRectangle
{
    internal int Left;

    internal int Top;

    internal int Right;

    internal int Bottom;
}

[Flags]
internal enum SetWindowPositionFlags : uint
{
    NoZOrder = 0x0004,
    NoActivate = 0x0010,
}

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        nint callback,
        nint state);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint window,
        nint insertAfter,
        int left,
        int top,
        int width,
        int height,
        SetWindowPositionFlags flags);

    [LibraryImport("user32.dll")]
    internal static partial nint SetThreadDpiAwarenessContext(
        nint dpiContext);
}