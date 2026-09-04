using System.Collections.Concurrent;
using System.Threading.Channels;

using Desknav.ControlPlane;

namespace Desknav.UI.Tests;

internal sealed class ControllableOverlayRenderer : IOverlayRenderer
{
    private readonly Channel<OverlayEvent> _events =
        Channel.CreateBounded<OverlayEvent>(
            new BoundedChannelOptions(32)
            {
                SingleReader = true,
                SingleWriter = false,
            });
    private readonly CancellationToken _timeout;
    private readonly ConcurrentQueue<PreparedScene> _activations = [];
    private readonly ConcurrentQueue<PreparationCall> _preparations = [];
    private int _activationInProgress;

    public ControllableOverlayRenderer(CancellationToken timeout)
    {
        _timeout = timeout;
    }

    public IReadOnlyCollection<PreparedScene> Activations =>
        [.. _activations];

    public IReadOnlyCollection<PreparationCall> Preparations =>
        [.. _preparations];

    public PreparedScene? CurrentScene { get; private set; }

    public async Task<IPreparedScene> PrepareAsync(
        TargetPresentation presentation,
        CancellationToken cancellationToken)
    {
        var call = new PreparationCall(
            presentation,
            cancellationToken,
            _events.Writer);
        _preparations.Enqueue(call);
        WriteEvent(new PreparationStarted(call));

        using var registration = cancellationToken.Register(
            () => WriteEvent(new PreparationCanceled(call)));
        try
        {
            return await call.Completion.ConfigureAwait(false);
        }
        finally
        {
            call.MarkExecutionEnded();
        }
    }

    public async Task ActivateAsync(IPreparedScene scene)
    {
        var prepared = Assert.IsType<PreparedScene>(scene);
        if (Interlocked.Exchange(ref _activationInProgress, 1) != 0)
        {
            throw new InvalidOperationException(
                "Overlay activations must not overlap.");
        }

        var previous = CurrentScene;
        var call = new ActivationCall();
        WriteEvent(new ActivationStarted(call));
        try
        {
            await call.Completion.ConfigureAwait(false);
            if (prepared.IsDisposed)
            {
                throw new InvalidOperationException(
                    "The scene was disposed before activation completed.");
            }
            if (previous?.IsDisposed == true)
            {
                throw new InvalidOperationException(
                    "The outgoing scene was disposed before activation"
                    + " completed.");
            }

            CurrentScene = prepared;
            _activations.Enqueue(prepared);
            WriteEvent(new ActivationCompleted());
        }
        finally
        {
            Volatile.Write(ref _activationInProgress, 0);
        }
    }

    public async Task<T> ReadEventAsync<T>()
        where T : OverlayEvent =>
        Assert.IsType<T>(
            await _events.Reader.ReadAsync(_timeout));

    public async Task<OverlayEvent> ReadEventAsync() =>
        await _events.Reader.ReadAsync(_timeout);

    public async Task<(
        PreparationCanceled Canceled,
        PreparationCall Started)> ReadCancellationAndStartAsync()
    {
        var events = new[]
        {
            await _events.Reader.ReadAsync(_timeout),
            await _events.Reader.ReadAsync(_timeout),
        };
        return (
            Assert.Single(events.OfType<PreparationCanceled>()),
            Assert.Single(events.OfType<PreparationStarted>()).Call);
    }

    private void WriteEvent(OverlayEvent item)
    {
        if (!_events.Writer.TryWrite(item))
        {
            throw new InvalidOperationException(
                "The overlay event recorder rejected an event.");
        }
    }
}

internal sealed class PreparationCall
{
    private readonly TaskCompletionSource<IPreparedScene> _completion = new();
    private readonly TaskCompletionSource _executionEnded = new();

    public PreparationCall(
        TargetPresentation presentation,
        CancellationToken cancellationToken,
        ChannelWriter<OverlayEvent> events)
    {
        Presentation = presentation;
        CancellationToken = cancellationToken;
        Scene = new PreparedScene(events);
    }

    public TargetPresentation Presentation { get; }

    public CancellationToken CancellationToken { get; }

    public PreparedScene Scene { get; }

    public Task<IPreparedScene> Completion => _completion.Task;

    public Task ExecutionEnded => _executionEnded.Task;

    public void Complete() => _completion.SetResult(Scene);

    public void Fail(Exception exception) =>
        _completion.SetException(exception);

    public void MarkExecutionEnded() => _executionEnded.SetResult();
}

internal sealed class ActivationCall
{
    private readonly TaskCompletionSource _completion = new();

    public Task Completion => _completion.Task;

    public void Complete() => _completion.SetResult();

    public void Fail(Exception exception) =>
        _completion.SetException(exception);
}

internal sealed class PreparedScene(ChannelWriter<OverlayEvent> events)
    : IPreparedScene
{
    private int _isDisposed;
    private Exception? _disposalFailure;

    public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    public void FailDisposal(Exception exception) =>
        _disposalFailure = exception;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            throw new InvalidOperationException(
                "A prepared scene was disposed more than once.");
        }

        if (!events.TryWrite(new SceneDisposed(this)))
        {
            throw new InvalidOperationException(
                "The overlay event recorder rejected scene disposal.");
        }

        return _disposalFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(_disposalFailure);
    }
}

internal abstract record OverlayEvent;

internal sealed record PreparationStarted(PreparationCall Call)
    : OverlayEvent;

internal sealed record PreparationCanceled(PreparationCall Call)
    : OverlayEvent;

internal sealed record ActivationStarted(ActivationCall Call)
    : OverlayEvent;

internal sealed record ActivationCompleted : OverlayEvent;

internal sealed record SceneDisposed(PreparedScene Scene)
    : OverlayEvent;