using System.Windows;
using System.Windows.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Desknav.App;

public partial class App : Application
{
    private const string Usage =
        "Usage: Desknav.App --kanata-endpoint <IP:PORT>";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown(await RunAsync(e.Args, Dispatcher));
    }

    internal static async Task<int> RunAsync(
        string[] args,
        Dispatcher dispatcher)
    {
        try
        {
            using var host = DesknavRuntime
                .CreateHostBuilder(args, dispatcher)
                .Build();
            return await RunHostAsync(host);
        }
        catch (OptionsValidationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    internal static async Task<int> RunHostAsync(IHost host)
    {
        var outcome = host.Services.GetRequiredService<RuntimeOutcome>();
        await host.RunAsync();
        if (outcome.Failure is null)
        {
            return 0;
        }

        Console.Error.WriteLine(outcome.Failure);
        return 1;
    }
}
