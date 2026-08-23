using Akka.Actor;

namespace Desknav.ControlPlane;

public sealed class PointAtTargetCoordinator : ReceiveActor
{
    private readonly IActorRef _kanata;
    private readonly IActorRef _pointerUi;
    private PointAtTargetLifecycle _lifecycle =
        PointAtTargetLifecycle.Initial;
    private IActorRef? _requester;

    public PointAtTargetCoordinator(
        IActorRef pointerUi,
        IActorRef kanata)
    {
        _pointerUi = pointerUi;
        _kanata = kanata;

        Receive<PointAtTarget>(_ => Start(Sender));
        Receive<PointAtTargetExecuted>(
            executed =>
            {
                executed.ActionId.EnsureValid();
                Apply(_lifecycle.Pointed(executed.ActionId));
            });
        Receive<PointAtTargetCancelled>(
            cancelled =>
            {
                cancelled.ActionId.EnsureValid();
                Apply(_lifecycle.Cancelled(cancelled.ActionId));
            });
        Receive<BaseLayerActive>(
            _ => Apply(_lifecycle.BaseLayerActive()));
    }

    public static Props Props(
        IActorRef pointerUi,
        IActorRef kanata)
    {
        ArgumentNullException.ThrowIfNull(pointerUi);
        ArgumentNullException.ThrowIfNull(kanata);

        return Akka.Actor.Props.Create(
            () => new PointAtTargetCoordinator(pointerUi, kanata));
    }

    protected override void PreRestart(Exception reason, object message) =>
        Context.Stop(Self);

    protected override void PostStop() =>
        Context.System.Terminate();

    private void Start(IActorRef requester)
    {
        var transition = _lifecycle.Start();
        if (transition.Effect is ExecutePointAtTargetEffect)
        {
            _requester = requester;
        }

        Apply(transition, requester);
    }

    private void Apply(
        PointAtTargetTransition transition,
        IActorRef? requester = null)
    {
        _lifecycle = transition.Lifecycle;

        switch (transition.Effect)
        {
            case null:
                return;
            case ExecutePointAtTargetEffect execute:
                _pointerUi.Tell(
                    new ExecutePointAtTarget(execute.ActionId),
                    Self);
                return;
            case RefusePointAtTargetBusyEffect:
                (requester ?? throw new InvalidOperationException(
                    "A busy refusal requires a requester."))
                    .Tell(new PointAtTargetBusy(), Self);
                return;
            case RestoreBaseLayerEffect:
                _kanata.Tell(new RestoreBaseLayer(), Self);
                return;
            case CompletePointAtTargetEffect complete:
                var completedRequester =
                    _requester ?? throw new InvalidOperationException(
                        "An active action requires a requester.");
                _requester = null;
                completedRequester.Tell(
                    new PointAtTargetCompleted(
                        complete.ActionId,
                        complete.Outcome),
                    Self);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unknown effect {transition.Effect.GetType().Name}.");
        }
    }
}