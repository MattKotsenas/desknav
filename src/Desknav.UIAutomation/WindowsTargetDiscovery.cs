using System.Collections.Immutable;

using Desknav.ControlPlane;

namespace Desknav.UIAutomation;

/// <summary>
/// Maps an eligible foreground-window UI Automation capture into physical
/// desktop targets.
/// </summary>
public sealed class WindowsTargetDiscovery : ITargetDiscovery
{
    private readonly Func<CancellationToken, Task<UiAutomationCapture>> _capture;

    public WindowsTargetDiscovery()
        : this(new WindowsTargetScanner())
    {
    }

    public WindowsTargetDiscovery(WindowsTargetScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _capture = scanner.CaptureForegroundWindowAsync;
    }

    internal WindowsTargetDiscovery(
        Func<CancellationToken, Task<UiAutomationCapture>> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _capture = capture;
    }

    public async Task<TargetDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        UiAutomationCapture capture;
        try
        {
            capture = await _capture(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UiAutomationCaptureException)
        {
            return new TargetDiscoveryResult.Failed();
        }

        var targets =
            ImmutableArray.CreateBuilder<DesktopTarget>();
        foreach (var element in capture.Elements)
        {
            if (!element.IsEligible)
            {
                continue;
            }

            if (element.Bounds is not { } bounds
                || !TryMap(bounds, out var mapped))
            {
                return new TargetDiscoveryResult.Failed();
            }

            targets.Add(
                new DesktopTarget(
                    TargetId.New(),
                    mapped));
        }

        return new TargetDiscoveryResult.Succeeded(
            targets.ToImmutable());
    }

    private static bool TryMap(
        PhysicalBounds bounds,
        out PhysicalRect mapped)
    {
        var edges = bounds.OutwardEdges;
        var width = (long)edges.Right - edges.Left;
        var height = (long)edges.Bottom - edges.Top;
        if (width > int.MaxValue || height > int.MaxValue)
        {
            mapped = default;
            return false;
        }

        mapped = new PhysicalRect(
            edges.Left,
            edges.Top,
            (int)width,
            (int)height);
        return true;
    }
}