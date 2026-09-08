using Desknav.ControlPlane;

namespace Desknav.UIAutomation.Tests;

public sealed class WindowsTargetDiscoveryTests
{
    [Fact]
    public async Task MapsEligibleElementsToOutwardRoundedPhysicalTargets()
    {
        var eligible = Element(
            PhysicalBounds.TryCreate(
                10.25,
                -20.75,
                30.5,
                40.5),
            exclusion: null);
        var capture = Capture(
            eligible,
            Element(
                PhysicalBounds.TryCreate(100, 200, 300, 400),
                UiAutomationExclusion.Disabled));
        var discovery = Discovery(
            _ => Task.FromResult(capture));

        var result = await discovery.DiscoverAsync(
            TestContext.Current.CancellationToken);

        var succeeded =
            Assert.IsType<TargetDiscoveryResult.Succeeded>(result);
        var target = Assert.Single(succeeded.Targets);
        Assert.NotEqual(Guid.Empty, target.Id.Value);
        Assert.Equal(
            new PhysicalRect(10, -21, 31, 41),
            target.Bounds);
    }

    [Fact]
    public async Task AllocatesFreshTargetIdentitiesForEachCapture()
    {
        var capture = Capture(
            Element(
                PhysicalBounds.TryCreate(10, 20, 30, 40),
                exclusion: null));
        var discovery = Discovery(
            _ => Task.FromResult(capture));

        var first = Assert.IsType<TargetDiscoveryResult.Succeeded>(
            await discovery.DiscoverAsync(
                TestContext.Current.CancellationToken));
        var second = Assert.IsType<TargetDiscoveryResult.Succeeded>(
            await discovery.DiscoverAsync(
                TestContext.Current.CancellationToken));

        Assert.NotEqual(
            Assert.Single(first.Targets).Id,
            Assert.Single(second.Targets).Id);
    }

    [Fact]
    public async Task ForegroundCaptureFailureIsExpectedDiscoveryFailure()
    {
        var scanner = new WindowsTargetScanner(
            maximumDepth: 128,
            maximumElements: 10_000,
            foregroundWindow: static () => 0);
        var discovery = new WindowsTargetDiscovery(scanner);

        var result = await discovery.DiscoverAsync(
            TestContext.Current.CancellationToken);

        Assert.IsType<TargetDiscoveryResult.Failed>(result);
    }

    [Fact]
    public async Task UnrepresentableEligibleBoundsFailTheWholeDiscovery()
    {
        var capture = Capture(
            Element(
                PhysicalBounds.TryCreate(
                    int.MinValue,
                    0,
                    uint.MaxValue,
                    10),
                exclusion: null));
        var discovery = Discovery(
            _ => Task.FromResult(capture));

        var result = await discovery.DiscoverAsync(
            TestContext.Current.CancellationToken);

        Assert.IsType<TargetDiscoveryResult.Failed>(result);
    }

    [Fact]
    public async Task CancellationIsNotTranslatedToDiscoveryFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var discovery = Discovery(
            token => Task.FromCanceled<UiAutomationCapture>(token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discovery.DiscoverAsync(cancellation.Token));
    }

    [Fact]
    public async Task UnexpectedCaptureFailurePropagates()
    {
        var discovery = Discovery(
            _ => Task.FromException<UiAutomationCapture>(
                new InvalidOperationException(
                    "Unexpected capture failure.")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => discovery.DiscoverAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal("Unexpected capture failure.", exception.Message);
    }

    private static WindowsTargetDiscovery Discovery(
        Func<CancellationToken, Task<UiAutomationCapture>> capture) =>
        new(new FakeTargetScanner(capture));

    private static UiAutomationCapture Capture(
        params UiAutomationElementCapture[] elements) =>
        new(
            new UiAutomationWindowCapture(
                WindowHandle: 1,
                ProcessId: 2,
                Name: "Test window",
                IsMinimized: false),
            [.. elements]);

    private static UiAutomationElementCapture Element(
        PhysicalBounds? bounds,
        UiAutomationExclusion? exclusion) =>
        new(
            TreePath: [0],
            RuntimeId: [1],
            AutomationId: "target",
            Name: "Target",
            ControlType: "ControlType.Button",
            ProcessId: 2,
            NativeWindowHandle: null,
            Bounds: bounds,
            IsEnabled: true,
            IsOffscreen: false,
            Actions: [UiAutomationAction.Invoke],
            Exclusion: exclusion,
            UnavailableProperties: []);

    private sealed class FakeTargetScanner(
        Func<CancellationToken, Task<UiAutomationCapture>> capture)
        : ITargetScanner
    {
        public Task<UiAutomationCapture> CaptureForegroundWindowAsync(
            CancellationToken cancellationToken = default) =>
            capture(cancellationToken);
    }
}