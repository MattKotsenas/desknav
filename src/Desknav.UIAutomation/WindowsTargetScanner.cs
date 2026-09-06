using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace Desknav.UIAutomation;

public sealed partial class WindowsTargetScanner
{
    private const int DefaultMaximumDepth = 128;
    private const int DefaultMaximumElements = 10_000;
    private static readonly nint PerMonitorAwareV2 = new(-4);
    private readonly Func<nint> _foregroundWindow;
    private readonly int _maximumDepth;
    private readonly int _maximumElements;

    public WindowsTargetScanner()
        : this(
            DefaultMaximumDepth,
            DefaultMaximumElements,
            NativeMethods.GetForegroundWindow)
    {
    }

    internal WindowsTargetScanner(
        int maximumDepth,
        int maximumElements,
        Func<nint>? foregroundWindow = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumElements);

        _maximumDepth = maximumDepth;
        _maximumElements = maximumElements;
        _foregroundWindow =
            foregroundWindow ?? NativeMethods.GetForegroundWindow;
    }

    public Task<UiAutomationCapture> CaptureForegroundWindowAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<UiAutomationCapture>(
                cancellationToken);
        }

        var windowHandle = _foregroundWindow();
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<UiAutomationCapture>(
                cancellationToken);
        }

        if (windowHandle == 0)
        {
            return Task.FromException<UiAutomationCapture>(
                new InvalidOperationException(
                    "Windows did not report a foreground window."));
        }

        return CaptureWindowAsync(
            windowHandle,
            cancellationToken);
    }

    public Task<UiAutomationCapture> CaptureWindowAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowHandle),
                windowHandle,
                "A window handle cannot be zero.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<UiAutomationCapture>(
                cancellationToken);
        }

        // Desktop UIA calls belong on an MTA worker, not the caller's
        // UI thread.
        return Task.Run(
            () => CaptureWindow(windowHandle, cancellationToken),
            cancellationToken);
    }

    private UiAutomationCapture CaptureWindow(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var dpiAwareness = DpiAwarenessScope.Enter();
        var root = AutomationElement.FromHandle(windowHandle)
            ?? throw new InvalidOperationException(
                $"UI Automation could not find window {windowHandle}.");
        var isMinimized = NativeMethods.IsIconic(windowHandle);
        var window = new UiAutomationWindowCapture(
            windowHandle.ToInt64(),
            root.Current.ProcessId,
            root.Current.Name,
            isMinimized);
        var elements =
            ImmutableArray.CreateBuilder<UiAutomationElementCapture>();
        var cacheRequest = CreateCacheRequest();

        CaptureChildren(
            root,
            [],
            elements,
            cacheRequest,
            isMinimized,
            cancellationToken);

        return new UiAutomationCapture(
            window,
            elements.ToImmutable());
    }

    private void CaptureChildren(
        AutomationElement parent,
        ImmutableArray<int> parentPath,
        ImmutableArray<UiAutomationElementCapture>.Builder elements,
        CacheRequest cacheRequest,
        bool isWindowMinimized,
        CancellationToken cancellationToken)
    {
        if (parentPath.Length >= _maximumDepth)
        {
            throw new InvalidOperationException(
                $"The UI Automation tree exceeded {_maximumDepth} levels.");
        }

        var walker = TreeWalker.ControlViewWalker;
        var child = walker.GetFirstChild(parent, cacheRequest);
        var childIndex = 0;
        while (child is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elements.Count >= _maximumElements)
            {
                throw new InvalidOperationException(
                    $"The UI Automation tree exceeded {_maximumElements}"
                    + " control elements.");
            }

            var path = parentPath.Add(childIndex);
            elements.Add(CaptureElement(
                child,
                path,
                isWindowMinimized));
            CaptureChildren(
                child,
                path,
                elements,
                cacheRequest,
                isWindowMinimized,
                cancellationToken);
            child = walker.GetNextSibling(child, cacheRequest);
            childIndex++;
        }
    }

    private static UiAutomationElementCapture CaptureElement(
        AutomationElement element,
        ImmutableArray<int> path,
        bool isWindowMinimized)
    {
        var unavailable =
            ImmutableArray.CreateBuilder<string>();
        var runtimeId = ReadReference<int[]>(
            element,
            AutomationElement.RuntimeIdProperty,
            unavailable);
        var automationId = ReadReference<string>(
            element,
            AutomationElement.AutomationIdProperty,
            unavailable);
        var name = ReadReference<string>(
            element,
            AutomationElement.NameProperty,
            unavailable);
        var controlType = ReadReference<ControlType>(
            element,
            AutomationElement.ControlTypeProperty,
            unavailable);
        var processId = ReadValue<int>(
            element,
            AutomationElement.ProcessIdProperty,
            unavailable);
        var nativeWindowHandle = ReadValue<int>(
            element,
            AutomationElement.NativeWindowHandleProperty,
            unavailable);
        var rectangle = ReadValue<Rect>(
            element,
            AutomationElement.BoundingRectangleProperty,
            unavailable);
        var isEnabled = ReadValue<bool>(
            element,
            AutomationElement.IsEnabledProperty,
            unavailable);
        var isOffscreen = ReadValue<bool>(
            element,
            AutomationElement.IsOffscreenProperty,
            unavailable);

        var actions = ReadActions(element, unavailable);
        PhysicalBounds? bounds = rectangle is { } value
            ? PhysicalBounds.TryCreate(
                value.Left,
                value.Top,
                value.Width,
                value.Height)
            : null;
        var exclusion = Classify(
            bounds,
            rectangle is null,
            isEnabled,
            isOffscreen,
            isWindowMinimized,
            actions);

        return new UiAutomationElementCapture(
            path,
            runtimeId is null ? [] : [.. runtimeId],
            automationId,
            name,
            controlType?.ProgrammaticName,
            processId,
            nativeWindowHandle,
            bounds,
            isEnabled,
            isOffscreen,
            actions,
            exclusion,
            unavailable.ToImmutable());
    }

    private static ImmutableArray<UiAutomationAction> ReadActions(
        AutomationElement element,
        ImmutableArray<string>.Builder unavailable)
    {
        var actions =
            ImmutableArray.CreateBuilder<UiAutomationAction>();
        AddAction(
            element,
            AutomationElement.IsInvokePatternAvailableProperty,
            UiAutomationAction.Invoke,
            actions,
            unavailable);
        AddAction(
            element,
            AutomationElement.IsTogglePatternAvailableProperty,
            UiAutomationAction.Toggle,
            actions,
            unavailable);
        AddAction(
            element,
            AutomationElement.IsSelectionItemPatternAvailableProperty,
            UiAutomationAction.SelectionItem,
            actions,
            unavailable);
        AddExpandCollapseAction(
            element,
            actions,
            unavailable);
        return actions.ToImmutable();
    }

    private static void AddAction(
        AutomationElement element,
        AutomationProperty property,
        UiAutomationAction action,
        ImmutableArray<UiAutomationAction>.Builder actions,
        ImmutableArray<string>.Builder unavailable)
    {
        if (ReadValue<bool>(
                element,
                property,
                unavailable)
            is true)
        {
            actions.Add(action);
        }
    }

    private static void AddExpandCollapseAction(
        AutomationElement element,
        ImmutableArray<UiAutomationAction>.Builder actions,
        ImmutableArray<string>.Builder unavailable)
    {
        if (ReadValue<bool>(
                element,
                AutomationElement.IsExpandCollapsePatternAvailableProperty,
                unavailable)
            is not true)
        {
            return;
        }

        var state = ReadValue<ExpandCollapseState>(
            element,
            ExpandCollapsePattern.ExpandCollapseStateProperty,
            unavailable);
        if (state is not null
            && state != ExpandCollapseState.LeafNode)
        {
            actions.Add(UiAutomationAction.ExpandCollapse);
        }
    }

    private static UiAutomationExclusion? Classify(
        PhysicalBounds? bounds,
        bool boundsPropertyUnavailable,
        bool? isEnabled,
        bool? isOffscreen,
        bool isWindowMinimized,
        ImmutableArray<UiAutomationAction> actions)
    {
        if (boundsPropertyUnavailable
            || isEnabled is null
            || isOffscreen is null)
        {
            return UiAutomationExclusion.PropertyUnavailable;
        }

        if (!isEnabled.Value)
        {
            return UiAutomationExclusion.Disabled;
        }

        if (isWindowMinimized)
        {
            return UiAutomationExclusion.WindowMinimized;
        }

        if (isOffscreen.Value)
        {
            return UiAutomationExclusion.Offscreen;
        }

        if (bounds is null)
        {
            return UiAutomationExclusion.InvalidBounds;
        }

        if (!IntersectsDisplay(bounds.Value))
        {
            return UiAutomationExclusion.OutsideDisplays;
        }

        return actions.IsEmpty
            ? UiAutomationExclusion.NoSupportedAction
            : null;
    }

    private static bool IntersectsDisplay(PhysicalBounds bounds)
    {
        var rectangle = new NativeRectangle(
            (int)Math.Floor(bounds.Left),
            (int)Math.Floor(bounds.Top),
            (int)Math.Ceiling(bounds.Right),
            (int)Math.Ceiling(bounds.Bottom));
        return NativeMethods.MonitorFromRect(
                ref rectangle,
                flags: 0)
            != 0;
    }

    private static T? ReadReference<T>(
        AutomationElement element,
        AutomationProperty property,
        ImmutableArray<string>.Builder unavailable)
        where T : class
    {
        var value = element.GetCachedPropertyValue(
            property,
            ignoreDefaultValue: true);
        if (ReferenceEquals(value, AutomationElement.NotSupported))
        {
            unavailable.Add(property.ProgrammaticName);
            return default;
        }

        return (T)value;
    }

    private static T? ReadValue<T>(
        AutomationElement element,
        AutomationProperty property,
        ImmutableArray<string>.Builder unavailable)
        where T : struct
    {
        var value = element.GetCachedPropertyValue(
            property,
            ignoreDefaultValue: true);
        if (ReferenceEquals(value, AutomationElement.NotSupported))
        {
            unavailable.Add(property.ProgrammaticName);
            return null;
        }

        return (T)value;
    }

    private static CacheRequest CreateCacheRequest()
    {
        var request = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.Full,
            TreeFilter = Automation.ControlViewCondition,
            TreeScope = TreeScope.Element,
        };
        request.Add(AutomationElement.RuntimeIdProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.ProcessIdProperty);
        request.Add(AutomationElement.NativeWindowHandleProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.IsEnabledProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.IsInvokePatternAvailableProperty);
        request.Add(AutomationElement.IsTogglePatternAvailableProperty);
        request.Add(
            AutomationElement.IsSelectionItemPatternAvailableProperty);
        request.Add(
            AutomationElement.IsExpandCollapsePatternAvailableProperty);
        request.Add(
            ExpandCollapsePattern.ExpandCollapseStateProperty);
        return request;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public NativeRectangle(
            int left,
            int top,
            int right,
            int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsIconic(nint windowHandle);

        [LibraryImport("user32.dll")]
        internal static partial nint MonitorFromRect(
            ref NativeRectangle rectangle,
            uint flags);

        [LibraryImport("user32.dll")]
        internal static partial nint SetThreadDpiAwarenessContext(
            nint dpiContext);
    }

    private readonly struct DpiAwarenessScope(nint previous)
        : IDisposable
    {
        public static DpiAwarenessScope Enter()
        {
            var previous = NativeMethods.SetThreadDpiAwarenessContext(
                PerMonitorAwareV2);
            return previous == 0
                ? throw new InvalidOperationException(
                    "Windows refused the UI Automation thread's"
                    + " per-monitor DPI awareness context.")
                : new DpiAwarenessScope(previous);
        }

        public void Dispose()
        {
            if (NativeMethods.SetThreadDpiAwarenessContext(previous) == 0)
            {
                throw new InvalidOperationException(
                    "Windows failed to restore the UI Automation"
                    + " thread's DPI awareness context.");
            }
        }
    }
}

public sealed record UiAutomationCapture(
    UiAutomationWindowCapture Window,
    ImmutableArray<UiAutomationElementCapture> Elements)
{
    public string CoordinateSpace => "physicalPixels";
}

public sealed record UiAutomationWindowCapture(
    long WindowHandle,
    int ProcessId,
    string Name,
    bool IsMinimized);

public sealed record UiAutomationElementCapture(
    ImmutableArray<int> TreePath,
    ImmutableArray<int> RuntimeId,
    string? AutomationId,
    string? Name,
    string? ControlType,
    int? ProcessId,
    int? NativeWindowHandle,
    PhysicalBounds? Bounds,
    bool? IsEnabled,
    bool? IsOffscreen,
    ImmutableArray<UiAutomationAction> Actions,
    UiAutomationExclusion? Exclusion,
    ImmutableArray<string> UnavailableProperties)
{
    public bool IsEligible => Exclusion is null;
}

public readonly record struct PhysicalBounds
{
    internal PhysicalBounds(
        double left,
        double top,
        double width,
        double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    internal double Bottom => Top + Height;

    internal double Right => Left + Width;

    internal static PhysicalBounds? TryCreate(
        double left,
        double top,
        double width,
        double height)
    {
        var right = left + width;
        var bottom = top + height;
        if (!double.IsFinite(left)
            || !double.IsFinite(top)
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || !double.IsFinite(right)
            || !double.IsFinite(bottom)
            || width <= 0
            || height <= 0
            || left < int.MinValue
            || top < int.MinValue
            || right > int.MaxValue
            || bottom > int.MaxValue)
        {
            return null;
        }

        return new PhysicalBounds(left, top, width, height);
    }
}

public enum UiAutomationAction
{
    Invoke,
    Toggle,
    SelectionItem,
    ExpandCollapse,
}

public enum UiAutomationExclusion
{
    PropertyUnavailable,
    Disabled,
    WindowMinimized,
    Offscreen,
    InvalidBounds,
    OutsideDisplays,
    NoSupportedAction,
}