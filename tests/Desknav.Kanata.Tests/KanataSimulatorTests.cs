using CliWrap;
using CliWrap.Buffered;

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
        var simulatorPath = Path.Combine(
            AppContext.BaseDirectory,
            "kanata_simulated_input.exe");

        if (!File.Exists(simulatorPath))
        {
            throw new FileNotFoundException(
                "Kanata simulator was not found.",
                simulatorPath);
        }

        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap(simulatorPath)
                .WithArguments(arguments => arguments
                    .Add("-c")
                    .Add(Path.Combine(fixtures, "pointer-left.kbd"))
                    .Add("-s")
                    .Add(Path.Combine(fixtures, simulation)))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Kanata simulator did not exit within 30 seconds.");
            throw;
        }

        Assert.True(
            result.ExitCode == 0,
            $"Kanata simulator exited with code {result.ExitCode}."
            + Environment.NewLine
            + result.StandardOutput
            + result.StandardError);

        return result.StandardOutput;
    }

    private static string[] MouseMoves(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("out🖰:move", StringComparison.Ordinal))
            .ToArray();
}