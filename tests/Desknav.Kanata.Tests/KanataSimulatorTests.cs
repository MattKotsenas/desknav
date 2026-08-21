using System.Diagnostics;
using System.Text;

namespace Desknav.Kanata.Tests;

public sealed class KanataSimulatorTests
{
    [Fact]
    public async Task PointerMovementDoesNotContinueAfterRelease()
    {
        var atRelease = await RunSimulatorAsync("pointer-left-at-release.sim");
        var afterRelease = await RunSimulatorAsync("pointer-left-after-release.sim");
        var atReleaseMoves = MouseMoves(atRelease);
        var afterReleaseMoves = MouseMoves(afterRelease);

        Assert.NotEmpty(atReleaseMoves);
        Assert.All(
            atReleaseMoves,
            move => Assert.StartsWith("out🖰:move Left,", move, StringComparison.Ordinal));
        Assert.Equal(atReleaseMoves, afterReleaseMoves);
    }

    private static async Task<string> RunSimulatorAsync(string simulation)
    {
        var simulatorPath = Environment.GetEnvironmentVariable(
            "KANATA_SIMULATOR_PATH");
        if (string.IsNullOrWhiteSpace(simulatorPath))
        {
            throw new InvalidOperationException(
                "KANATA_SIMULATOR_PATH must identify kanata_simulated_input.exe.");
        }

        if (!File.Exists(simulatorPath))
        {
            throw new FileNotFoundException(
                "Kanata simulator was not found.",
                simulatorPath);
        }

        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var startInfo = new ProcessStartInfo
        {
            FileName = simulatorPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(Path.Combine(fixtures, "pointer-left.kbd"));
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(Path.Combine(fixtures, simulation));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Kanata simulator did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Kanata simulator did not exit within 30 seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"Kanata simulator exited with code {process.ExitCode}.{Environment.NewLine}{output}{error}");

        return output;
    }

    private static string[] MouseMoves(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("out🖰:move", StringComparison.Ordinal))
            .ToArray();
}