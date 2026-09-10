using System.Net;
using System.Windows.Threading;

using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Desknav.ControlPlane;
using Desknav.UI;
using Desknav.UI.Wpf;
using Desknav.UIAutomation;

namespace Desknav.App;

internal static class DesknavRuntime
{
    public static IHostBuilder CreateHostBuilder(
        string[] args,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(dispatcher);

        return Host
            .CreateDefaultBuilder(args)
            .UseConsoleLifetime()
            .ConfigureServices(
                (_, services) =>
                {
                    services
                        .AddOptionsWithValidateOnStart<
                            KanataOptions,
                            KanataOptionsValidator>()
                        .BindConfiguration(KanataOptions.SectionName);
                    services.AddSingleton(
                        provider => IPEndPoint.Parse(
                            provider
                                .GetRequiredService<
                                    IOptions<KanataOptions>>()
                                .Value
                                .Endpoint));
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
                    services.AddSingleton<RuntimeOutcome>();
                    services.AddAkka(
                        "desknav",
                        (builder, _) =>
                        {
                            builder.AddHocon(
                                """
                                akka.coordinated-shutdown {
                                  run-by-clr-shutdown-hook = off
                                }
                                """,
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
                    services.AddHostedService<KanataIngressService>();
                });
    }
}
