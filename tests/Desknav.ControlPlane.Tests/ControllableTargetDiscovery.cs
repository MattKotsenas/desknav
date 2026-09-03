using System.Threading.Channels;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

internal sealed class ControllableTargetDiscovery : ITargetDiscovery
{
    private readonly Channel<DiscoveryEvent> _events =
        Channel.CreateBounded<DiscoveryEvent>(
            new BoundedChannelOptions(4)
            {
                SingleReader = true,
                SingleWriter = false,
            });
    private readonly CancellationToken _timeout;
    private readonly bool _throwOnCancellation;
    private readonly bool _blockSynchronousPrefix;
    private readonly bool _blockCancellationCallback;
    private readonly TaskCompletionSource _prefixRelease = new();
    private readonly TaskCompletionSource _callbackRelease = new();
    private int _activeCalls;

    public ControllableTargetDiscovery(
        CancellationToken timeout,
        bool throwOnCancellation = false,
        bool blockSynchronousPrefix = false,
        bool blockCancellationCallback = false)
    {
        _timeout = timeout;
        _throwOnCancellation = throwOnCancellation;
        _blockSynchronousPrefix = blockSynchronousPrefix;
        _blockCancellationCallback = blockCancellationCallback;
    }

    public async Task<TargetDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        var activeCalls = Interlocked.Increment(ref _activeCalls);
        if (activeCalls
            > TargetDiscoveryActor.MaximumConcurrentOperations)
        {
            Interlocked.Decrement(ref _activeCalls);
            throw new InvalidOperationException(
                "Too many concurrent discovery calls.");
        }

        DiscoveryCall? call = null;
        try
        {
            if (_throwOnCancellation)
            {
                cancellationToken.Register(
                    () => throw new InvalidOperationException(
                        "Cancellation callback failed."));
            }
            else if (_blockCancellationCallback)
            {
                cancellationToken.Register(
                    () => _callbackRelease.Task
                        .GetAwaiter()
                        .GetResult());
            }

            call = new DiscoveryCall(
                cancellationToken,
                _events.Writer);
            if (!_events.Writer.TryWrite(new DiscoveryStarted(call)))
            {
                throw new InvalidOperationException(
                    "The discovery event recorder rejected a start.");
            }

            if (_blockSynchronousPrefix)
            {
                _prefixRelease.Task.GetAwaiter().GetResult();
            }

            return await call.Completion;
        }
        finally
        {
            call?.MarkExecutionEnded();
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    public Task EventsCompletion => _events.Reader.Completion;

    public async Task<DiscoveryEvent> ReadEventAsync() =>
        await _events.Reader.ReadAsync(_timeout);

    public async Task<T> ReadEventAsync<T>() =>
        Assert.IsType<T>(
            await ReadEventAsync());

    public async Task<DiscoveryCall> ReadStartedCallAsync() =>
        (await ReadEventAsync<DiscoveryStarted>()).Call;

    public async Task<(
        DiscoveryCanceled Canceled,
        DiscoveryCall Started)> ReadCancellationAndStartAsync()
    {
        var events = new[]
        {
            await ReadEventAsync(),
            await ReadEventAsync(),
        };
        return (
            Assert.Single(events.OfType<DiscoveryCanceled>()),
            Assert.Single(events.OfType<DiscoveryStarted>()).Call);
    }

    public void ReleaseSynchronousPrefix() =>
        _prefixRelease.SetResult();

    public void ReleaseCancellationCallback() =>
        _callbackRelease.SetResult();

    public void CompleteEvents() => _events.Writer.TryComplete();
}

internal sealed class DiscoveryCall
{
    private readonly TaskCompletionSource<TargetDiscoveryResult>
        _completion = new();
    private readonly TaskCompletionSource _executionEnded = new();
    private readonly CancellationTokenRegistration _registration;

    public DiscoveryCall(
        CancellationToken cancellationToken,
        ChannelWriter<DiscoveryEvent> events)
    {
        CancellationToken = cancellationToken;
        _registration = cancellationToken.Register(
            () =>
            {
                if (!events.TryWrite(
                        new DiscoveryCanceled(this)))
                {
                    throw new InvalidOperationException(
                        "The discovery event recorder rejected"
                        + " cancellation.");
                }
            });
    }

    public CancellationToken CancellationToken { get; }

    public Task<TargetDiscoveryResult> Completion => _completion.Task;

    public Task ExecutionEnded => _executionEnded.Task;

    public void Complete(params DesktopTarget[] targets)
    {
        _completion.SetResult(
            new TargetDiscoveryResult.Succeeded([.. targets]));
        _registration.Dispose();
    }

    public void FailExpected()
    {
        _completion.SetResult(new TargetDiscoveryResult.Failed());
        _registration.Dispose();
    }

    public void Fail(Exception exception)
    {
        _completion.SetException(exception);
        _registration.Dispose();
    }

    public void MarkExecutionEnded() =>
        _executionEnded.SetResult();
}

internal abstract record DiscoveryEvent;

internal sealed record DiscoveryStarted(DiscoveryCall Call)
    : DiscoveryEvent;

internal sealed record DiscoveryCanceled(DiscoveryCall Call)
    : DiscoveryEvent;
