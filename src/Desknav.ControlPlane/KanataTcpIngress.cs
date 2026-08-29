using System.Net;
using System.Net.Sockets;

using Akka.Actor;

namespace Desknav.ControlPlane;

internal sealed class KanataTcpIngress
{
    private readonly IPEndPoint _endpoint;
    private readonly IKanataFrameParser _frameParser;

    public KanataTcpIngress(
        IPEndPoint endpoint,
        IKanataFrameParser frameParser)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(frameParser);
        if (!IPAddress.IsLoopback(endpoint.Address))
        {
            throw new ArgumentException(
                "Kanata TCP ingress must connect through loopback.",
                nameof(endpoint));
        }

        _endpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
        _frameParser = frameParser;
    }

    public async Task RunAsync(
        IActorRef kanataActor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kanataActor);

        using var client = new TcpClient(_endpoint.AddressFamily)
        {
            NoDelay = true,
        };
        await client
            .ConnectAsync(_endpoint, cancellationToken)
            .ConfigureAwait(false);

        var connectionId = KanataConnectionId.New();
        kanataActor.Tell(new KanataConnectionOpened(connectionId));

        try
        {
            // Kanata emits short, human-rate JSON lines; pipelines would add
            // buffering complexity without meaningful allocation savings.
            using var reader = new StreamReader(client.GetStream());
            var sequence = 0L;
            while (await reader
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false) is { } line)
            {
                var frame = _frameParser.Parse(line);
                kanataActor.Tell(
                    new KanataFrameReceived(
                        connectionId,
                        KanataFrameSequence.From(++sequence),
                        frame));
            }
        }
        finally
        {
            kanataActor.Tell(new KanataConnectionClosed(connectionId));
        }
    }
}
