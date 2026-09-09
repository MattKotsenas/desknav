using System.Net;
using System.Windows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Desknav.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args is not ["--kanata-endpoint", var endpointText]
            || !IPEndPoint.TryParse(endpointText, out var endpoint))
        {
            Console.Error.WriteLine(
                "Usage: Desknav.App --kanata-endpoint <IP:PORT>");
            Shutdown(exitCode: 2);
            return;
        }

        using var host = DesknavRuntime
            .CreateHostBuilder(endpoint, Dispatcher)
            .Build();
        try
        {
            Shutdown(await RunHostAsync(host));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Shutdown(exitCode: 1);
        }
    }

    internal static async Task<int> RunHostAsync(IHost host)
    {
        var status = host.Services.GetRequiredService<RuntimeStatus>();
        await host.RunAsync();
        if (status.Failure is null)
        {
            return 0;
        }

        Console.Error.WriteLine(status.Failure);
        return 1;
    }
}
