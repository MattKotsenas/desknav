using Akka.Actor;
using Akka.Event;

using Microsoft.Extensions.Hosting;

using Desknav.ControlPlane;
using Desknav.UI;

namespace Desknav.App;

/// <summary>
/// Owns the critical local actor graph and translates unrecoverable component
/// loss into host shutdown.
/// </summary>
internal sealed class RuntimeGuardian : ReceiveActor
{
    private static readonly TimeSpan TargetDiscoveryTimeout =
        TimeSpan.FromSeconds(5);

    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly RuntimeOutcome _outcome;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    public RuntimeGuardian(
        ITargetDiscovery targetDiscovery,
        IOverlayRenderer overlayRenderer,
        IHostApplicationLifetime applicationLifetime,
        RuntimeOutcome outcome)
    {
        _applicationLifetime = applicationLifetime;
        _outcome = outcome;

        var overlay = Context.ActorOf(
            OverlayActor.CreateProps(overlayRenderer),
            "overlay");
        var coordinator = Context.ActorOf(
            NavigationCoordinator.CreateProps(
                TargetDiscoveryActor.CreateProps(
                    targetDiscovery,
                    TargetDiscoveryTimeout),
                ActorRefs.Nobody,
                overlay),
            "coordinator");
        var kanata = Context.ActorOf(
            KanataActor.CreateProps(coordinator),
            "kanata");
        Context.Watch(overlay);
        Context.Watch(coordinator);
        Context.Watch(kanata);

        Receive<RuntimeFailure>(failure => StopApplication(failure.Cause));
        Receive<Terminated>(Handle);
        ReceiveAny(kanata.Forward);
    }

    protected override SupervisorStrategy SupervisorStrategy() =>
        new OneForOneStrategy(
            exception =>
            {
                StopApplication(exception);
                return Directive.Stop;
            });

    private void Handle(Terminated terminated)
        => StopApplication(
            new InvalidOperationException(
                $"{terminated.ActorRef.Path.Name} stopped unexpectedly."));

    private void StopApplication(Exception cause)
    {
        _outcome.RecordFailure(cause);
        _log.Error(cause, "A critical Desknav component failed.");
        _applicationLifetime.StopApplication();
    }
}
