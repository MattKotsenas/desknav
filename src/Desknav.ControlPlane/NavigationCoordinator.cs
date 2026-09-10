using Akka.Actor;

namespace Desknav.ControlPlane;

public sealed class NavigationCoordinator : ReceiveActor
{
    private readonly IActorRef _targetDiscovery;
    private readonly IActorRef _inputObserver;
    private readonly IActorRef _overlayOwner;
    private bool _isStopping;
    private NavigationWorkflowState _state =
        NavigationWorkflowState.Initial;

    public NavigationCoordinator(
        Props targetDiscoveryProps,
        IActorRef inputObserver,
        IActorRef overlayOwner)
    {
        _targetDiscovery = Context.ActorOf(
            targetDiscoveryProps,
            "target-discovery");
        Context.Watch(_targetDiscovery);
        _inputObserver = inputObserver;
        _overlayOwner = overlayOwner;

        Receive<KeyboardLayerObserved>(Handle);
        Receive<KeyboardLayerUnavailable>(Handle);
        Receive<GestureObserved>(Handle);
        Receive<TargetDiscoveryCompleted>(Handle);
        Receive<TargetPresentationApplied>(Handle);
        Receive<RuntimeFailure>(
            failure => Context.Parent.Forward(failure));
        Receive<PrepareForShutdown>(Handle);
        Receive<Terminated>(Handle);
    }

    protected override SupervisorStrategy SupervisorStrategy() =>
        new OneForOneStrategy(
            exception =>
            {
                Context.Parent.Tell(new RuntimeFailure(exception));
                return Directive.Stop;
            });

    public static Props CreateProps(
        Props targetDiscoveryProps,
        IActorRef inputObserver,
        IActorRef overlayOwner)
    {
        ArgumentNullException.ThrowIfNull(targetDiscoveryProps);
        ArgumentNullException.ThrowIfNull(inputObserver);
        ArgumentNullException.ThrowIfNull(overlayOwner);
        return Akka.Actor.Props.Create(
            () => new NavigationCoordinator(
                targetDiscoveryProps,
                inputObserver,
                overlayOwner));
    }

    private void Handle(PrepareForShutdown _)
    {
        if (_isStopping)
        {
            return;
        }

        _isStopping = true;
        _targetDiscovery.Tell(new PrepareForShutdown());
    }

    private void Handle(Terminated terminated)
    {
        if (!terminated.ActorRef.Equals(_targetDiscovery))
        {
            return;
        }

        if (!_isStopping)
        {
            Context.Parent.Tell(
                new RuntimeFailure(
                    new InvalidOperationException(
                        "The target discovery actor stopped unexpectedly.")));
            Context.Stop(Self);
            return;
        }

        Context.Stop(Self);
    }

    private void Handle(KeyboardLayerObserved observed)
        => Apply(NavigationWorkflow.Decide(_state, observed));

    private void Handle(KeyboardLayerUnavailable unavailable)
        => Apply(NavigationWorkflow.Decide(_state, unavailable));

    private void Handle(GestureObserved observed)
        => Apply(
            NavigationWorkflow.Decide(
                _state,
                observed,
                TargetDiscoveryRequestId.New));

    private void Handle(TargetDiscoveryCompleted completed)
        => Apply(NavigationWorkflow.Decide(_state, completed));

    private void Handle(TargetPresentationApplied applied)
        => Apply(NavigationWorkflow.Decide(_state, applied));

    private void Apply(NavigationDecision decision)
    {
        _state = decision.State;
        foreach (var effect in decision.Effects)
        {
            switch (effect)
            {
                case NavigationEffect.ReportKeyboardLayer report:
                    _inputObserver.Tell(report.Observation);
                    break;
                case NavigationEffect.ReportKeyboardLayerUnavailable report:
                    _inputObserver.Tell(report.Observation);
                    break;
                case NavigationEffect.ReportCommandInput report:
                    _inputObserver.Tell(
                        new CommandInputObserved(report.Token));
                    break;
                case NavigationEffect.ReportCommandSessionEnded:
                    _inputObserver.Tell(new CommandSessionEnded());
                    break;
                case NavigationEffect.CancelDiscovery cancel:
                    _targetDiscovery.Tell(
                        new CancelTargetDiscovery(cancel.RequestId));
                    break;
                case NavigationEffect.RequestTargetDiscovery request:
                    _targetDiscovery.Tell(
                        new DiscoverTargets(request.RequestId));
                    break;
                case NavigationEffect.ApplyTargetPresentation apply:
                    _overlayOwner.Tell(
                        new ApplyTargetPresentation(
                            apply.Revision,
                            apply.Presentation));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(effect),
                        effect,
                        "Unknown navigation effect.");
            }
        }
    }
}
