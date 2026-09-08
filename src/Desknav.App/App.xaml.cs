using System.Net;
using System.Windows;

using Desknav.UI.Wpf;
using Desknav.UIAutomation;

namespace Desknav.App;

public partial class App : Application
{
    private static readonly TimeSpan TargetDiscoveryTimeout =
        TimeSpan.FromSeconds(5);

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

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;

        try
        {
            var host = new DesknavHost(
                endpoint,
                new WindowsTargetDiscovery(
                    new WindowsTargetScanner()),
                new WpfOverlayRenderer(Dispatcher),
                TargetDiscoveryTimeout);
            await host.RunAsync(cancellation.Token);
            Shutdown();
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
            Shutdown();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Shutdown(exitCode: 1);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
