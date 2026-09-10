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
        KanataOptions options)
    {
        if (IPEndPoint.TryParse(options.Endpoint, out var endpoint)
            && IPAddress.IsLoopback(endpoint.Address)
            && endpoint.Port is > 0)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            "Kanata:Endpoint must be a loopback IP endpoint"
            + " with a nonzero port.");
    }
}
