using System.Globalization;

using CliWrap;
using CliWrap.Buffered;

namespace Desknav.Kanata.Tests;

public sealed class KanataSimulatorTests
{
    private const int DiagonalTicksToInspect = 4;
    private const int MaximumPointerDistance = 12;
    private const int MovesPerDiagonalTick = 2;
    private const int MinimumPointerDistance = 1;
    private const char MarkerKey = 'q';
    private const string MovePrefix = "out🖰:move ";

    [Theory]
    [InlineData("h", "Left")]
    [InlineData("j", "Down")]
    [InlineData("k", "Up")]
    [InlineData("l", "Right")]
    public async Task CapsSpaceEntersPointerAndHeldMotionAccelerates(
        string key,
        string direction)
    {
        var output = await RunSimulatorAsync(
            $"{EnterPointer()} d:{key} t:800 u:{key} t:80");
        var moves = MouseMoves(output);

        Assert.NotEmpty(moves);
        Assert.All(moves, move => Assert.Equal(direction, move.Direction));
        Assert.Equal(MinimumPointerDistance, moves[0].Distance);
        Assert.Equal(MaximumPointerDistance, moves[^1].Distance);
        Assert.True(
            moves.SequenceEqual(moves.OrderBy(move => move.Distance)),
            "Pointer movement slowed while the key remained held.");
    }

    [Theory]
    [InlineData("h", "j", "Left", "Down")]
    [InlineData("h", "k", "Left", "Up")]
    [InlineData("l", "j", "Right", "Down")]
    [InlineData("l", "k", "Right", "Up")]
    public async Task HeldAxesCompose(
        string horizontalKey,
        string verticalKey,
        string horizontalDirection,
        string verticalDirection)
    {
        var output = await RunSimulatorAsync(
            $"{EnterPointer()} d:{horizontalKey} d:{verticalKey} t:120 "
            + $"u:{horizontalKey} u:{verticalKey} t:80");
        var moves = MouseMoves(output);
        var firstTicks = moves
            .Take(DiagonalTicksToInspect * MovesPerDiagonalTick)
            .Chunk(MovesPerDiagonalTick)
            .ToArray();

        Assert.Equal(DiagonalTicksToInspect, firstTicks.Length);
        Assert.All(
            firstTicks,
            tick => Assert.Equal(
                new[] { horizontalDirection, verticalDirection }.Order(),
                tick.Select(move => move.Direction).Order()));
    }

    [Theory]
    [InlineData('h')]
    [InlineData('j')]
    [InlineData('k')]
    [InlineData('l')]
    public async Task MovementKeyWithoutSpaceCancelsPointerEntry(char key)
    {
        var probeKey = key is 'h' ? 'l' : 'h';

        // Space would enter pointer if the movement key left the Caps prefix active.
        var output = await RunSimulatorAsync(
            $"{Tap("caps")} {Tap(key.ToString())} {Tap("spc")} "
            + $"d:{probeKey} t:120 u:{probeKey} t:80");

        Assert.Contains(KeyOutput('↓', key), output, StringComparison.Ordinal);
        Assert.Contains(
            KeyOutput('↓', probeKey),
            output,
            StringComparison.Ordinal);
        Assert.Empty(MouseMoves(output));
    }

    [Theory]
    [InlineData("h", "j", "Down")]
    [InlineData("l", "k", "Up")]
    [InlineData("j", "h", "Left")]
    [InlineData("k", "l", "Right")]
    public async Task ReleasingOneAxisContinuesTheOther(
        string releasedKey,
        string heldKey,
        string heldDirection)
    {
        var output = await RunSimulatorAsync(
            $"{EnterPointer()} d:{releasedKey} d:{heldKey} t:120 "
            + $"u:{releasedKey} {Marker()} t:120 u:{heldKey} t:80");
        var afterRelease = AfterMarker(output, KeyOutput('↓', MarkerKey));
        var moves = MouseMoves(afterRelease);

        Assert.NotEmpty(moves);
        Assert.All(moves, move => Assert.Equal(heldDirection, move.Direction));
    }

    [Fact]
    public async Task PointerMovementStopsAfterRelease()
    {
        var output = await RunSimulatorAsync(
            $"{EnterPointer()} d:h t:120 u:h {Marker()} t:80");
        var markerIndex = MarkerIndex(
            output,
            KeyOutput('↓', MarkerKey));
        var beforeMarker = output[..markerIndex];
        var afterMarker = AfterMarker(output, KeyOutput('↓', MarkerKey));

        Assert.NotEmpty(MouseMoves(beforeMarker));
        Assert.Empty(MouseMoves(afterMarker));
    }

    [Theory]
    [InlineData("caps")]
    [InlineData("esc")]
    public async Task CapsOrEscapeExitsPointer(string exitKey)
    {
        var output = await RunSimulatorAsync(
            $"{EnterPointer()} {Tap(exitKey)} d:h t:120 u:h t:80");

        Assert.Contains(KeyOutput('↓', 'h'), output, StringComparison.Ordinal);
        Assert.Contains(KeyOutput('↑', 'h'), output, StringComparison.Ordinal);
        Assert.Empty(MouseMoves(output));
    }

    private static string EnterPointer() => $"{Tap("caps")} {Tap("spc")}";

    // Q is outside defsrc, so process-unmapped-keys echoes it as a timing fence.
    private static string Marker() => Tap(MarkerKey.ToString());

    private static string KeyOutput(char edge, char key) =>
        $"out:{edge}{char.ToUpperInvariant(key)}";

    private static string Tap(string key) => $"d:{key} t:10 u:{key} t:10";

    private static async Task<string> RunSimulatorAsync(string simulation)
    {
        var simulatorPath = Path.Combine(
            AppContext.BaseDirectory,
            "kanata_simulated_input.exe");
        var configPath = Path.Combine(AppContext.BaseDirectory, "desknav.kbd");

        if (!File.Exists(simulatorPath))
        {
            throw new FileNotFoundException(
                "Kanata simulator was not found.",
                simulatorPath);
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "desknav Kanata configuration was not found.",
                configPath);
        }

        var simulationPath = Path.Combine(
            Path.GetTempPath(),
            $"desknav-kanata-{Guid.NewGuid():N}.sim");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(
            simulationPath,
            simulation,
            timeout.Token);

        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap(simulatorPath)
                .WithArguments(arguments => arguments
                    .Add("-c")
                    .Add(configPath)
                    .Add("-s")
                    .Add(simulationPath))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Kanata simulator did not exit within 30 seconds.");
            throw;
        }
        finally
        {
            File.Delete(simulationPath);
        }

        Assert.True(
            result.ExitCode == 0,
            $"Kanata simulator exited with code {result.ExitCode}."
            + Environment.NewLine
            + result.StandardOutput
            + result.StandardError);

        return result.StandardOutput;
    }

    private static string AfterMarker(string output, string marker)
    {
        var markerIndex = MarkerIndex(output, marker);
        return output[(markerIndex + marker.Length)..];
    }

    private static int MarkerIndex(string output, string marker)
    {
        var markerIndex = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Missing marker: {marker}");
        return markerIndex;
    }

    private static MouseMove[] MouseMoves(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(MovePrefix, StringComparison.Ordinal))
            .Select(ParseMouseMove)
            .ToArray();

    private static MouseMove ParseMouseMove(string line)
    {
        var parts = line[MovePrefix.Length..].Split(',');
        return new MouseMove(
            parts[0],
            int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private readonly record struct MouseMove(string Direction, int Distance);
}