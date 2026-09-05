using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desknav.UIAutomation;

public static class UiAutomationCaptureJson
{
    public static async Task WriteAsync(
        Stream stream,
        UiAutomationCapture capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(capture);

        await JsonSerializer.SerializeAsync(
            stream,
            capture,
            UiAutomationJsonContext.Default.UiAutomationCapture,
            cancellationToken);
    }
}

[JsonSerializable(typeof(UiAutomationCapture))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
internal sealed partial class UiAutomationJsonContext
    : JsonSerializerContext;
