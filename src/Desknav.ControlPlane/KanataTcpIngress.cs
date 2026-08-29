using System.Net;
using System.Net.Sockets;

using Akka.Actor;

namespace Desknav.ControlPlane;

public sealed class KanataTcpIngress
{
    private readonly IPEndPoint _endpoint;

    public KanataTcpIngress(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IPAddress.IsLoopback(endpoint.Address))
        {
            throw new ArgumentException(
                "Kanata TCP ingress must connect through loopback.",
                nameof(endpoint));
        }

        _endpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
    }

    public async Task RunAsync(
        IActorRef boundary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);

        using var client = new TcpClient(_endpoint.AddressFamily)
        {
            NoDelay = true,
        };
        await client
            .ConnectAsync(_endpoint, cancellationToken)
            .ConfigureAwait(false);

        var connectionId = KanataConnectionId.New();
        boundary.Tell(new KanataConnectionOpened(connectionId));

        try
        {
            using var reader = new StreamReader(client.GetStream());
            var ordinal = 0L;
            while (await reader
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false) is { } line)
            {
                var frame = KanataFrameParser.Parse(line);
                boundary.Tell(
                    new KanataFrameReceived(
                        connectionId,
                        new KanataIngressOrdinal(++ordinal),
                        frame));
            }
        }
        finally
        {
            boundary.Tell(new KanataConnectionClosed(connectionId));
        }
    }
}
