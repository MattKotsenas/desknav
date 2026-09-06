using System.Globalization;

using Desknav.UIAutomation;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

TargetDumpOptions options;
try
{
    options = TargetDumpOptions.Parse(args);
}
catch (TargetDumpUsageException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    await Console.Error.WriteLineAsync(TargetDumpOptions.Usage);
    return 2;
}

if (options.ShowHelp)
{
    await Console.Out.WriteLineAsync(TargetDumpOptions.Usage);
    return 0;
}

var scanner = new WindowsTargetScanner();
UiAutomationCapture capture;
if (options.WindowHandle is { } windowHandle)
{
    capture = await scanner.CaptureWindowAsync(
        windowHandle,
        cancellation.Token);
}
else
{
    await Task.Delay(options.Delay, cancellation.Token);
    capture = await scanner.CaptureForegroundWindowAsync(
        cancellation.Token);
}

await UiAutomationCaptureJson.WriteAsync(
    Console.OpenStandardOutput(),
    capture,
    cancellation.Token);
await Console.Out.WriteLineAsync();
return 0;

internal sealed record TargetDumpOptions(
    nint? WindowHandle,
    TimeSpan Delay,
    bool ShowHelp)
{
    public const string Usage =
        "Usage: Desknav.TargetDump"
        + " [--window-handle <decimal-or-hex> | --delay <milliseconds>]"
        + " [--help|-h]";

    public static TargetDumpOptions Parse(string[] arguments)
    {
        nint? windowHandle = null;
        var delay = TimeSpan.FromSeconds(2);
        var delayWasSpecified = false;
        var showHelp = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--window-handle":
                    windowHandle = ParseWindowHandle(
                        ReadValue(arguments, ref index));
                    break;
                case "--delay":
                    delay = ParseDelay(
                        ReadValue(arguments, ref index));
                    delayWasSpecified = true;
                    break;
                default:
                    throw new TargetDumpUsageException(
                        $"Unknown argument '{arguments[index]}'.");
            }
        }

        if (windowHandle is not null && delayWasSpecified)
        {
            throw new TargetDumpUsageException(
                "--window-handle and --delay cannot be combined.");
        }

        return new TargetDumpOptions(
            windowHandle,
            delay,
            showHelp);
    }

    private static string ReadValue(
        string[] arguments,
        ref int index)
    {
        index++;
        if (index >= arguments.Length)
        {
            throw new TargetDumpUsageException(
                $"Argument '{arguments[index - 1]}' requires a value.");
        }

        return arguments[index];
    }

    private static nint ParseWindowHandle(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(
                    value.AsSpan(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var bits)
                || (IntPtr.Size == 4 && bits > uint.MaxValue))
            {
                throw InvalidWindowHandle(value);
            }

            var hexadecimalHandle = IntPtr.Size == 8
                ? new nint(unchecked((long)bits))
                : new nint(unchecked((int)(uint)bits));
            return hexadecimalHandle == 0
                ? throw InvalidWindowHandle(value)
                : hexadecimalHandle;
        }

        if (!long.TryParse(
                value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var signed)
            || (IntPtr.Size == 4
                && signed is < int.MinValue or > int.MaxValue))
        {
            throw InvalidWindowHandle(value);
        }

        var decimalHandle = new nint(signed);
        return decimalHandle == 0
            ? throw InvalidWindowHandle(value)
            : decimalHandle;
    }

    private static TargetDumpUsageException InvalidWindowHandle(
        string value) =>
        new(
            $"Window handle '{value}' is not a nonzero native integer.");

    private static TimeSpan ParseDelay(string value)
    {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var milliseconds)
            || milliseconds < 0)
        {
            throw new TargetDumpUsageException(
                $"Delay '{value}' is not a nonnegative integer.");
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }
}

internal sealed class TargetDumpUsageException(string message)
    : Exception(message);
