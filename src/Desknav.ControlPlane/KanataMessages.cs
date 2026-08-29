using Vogen;

namespace Desknav.ControlPlane;

[ValueObject<Guid>(conversions: Conversions.None)]
public readonly partial struct KanataConnectionId
{
    public static KanataConnectionId New() => From(Guid.NewGuid());

    private static Validation Validate(Guid value) =>
        value == Guid.Empty
            ? Validation.Invalid("A Kanata connection ID cannot be empty.")
            : Validation.Ok;
}

[ValueObject<long>(conversions: Conversions.None)]
public readonly partial struct KanataFrameSequence
{
    private static Validation Validate(long value) =>
        value <= 0
            ? Validation.Invalid("A Kanata frame sequence must be positive.")
            : Validation.Ok;
}

[ValueObject<string>(conversions: Conversions.None)]
public readonly partial struct KeyboardLayer
{
    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("A keyboard layer cannot be empty.")
            : Validation.Ok;
}

public sealed record GestureToken
{
    public GestureToken(string context, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Context = context;
        Key = key;
    }

    public string Context { get; }

    public string Key { get; }
}

public sealed record KeyboardLayerObserved(
    KanataConnectionId ConnectionId,
    KanataFrameSequence Sequence,
    KeyboardLayer Layer);

public sealed record KeyboardLayerUnavailable(KanataConnectionId ConnectionId);

public sealed record GestureObserved(
    KanataConnectionId ConnectionId,
    KanataFrameSequence Sequence,
    GestureToken Token);

public sealed record CommandInputObserved(GestureToken Token);

public sealed record CommandSessionEnded;

[ValueObject<Guid>(conversions: Conversions.None)]
public readonly partial struct TargetDiscoveryRequestId
{
    public static TargetDiscoveryRequestId New() => From(Guid.NewGuid());

    private static Validation Validate(Guid value) =>
        value == Guid.Empty
            ? Validation.Invalid(
                "A target discovery request ID cannot be empty.")
            : Validation.Ok;
}

public sealed record DiscoverTargets(TargetDiscoveryRequestId RequestId);

internal abstract record KanataServerFrame;

internal sealed record KanataLayerChanged(KeyboardLayer Layer)
    : KanataServerFrame;

internal sealed record KanataGesturePushed(GestureToken Token)
    : KanataServerFrame;

internal sealed record KanataConnectionOpened(KanataConnectionId ConnectionId);

internal sealed record KanataConnectionClosed(KanataConnectionId ConnectionId);

internal sealed record KanataFrameReceived(
    KanataConnectionId ConnectionId,
    KanataFrameSequence Sequence,
    KanataServerFrame Frame);
