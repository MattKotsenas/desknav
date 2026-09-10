using Akka.Hosting;

using Microsoft.Extensions.Hosting;

using Desknav.ControlPlane;

namespace Desknav.App;

internal sealed class KanataIngressService(
    KanataTcpIngress ingress,
    IRequiredActor<RuntimeGuardian> runtime,
    IHostApplicationLifetime applicationLifetime,
    RuntimeOutcome outcome)
    : BackgroundService
{
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
            outcome.RecordFailure(exception);
            throw;
        }

        applicationLifetime.StopApplication();
    }
}
