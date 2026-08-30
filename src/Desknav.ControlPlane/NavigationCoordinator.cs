using Akka.Actor;

using Vogen;

namespace Desknav.ControlPlane;

public sealed class NavigationCoordinator : ReceiveActor
{
    private readonly IActorRef _targetDiscovery;
    private readonly IActorRef _inputObserver;
    private readonly IActorRef _overlayOwner;
    private CommandSessionState _commandState;
    private ActiveTargetDiscovery? _activeDiscovery;
    private long _workflowGenerationHighWatermark;
    private long _presentationRevisionHighWatermark;

    public NavigationCoordinator(
        IActorRef targetDiscovery,
        IActorRef inputObserver,
        IActorRef overlayOwner)
    {
        _targetDiscovery = targetDiscovery;
        _inputObserver = inputObserver;
        _overlayOwner = overlayOwner;

        Receive<KeyboardLayerObserved>(Handle);
        Receive<KeyboardLayerUnavailable>(Handle);
        Receive<GestureObserved>(Handle);
        Receive<TargetDiscoveryCompleted>(Handle);
    }

    public static Props CreateProps(
        IActorRef targetDiscovery,
        IActorRef inputObserver,
        IActorRef overlayOwner)
    {
        ArgumentNullException.ThrowIfNull(targetDiscovery);
        ArgumentNullException.ThrowIfNull(inputObserver);
        ArgumentNullException.ThrowIfNull(overlayOwner);
        return Akka.Actor.Props.Create(
            () => new NavigationCoordinator(
                targetDiscovery,
                inputObserver,
                overlayOwner));
    }

    private void Handle(KeyboardLayerObserved observed)
    {
        _inputObserver.Tell(observed);
        if (observed.Layer.Value == "base")
        {
            EndCommandSession();
        }
        else if (observed.Layer.Value == "command")
        {
            _commandState = CommandSessionState.Command;
        }
    }

    private void Handle(KeyboardLayerUnavailable unavailable)
    {
        _inputObserver.Tell(unavailable);
        EndCommandSession();
    }

    private void Handle(GestureObserved observed)
    {
        _inputObserver.Tell(new CommandInputObserved(observed.Token));

        if (observed.Token.Key == "esc")
        {
            EndCommandSession();
            return;
        }

        if (observed.Token is { Context: "command", Key: "spc" })
        {
            _commandState = CommandSessionState.PointerPrefix;
        }
        else if (observed.Token is { Context: "pointer", Key: "f" }
                 && _commandState == CommandSessionState.PointerPrefix)
        {
            _commandState = CommandSessionState.Command;
            StartTargetDiscovery();
        }
    }

    private void Handle(TargetDiscoveryCompleted completed)
    {
        if (_activeDiscovery is not { } active
            || active.RequestId != completed.Snapshot.RequestId)
        {
            return;
        }

        _activeDiscovery = null;
        _presentationRevisionHighWatermark++;
        _overlayOwner.Tell(
            new PresentTargets(
                PresentationRevision.From(_presentationRevisionHighWatermark),
                completed.Snapshot));
    }

    private void StartTargetDiscovery()
    {
        _workflowGenerationHighWatermark++;
        var next = new ActiveTargetDiscovery(
            WorkflowGeneration.From(_workflowGenerationHighWatermark),
            TargetDiscoveryRequestId.New());
        var previous = _activeDiscovery;
        _activeDiscovery = next;

        if (previous is not null)
        {
            _targetDiscovery.Tell(
                new CancelTargetDiscovery(previous.RequestId));
        }

        _targetDiscovery.Tell(new DiscoverTargets(next.RequestId));
    }

    private void EndCommandSession()
    {
        if (_commandState == CommandSessionState.Inactive)
        {
            return;
        }

        _commandState = CommandSessionState.Inactive;
        if (_activeDiscovery is { } active)
        {
            _activeDiscovery = null;
            _targetDiscovery.Tell(
                new CancelTargetDiscovery(active.RequestId));
        }

        _inputObserver.Tell(new CommandSessionEnded());
    }

    private sealed record ActiveTargetDiscovery(
        WorkflowGeneration Generation,
        TargetDiscoveryRequestId RequestId);

    private enum CommandSessionState
    {
        Inactive,
        Command,
        PointerPrefix,
    }
}

[ValueObject<long>(conversions: Conversions.None)]
internal readonly partial struct WorkflowGeneration
{
    private static Validation Validate(long value) =>
        value <= 0
            ? Validation.Invalid("A workflow generation must be positive.")
            : Validation.Ok;
}
