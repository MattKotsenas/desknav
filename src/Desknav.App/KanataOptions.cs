using System.Net;

using Microsoft.Extensions.Options;

namespace Desknav.App;

internal sealed class KanataOptions
{
    public const string SectionName = "Kanata";

    public string Endpoint { get; set; } = string.Empty;
}

internal sealed class KanataOptionsValidator
    : IValidateOptions<KanataOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        KanataOptions options) =>
        TryParseEndpoint(options.Endpoint, out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "Kanata:Endpoint must be a loopback IP endpoint.");

    public static bool TryParseEndpoint(
        string endpointText,
        out IPEndPoint? endpoint)
    {
        if (IPEndPoint.TryParse(endpointText, out var parsed)
            && IPAddress.IsLoopback(parsed.Address))
        {
            endpoint = parsed;
            return true;
        }

        endpoint = null;
        return false;
    }
}
