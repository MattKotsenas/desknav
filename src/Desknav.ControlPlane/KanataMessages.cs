namespace Desknav.ControlPlane;

public sealed record KanataConnectionId
{
    private KanataConnectionId(Guid value) => Value = value;

    public Guid Value { get; }

    public static KanataConnectionId New() => new(Guid.NewGuid());
}

public readonly record struct KanataIngressOrdinal
{
    public KanataIngressOrdinal(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "A Kanata ingress ordinal must be positive.");
        }

        Value = value;
    }

    public long Value { get; }
}

public sealed record KeyboardLayer
{
    public KeyboardLayer(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
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

public sealed record KeyboardModeObserved(
    KanataConnectionId ConnectionId,
    KanataIngressOrdinal Ordinal,
    KeyboardLayer Layer);

public sealed record KeyboardModeUnavailable(KanataConnectionId ConnectionId);

public sealed record GestureObserved(
    KanataConnectionId ConnectionId,
    KanataIngressOrdinal Ordinal,
    GestureToken Token);

public sealed record TargetDiscoveryRequestId
{
    private TargetDiscoveryRequestId(Guid value) => Value = value;

    public Guid Value { get; }

    public static TargetDiscoveryRequestId New() => new(Guid.NewGuid());
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
    KanataIngressOrdinal Ordinal,
    KanataServerFrame Frame);
