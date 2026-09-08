using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

using Desknav.ControlPlane;

namespace Desknav.UIAutomation;

/// <summary>
/// Maps an eligible foreground-window UI Automation capture into physical
/// desktop targets.
/// </summary>
public sealed class WindowsTargetDiscovery : ITargetDiscovery
{
    private readonly ITargetScanner _scanner;

    public WindowsTargetDiscovery(ITargetScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
    }

    public async Task<TargetDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        UiAutomationCapture capture;
        try
        {
            capture = await _scanner
                .CaptureForegroundWindowAsync(cancellationToken)
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
                    mapped.Value));
        }

        return new TargetDiscoveryResult.Succeeded(
            targets.ToImmutable());
    }

    private static bool TryMap(
        PhysicalBounds bounds,
        [NotNullWhen(true)] out PhysicalRect? mapped)
    {
        var edges = bounds.OutwardEdges;
        var width = (long)edges.Right - edges.Left;
        var height = (long)edges.Bottom - edges.Top;
        if (width > int.MaxValue || height > int.MaxValue)
        {
            mapped = null;
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