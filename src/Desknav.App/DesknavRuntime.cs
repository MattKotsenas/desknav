using System.Net;
using System.Windows.Threading;

using Akka.DependencyInjection;
using Akka.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Desknav.ControlPlane;
using Desknav.UI;
using Desknav.UI.Wpf;
using Desknav.UIAutomation;

namespace Desknav.App;

internal static class DesknavRuntime
{
    public static IHostBuilder CreateHostBuilder(
        IPEndPoint kanataEndpoint,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(kanataEndpoint);
        ArgumentNullException.ThrowIfNull(dispatcher);

        return new HostBuilder()
            .UseConsoleLifetime()
            .ConfigureLogging(logging => logging.AddConsole())
            .ConfigureServices(
                services =>
                {
                    services.AddSingleton(
                        new IPEndPoint(
                            kanataEndpoint.Address,
                            kanataEndpoint.Port));
                    services.AddSingleton(dispatcher);
                    services.AddSingleton<IKanataFrameParser, KanataFrameParser>();
                    services.AddSingleton<KanataTcpIngress>();
                    services.AddSingleton<ITargetScanner, WindowsTargetScanner>();
                    services.AddSingleton<
                        ITargetDiscovery,
                        WindowsTargetDiscovery>();
                    services.AddSingleton<IOverlayRenderer>(
                        provider => new WpfOverlayRenderer(
                            provider.GetRequiredService<Dispatcher>()));
                    services.AddSingleton<RuntimeStatus>();
                    services.Configure<HostOptions>(
                        options =>
                            options.ShutdownTimeout =
                                Timeout.InfiniteTimeSpan);
                    services.AddAkka(
                        "desknav",
                        (builder, _) =>
                        {
                            builder.AddHocon(
                                "akka.coordinated-shutdown"
                                + ".run-by-clr-shutdown-hook = off",
                                HoconAddMode.Prepend);
                            builder.WithActors(
                                (system, registry, resolver) =>
                                {
                                    var runtime = system.ActorOf(
                                        resolver.Props<RuntimeGuardian>(),
                                        "runtime");
                                    registry.Register<RuntimeGuardian>(
                                        runtime);
                                });
                        });
                    services.AddHostedService<RuntimeShutdownService>();
                    services.AddHostedService<KanataIngressService>();
                });
    }
}
