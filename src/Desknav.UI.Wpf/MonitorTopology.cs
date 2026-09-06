using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

using Desknav.ControlPlane;

namespace Desknav.UI.Wpf;

internal readonly record struct PhysicalMonitorBounds
{
    public PhysicalMonitorBounds(
        int left,
        int top,
        int width,
        int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    internal long Right => (long)Left + Width;

    internal long Bottom => (long)Top + Height;

    internal bool Contains(int left, int top) =>
        left >= Left
        && left < Right
        && top >= Top
        && top < Bottom;

    internal long IntersectionArea(TargetBounds target)
    {
        var left = Math.Max((long)Left, target.Left);
        var top = Math.Max((long)Top, target.Top);
        var right = Math.Min(Right, (long)target.Left + target.Width);
        var bottom = Math.Min(
            Bottom,
            (long)target.Top + target.Height);
        return Math.Max(0, right - left)
            * Math.Max(0, bottom - top);
    }
}

internal readonly record struct PhysicalMonitor
{
    public PhysicalMonitor(
        nint handle,
        PhysicalMonitorBounds bounds)
    {
        if (handle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }

        Handle = handle;
        Bounds = bounds;
    }

    public nint Handle { get; }

    public PhysicalMonitorBounds Bounds { get; }

    internal static void Validate(
        ImmutableArray<PhysicalMonitor> monitors)
    {
        if (monitors.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                "The overlay requires at least one display.");
        }

        if (monitors.Any(
                static monitor =>
                    monitor.Handle == 0
                    || monitor.Bounds == default))
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
    }
}

internal static partial class MonitorTopology
{
    private static readonly MonitorEnumerationCallback Callback =
        AddMonitor;

    internal static ImmutableArray<PhysicalMonitor> GetCurrent() =>
        DpiAwarenessContext.RunPerMonitorAware(GetCurrentCore);

    private static ImmutableArray<PhysicalMonitor> GetCurrentCore()
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

        var monitors = state.Monitors.ToImmutable();
        PhysicalMonitor.Validate(monitors);
        return monitors;
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
                    monitor,
                    new PhysicalMonitorBounds(
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

internal static class WindowDpi
{
    private const double DefaultDpi = 96;

    internal static DpiScale GetScale(nint windowHandle)
    {
        var dpi = NativeMethods.GetDpiForWindow(windowHandle);
        return dpi == 0
            ? throw new InvalidOperationException(
                "Windows could not determine the overlay window DPI.")
            : new DpiScale(dpi / DefaultDpi, dpi / DefaultDpi);
    }
}

internal static class DpiAwarenessContext
{
    private static readonly nint PerMonitorAwareV2 = new(-4);

    internal static T RunPerMonitorAware<T>(Func<T> operation)
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
    public int Left;

    public int Top;

    public int Right;

    public int Bottom;
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