using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Desknav.ControlPlane;

using Desknav.UIAutomation;

namespace Desknav.UIAutomation.Tests;

public sealed class WindowsTargetScannerTests
{
    [Fact]
    public async Task CapturesActionableControlsFromAnotherProcess()
    {
        await using var fixture = await FixtureProcess.StartAsync(
            TestContext.Current.CancellationToken);
        var scanner = new WindowsTargetScanner();

        var capture = await scanner.CaptureWindowAsync(
            fixture.WindowHandle,
            TestContext.Current.CancellationToken);

        Assert.Equal("physicalPixels", capture.CoordinateSpace);
        Assert.Equal("Desknav UIA Fixture", capture.Window.Name);
        Assert.Equal(fixture.ProcessId, capture.Window.ProcessId);

        var eligible = capture.Elements
            .Where(static element => element.IsEligible)
            .ToDictionary(
                static element =>
                    Assert.IsType<string>(element.AutomationId),
                StringComparer.Ordinal);
        Assert.Contains("expand-target", eligible);
        Assert.Contains("invoke-target", eligible);
        Assert.Contains("selection-target", eligible);
        Assert.Contains("toggle-target", eligible);
        Assert.Equal(
            [UiAutomationAction.Invoke],
            eligible["invoke-target"].Actions);
        Assert.Equal(
            [UiAutomationAction.Toggle],
            eligible["toggle-target"].Actions);
        Assert.Equal(
            [UiAutomationAction.SelectionItem],
            eligible["selection-target"].Actions);
        Assert.Equal(
            [UiAutomationAction.ExpandCollapse],
            eligible["expand-target"].Actions);
        Assert.All(
            eligible.Values,
            static element =>
            {
                var bounds = Assert.IsType<PhysicalBounds>(
                    element.Bounds);
                Assert.True(bounds.Width > 0);
                Assert.True(bounds.Height > 0);
                Assert.Null(element.Exclusion);
            });

        var disabled = Assert.Single(
            capture.Elements,
            static element =>
                element.AutomationId == "disabled-target");
        Assert.False(disabled.IsEligible);
        Assert.Equal(
            UiAutomationExclusion.Disabled,
            disabled.Exclusion);

        var readOnly = Assert.Single(
            capture.Elements,
            static element =>
                element.AutomationId == "read-only-text");
        Assert.False(readOnly.IsEligible);
        Assert.Equal(
            UiAutomationExclusion.NoSupportedAction,
            readOnly.Exclusion);
        var leaf = Assert.Single(
            capture.Elements,
            static element =>
                element.AutomationId == "leaf-target");
        Assert.Empty(leaf.Actions);
        Assert.False(leaf.IsEligible);
        Assert.Equal(
            UiAutomationExclusion.NoSupportedAction,
            leaf.Exclusion);
        Assert.DoesNotContain(
            capture.Elements,
            static element =>
                element.AutomationId == "raw-only-target");

        var secondCapture = await scanner.CaptureWindowAsync(
            fixture.WindowHandle,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            await SerializeAsync(capture),
            await SerializeAsync(secondCapture));
    }

    [Fact]
    public async Task CanceledCaptureDoesNotStart()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var scanner = new WindowsTargetScanner();

        var capture = scanner.CaptureWindowAsync(
            1,
            cancellation.Token);

        Assert.True(capture.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => capture);
    }

    [Fact]
    public async Task CanceledForegroundCaptureDoesNotReadTheDesktop()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var scanner = new WindowsTargetScanner(
            maximumDepth: 128,
            maximumElements: 10_000,
            foregroundWindow: static () => 0);

        var capture = scanner.CaptureForegroundWindowAsync(
            cancellation.Token);

        Assert.True(capture.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => capture);
    }

    [Fact]
    public async Task CancellationAfterSchedulingProducesCanceledTask()
    {
        await using var fixture = await FixtureProcess.StartAsync(
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var scanner = new WindowsTargetScanner();

        var capture = scanner.CaptureWindowAsync(
            fixture.WindowHandle,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => capture);
        Assert.True(capture.IsCanceled);
    }

    [Fact]
    public async Task ForegroundCaptureUsesReportedForegroundWindow()
    {
        await using var fixture = await FixtureProcess.StartAsync(
            TestContext.Current.CancellationToken);
        var scanner = new WindowsTargetScanner(
            maximumDepth: 128,
            maximumElements: 10_000,
            foregroundWindow: () => new nint(fixture.WindowHandle));

        var capture = await scanner.CaptureForegroundWindowAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(fixture.ProcessId, capture.Window.ProcessId);
        Assert.Equal(
            "Desknav UIA Fixture",
            capture.Window.Name);
    }

    [Fact]
    public async Task DiscoveryMapsEligibleForegroundFixtureTargets()
    {
        await using var fixture = await FixtureProcess.StartAsync(
            TestContext.Current.CancellationToken);
        var scanner = new WindowsTargetScanner(
            maximumDepth: 128,
            maximumElements: 10_000,
            foregroundWindow: () => new nint(fixture.WindowHandle));
        var capture = await scanner.CaptureForegroundWindowAsync(
            TestContext.Current.CancellationToken);
        var expected = capture.Elements
            .Where(static element => element.IsEligible)
            .Select(
                static element =>
                    OutwardRect(
                        Assert.IsType<PhysicalBounds>(
                            element.Bounds)))
            .Order()
            .ToArray();
        var discovery = new WindowsTargetDiscovery(scanner);

        var result = await discovery.DiscoverAsync(
            TestContext.Current.CancellationToken);

        var succeeded =
            Assert.IsType<TargetDiscoveryResult.Succeeded>(result);
        Assert.Equal(
            expected,
            succeeded.Targets
                .Select(static target => target.Bounds)
                .Order()
                .ToArray());
        Assert.Equal(
            succeeded.Targets.Length,
            succeeded.Targets
                .Select(static target => target.Id)
                .Distinct()
                .Count());
    }

    [Fact]
    public async Task MinimizedWindowControlsAreIneligible()
    {
        await using var fixture = await FixtureProcess.StartAsync(
            TestContext.Current.CancellationToken);
        await fixture.SendCommandAsync(
            "minimize",
            "minimized",
            TestContext.Current.CancellationToken);
        var scanner = new WindowsTargetScanner();

        var capture = await scanner.CaptureWindowAsync(
            fixture.WindowHandle,
            TestContext.Current.CancellationToken);

        Assert.True(capture.Window.IsMinimized);
        var target = Assert.Single(
            capture.Elements,
            static element =>
                element.AutomationId == "invoke-target");
        Assert.False(target.IsEligible);
        Assert.Equal(
            UiAutomationExclusion.WindowMinimized,
            target.Exclusion);
    }

    [Fact]
    public async Task ControlsOutsideDisplaysAreIneligible()
    {
        await using var fixture = await FixtureProcess.StartAsync(
            TestContext.Current.CancellationToken);
        await fixture.SendCommandAsync(
            "move-offscreen",
            "moved-offscreen",
            TestContext.Current.CancellationToken);
        var scanner = new WindowsTargetScanner();

        var capture = await scanner.CaptureWindowAsync(
            fixture.WindowHandle,
            TestContext.Current.CancellationToken);

        var target = Assert.Single(
            capture.Elements,
            static element =>
                element.AutomationId == "invoke-target");
        Assert.False(target.IsEligible);
        Assert.Equal(
            UiAutomationExclusion.OutsideDisplays,
            target.Exclusion);
    }

    [Fact]
    public async Task ScanLimitsFailInsteadOfReturningPartialCaptures()
    {
        await using var fixture = await FixtureProcess.StartAsync(
            TestContext.Current.CancellationToken);

        var elementLimit = new WindowsTargetScanner(
            maximumDepth: 128,
            maximumElements: 1);
        var elementException =
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => elementLimit.CaptureWindowAsync(
                    fixture.WindowHandle,
                    TestContext.Current.CancellationToken));
        Assert.Contains(
            "exceeded 1 control elements",
            elementException.Message,
            StringComparison.Ordinal);

        var depthLimit = new WindowsTargetScanner(
            maximumDepth: 1,
            maximumElements: 10_000);
        var depthException =
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => depthLimit.CaptureWindowAsync(
                    fixture.WindowHandle,
                    TestContext.Current.CancellationToken));
        Assert.Contains(
            "exceeded 1 levels",
            depthException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidBoundsRemainJsonSafe()
    {
        Assert.Null(
            PhysicalBounds.TryCreate(
                double.PositiveInfinity,
                0,
                double.NegativeInfinity,
                20));
        var capture = new UiAutomationCapture(
            new UiAutomationWindowCapture(
                WindowHandle: 1,
                ProcessId: 2,
                Name: "Invalid bounds",
                IsMinimized: false),
            [
                new UiAutomationElementCapture(
                    TreePath: [0],
                    RuntimeId: [],
                    AutomationId: "invalid-bounds",
                    Name: "Invalid bounds",
                    ControlType: "ControlType.Button",
                    ProcessId: 2,
                    NativeWindowHandle: null,
                    Bounds: null,
                    IsEnabled: true,
                    IsOffscreen: false,
                    Actions: [UiAutomationAction.Invoke],
                    Exclusion: UiAutomationExclusion.InvalidBounds,
                    UnavailableProperties: []),
            ]);

        var json = await SerializeAsync(capture);

        using var document = JsonDocument.Parse(json);
        var element = Assert.Single(
            document.RootElement
                .GetProperty("elements")
                .EnumerateArray());
        Assert.Equal(
            "InvalidBounds",
            element.GetProperty("exclusion").GetString());
        Assert.False(element.TryGetProperty("bounds", out _));
    }

    [Fact]
    public async Task TargetDumpSerializesTheRequestedWindow()
    {
        await using var fixture = await FixtureProcess.StartAsync(
            TestContext.Current.CancellationToken);
        var result = await RunDotnetAsync(
            [
                "run",
                "--no-build",
                "--project",
                RepositoryPath(
                    "src",
                    "Desknav.TargetDump",
                    "Desknav.TargetDump.csproj"),
                "--configuration",
                BuildConfiguration,
                "--",
                "--window-handle",
                fixture.WindowHandle.ToString(
                    CultureInfo.InvariantCulture),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(
            "physicalPixels",
            root.GetProperty("coordinateSpace").GetString());
        Assert.Equal(
            fixture.ProcessId,
            root.GetProperty("window").GetProperty("processId").GetInt32());
        Assert.Contains(
            root.GetProperty("elements").EnumerateArray(),
            static element =>
                element.GetProperty("automationId").GetString()
                    == "invoke-target"
                && element.GetProperty("isEligible").GetBoolean());
    }

    [Theory]
    [InlineData("-2147483648")]
    [InlineData("0xFFFFFFFF80000000")]
    public void TargetDumpAcceptsSignExtendedWindowHandles(
        string value)
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        var options = TargetDumpOptions.Parse(
            ["--window-handle", value]);

        Assert.Equal(
            new nint(-2_147_483_648),
            options.WindowHandle);
    }

    private static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static PhysicalRect OutwardRect(PhysicalBounds bounds)
    {
        var left = (int)Math.Floor(bounds.Left);
        var top = (int)Math.Floor(bounds.Top);
        var right = (int)Math.Ceiling(bounds.Right);
        var bottom = (int)Math.Ceiling(bounds.Bottom);
        return new PhysicalRect(
            left,
            top,
            right - left,
            bottom - top);
    }

    private static async Task<string> SerializeAsync(
        UiAutomationCapture capture)
    {
        await using var stream = new MemoryStream();
        await UiAutomationCaptureJson.WriteAsync(stream, capture);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The dotnet process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string RepositoryPath(params string[] segments) =>
        segments.Aggregate(
            RepositoryRoot,
            Path.Combine);

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(directory.FullName, "desknav.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root.");
    }

    private sealed class FixtureProcess(
        Process process,
        nint windowHandle,
        int processId) : IAsyncDisposable
    {
        public int ProcessId { get; } = processId;

        public nint WindowHandle { get; } = windowHandle;

        public static async Task<FixtureProcess> StartAsync(
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryRoot,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--no-build");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(
                RepositoryPath(
                    "tests",
                    "Desknav.UIAutomation.Fixture",
                    "Desknav.UIAutomation.Fixture.csproj"));
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add(BuildConfiguration);

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The UI Automation fixture did not start.");
            var handleLine = await process.StandardOutput.ReadLineAsync(
                cancellationToken);
            var values = handleLine?.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (values is not [var handleValue, var processValue]
                || !long.TryParse(
                    handleValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var windowHandleValue)
                || !int.TryParse(
                    processValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId))
            {
                var error = await process.StandardError.ReadToEndAsync(
                    cancellationToken);
                process.Dispose();
                throw new InvalidOperationException(
                    $"The UI Automation fixture returned '{handleLine}'."
                    + $" Error: {error}");
            }

            return new FixtureProcess(
                process,
                new nint(windowHandleValue),
                processId);
        }

        public async Task SendCommandAsync(
            string command,
            string expectedResponse,
            CancellationToken cancellationToken)
        {
            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync();
            var response = await process.StandardOutput.ReadLineAsync(
                cancellationToken);
            Assert.Equal(expectedResponse, response);
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync();
                await process.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }

            process.Dispose();
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}