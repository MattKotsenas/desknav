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

    public static Func<IActorRef, Props> CreatePropsFactory(
        ChannelWriter<object> writer) =>
        _ => Props.Create(() => new RecordingActor(writer));

    public static Channel<object> CreateChannel(int capacity) =>
        Channel.CreateBounded<object>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
            });

    protected override void PostStop() => _writer.TryComplete();
}
