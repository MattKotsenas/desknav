using System.Threading.Channels;

using Akka.Actor;

namespace Desknav.ControlPlane.Tests;

internal sealed class RecordingActor : ReceiveActor
{
    private readonly ChannelWriter<object> _writer;

    public RecordingActor(ChannelWriter<object> writer)
    {
        _writer = writer;
        ReceiveAny(message =>
        {
            if (!_writer.TryWrite(message))
            {
                throw new InvalidOperationException(
                    "The test recorder rejected a message.");
            }
        });
    }

    protected override void PostStop() => _writer.TryComplete();
}
