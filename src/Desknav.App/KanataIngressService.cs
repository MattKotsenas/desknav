using Akka.Hosting;

using Microsoft.Extensions.Hosting;

using Desknav.ControlPlane;

namespace Desknav.App;

internal sealed class KanataIngressService(
    KanataTcpIngress ingress,
    IRequiredActor<RuntimeGuardian> runtime,
    IHostApplicationLifetime applicationLifetime,
    RuntimeStatus status)
    : BackgroundService
{
    public override Task StopAsync(CancellationToken _)
    {
        // Ingress must finish before runtime cleanup and actor-system shutdown,
        // even when an external host-stop token expires.
        return base.StopAsync(CancellationToken.None);
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            await ingress
                .RunAsync(runtime.ActorRef, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (stoppingToken.IsCancellationRequested
                  && exception.CancellationToken == stoppingToken)
        {
            return;
        }
        catch (Exception exception)
        {
            status.RecordFailure(exception);
            throw;
        }

        applicationLifetime.StopApplication();
    }
}
