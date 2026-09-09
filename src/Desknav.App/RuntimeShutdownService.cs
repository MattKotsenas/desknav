using Akka;
using Akka.Actor;
using Akka.Hosting;

using Microsoft.Extensions.Hosting;

using Desknav.ControlPlane;

namespace Desknav.App;

internal sealed class RuntimeShutdownService(
    IRequiredActor<RuntimeGuardian> runtime)
    : IHostedService
{
    public Task StartAsync(CancellationToken _) =>
        Task.CompletedTask;

    public async Task StopAsync(CancellationToken _)
    {
        // Actor-owned resources must be released before Akka.Hosting stops
        // the actor system, even when an external host-stop token expires.
        await runtime.ActorRef
            .Ask<Done>(
                new PrepareForShutdown(),
                CancellationToken.None)
            .ConfigureAwait(false);
    }
}
