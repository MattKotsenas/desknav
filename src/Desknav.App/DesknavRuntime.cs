using System.Windows.Threading;

using Akka;
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Desknav.ControlPlane;
using Desknav.UI;
using Desknav.UI.Wpf;
using Desknav.UIAutomation;

namespace Desknav.App;

internal static class DesknavRuntime
{
    private static readonly TimeSpan CleanupTimeout =
        TimeSpan.FromSeconds(5);

    public static IHostBuilder CreateHostBuilder(
        string[] args,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(dispatcher);

        return new HostBuilder()
            .UseConsoleLifetime()
            .ConfigureLogging(logging => logging.AddConsole())
            .ConfigureAppConfiguration(
                configuration => configuration.AddCommandLine(
                    args,
                    new Dictionary<string, string>
                    {
                        ["--kanata-endpoint"] =
                            $"{KanataOptions.SectionName}:Endpoint",
                    }))
            .ConfigureServices(
                (context, services) =>
                {
                    services
                        .AddOptions<KanataOptions>()
                        .Bind(
                            context.Configuration.GetSection(
                                KanataOptions.SectionName))
                        .ValidateOnStart();
                    services.AddSingleton<
                        IValidateOptions<KanataOptions>,
                        KanataOptionsValidator>();
                    services.AddSingleton(
                        provider =>
                        {
                            var options = provider
                                .GetRequiredService<
                                    IOptions<KanataOptions>>()
                                .Value;
                            KanataOptionsValidator.TryParseEndpoint(
                                options.Endpoint,
                                out var endpoint);
                            return endpoint!;
                        });
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
                        (builder, provider) =>
                        {
                            builder.AddHocon(
                                """
                                akka.coordinated-shutdown {
                                  run-by-clr-shutdown-hook = off
                                  phases.before-actor-system-terminate {
                                    timeout = 6s
                                  }
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
                                    CoordinatedShutdown
                                        .Get(system)
                                        .AddTask(
                                            CoordinatedShutdown
                                                .PhaseBeforeActorSystemTerminate,
                                            "stop-desknav-runtime",
                                            () => StopRuntimeAsync(
                                                runtime,
                                                provider.GetRequiredService<
                                                    RuntimeOutcome>()));
                                });
                        });
                    services.AddHostedService<KanataIngressService>();
                });
    }

    private static async Task<Done> StopRuntimeAsync(
        IActorRef runtime,
        RuntimeOutcome outcome)
    {
        try
        {
            return await runtime
                .Ask<Done>(
                    new PrepareForShutdown(),
                    CleanupTimeout)
                .ConfigureAwait(false);
        }
        catch (AskTimeoutException exception)
        {
            outcome.RecordFailure(
                new TimeoutException(
                    "Desknav runtime cleanup exceeded its five-second"
                    + " shutdown budget.",
                    exception));
            return Done.Instance;
        }
        catch (Exception exception)
        {
            outcome.RecordFailure(exception);
            return Done.Instance;
        }
    }
}
