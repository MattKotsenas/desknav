using Akka.Actor;

namespace Desknav.ControlPlane.Tests;

internal static class ActorTestHelpers
{
    public static async Task FlushAsync(
        IActorRef actor,
        CancellationToken cancellationToken) =>
        await actor.Ask<ActorIdentity>(
            new Identify(null),
            cancellationToken);

    public static async Task<IActorRef> ResolveTargetDiscoveryAsync(
        ActorSystem system,
        IActorRef parent,
        CancellationToken cancellationToken)
    {
        await FlushAsync(parent, cancellationToken);
        return await system
            .ActorSelection(
                parent.Path.Child("target-discovery"))
            .ResolveOne(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
    }

    public static async Task PoisonTargetDiscoveryAsync(
        ActorSystem system,
        IActorRef parent,
        CancellationToken cancellationToken)
    {
        var targetDiscovery = await ResolveTargetDiscoveryAsync(
            system,
            parent,
            cancellationToken);
        targetDiscovery.Tell(PoisonPill.Instance);
    }
}
