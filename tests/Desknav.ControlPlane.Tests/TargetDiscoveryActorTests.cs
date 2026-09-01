using System.Threading.Channels;

using Akka.Actor;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class TargetDiscoveryActorTests
{
    [Fact]
    public async Task CurrentDiscoveryReportsSnapshotWithRequestId()
    {
        await using var harness = new ActorHarness();
        var requestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(requestId));
        var call = await harness.Discovery.ReadStartedCallAsync();
        call.Complete();

        var completed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryCompleted>();
        Assert.Equal(requestId, completed.Snapshot.RequestId);
    }

    [Fact]
    public async Task ReplacementStartsBeforeCancellationCompletes()
    {
        await using var harness = new ActorHarness();
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        Assert.False(first.CancellationToken.IsCancellationRequested);

        harness.Actor.Tell(new CancelTargetDiscovery(firstRequestId));
        harness.Actor.Tell(new DiscoverTargets(secondRequestId));
        var (canceled, second) =
            await ReadCancellationAndStartAsync(harness.Discovery);

        Assert.Same(first, canceled.Call);
        Assert.True(first.CancellationToken.IsCancellationRequested);

        first.Complete();
        second.Complete();

        var completed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryCompleted>();
        Assert.Equal(secondRequestId, completed.Snapshot.RequestId);
        await harness.AssertNoMoreCoordinatorMessagesAsync();
    }

    [Fact]
    public async Task PendingDiscoveryCoalescesToNewestRequest()
    {
        await using var harness = new ActorHarness();
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();
        var supersededPendingRequestId = TargetDiscoveryRequestId.New();
        var newestRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        Assert.False(first.CancellationToken.IsCancellationRequested);
        harness.Actor.Tell(new CancelTargetDiscovery(firstRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        harness.Actor.Tell(new DiscoverTargets(secondRequestId));
        var second = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(secondRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        harness.Actor.Tell(
            new DiscoverTargets(supersededPendingRequestId));
        harness.Actor.Tell(new DiscoverTargets(newestRequestId));

        first.Complete();
        var newest = await harness.Discovery.ReadStartedCallAsync();
        newest.Complete();

        var superseded =
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>();
        var completed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryCompleted>();
        Assert.Equal(
            supersededPendingRequestId,
            superseded.RequestId);
        Assert.Equal(newestRequestId, completed.Snapshot.RequestId);

        second.Complete();
        await harness.AssertNoMoreCoordinatorMessagesAsync();
        await harness.AssertNoMoreDiscoveryEventsAsync();
    }

    [Fact]
    public async Task CanceledPendingDiscoveryNeverStarts()
    {
        await using var harness = new ActorHarness();
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();
        var pendingRequestId = TargetDiscoveryRequestId.New();
        var nextRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(firstRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        harness.Actor.Tell(new DiscoverTargets(secondRequestId));
        var second = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(secondRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        harness.Actor.Tell(new DiscoverTargets(pendingRequestId));
        harness.Actor.Tell(new CancelTargetDiscovery(pendingRequestId));
        first.Complete();
        second.Complete();
        await first.ExecutionEnded;
        await second.ExecutionEnded;
        harness.Actor.Tell(
            new TargetDiscoveryActor.DiscoveryFinished(
                firstRequestId,
                WasCancellationRequested: true,
                TargetDiscoveryResult.Failed));
        harness.Actor.Tell(
            new TargetDiscoveryActor.DiscoveryFinished(
                secondRequestId,
                WasCancellationRequested: true,
                TargetDiscoveryResult.Failed));
        harness.Actor.Tell(new DiscoverTargets(nextRequestId));

        var next = await harness.Discovery.ReadStartedCallAsync();
        next.Complete();
        var completed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryCompleted>();

        Assert.Equal(nextRequestId, completed.Snapshot.RequestId);
        await harness.AssertNoMoreDiscoveryEventsAsync();
    }

    [Fact]
    public async Task StoppingActorCancelsInFlightDiscovery()
    {
        await using var harness = new ActorHarness();

        harness.Actor.Tell(
            new DiscoverTargets(TargetDiscoveryRequestId.New()));
        var call = await harness.Discovery.ReadStartedCallAsync();

        harness.System.Stop(harness.Actor);
        var canceled =
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        Assert.Same(call, canceled.Call);
        call.Complete();
    }

    [Fact]
    public async Task ConfiguredCancellationTimeoutFires()
    {
        await using var harness = new ActorHarness(
            operationTimeout: TimeSpan.FromMilliseconds(100),
            testTimeout: TimeSpan.FromSeconds(2));
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();
        var pendingRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(firstRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        harness.Actor.Tell(new DiscoverTargets(secondRequestId));
        var second = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(secondRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        harness.Actor.Tell(new DiscoverTargets(pendingRequestId));

        var failures = new[]
        {
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
        };
        Assert.Contains(
            pendingRequestId,
            failures.Select(failure => failure.RequestId));

        first.Complete();
        second.Complete();
    }

    [Fact]
    public async Task BlockingDiscoveryPrefixDoesNotDelayCancellation()
    {
        await using var harness = new ActorHarness(
            blockSynchronousPrefix: true);
        var requestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(requestId));
        var call = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(requestId));

        var canceled =
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        Assert.Same(call, canceled.Call);

        harness.Discovery.ReleaseSynchronousPrefix();
        call.Complete();
    }

    [Fact]
    public async Task BlockingCancellationCallbackDoesNotDelayReplacement()
    {
        await using var harness = new ActorHarness(
            blockCancellationCallback: true);
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(firstRequestId));
        harness.Actor.Tell(new DiscoverTargets(secondRequestId));

        var (canceled, second) =
            await ReadCancellationAndStartAsync(harness.Discovery);
        Assert.Same(first, canceled.Call);
        Assert.True(first.CancellationToken.IsCancellationRequested);

        harness.Discovery.ReleaseCancellationCallback();
        first.Complete();
        second.Complete();
    }

    [Fact]
    public async Task CanceledFaultStaysSilentAndNextRequestCompletes()
    {
        await using var harness = new ActorHarness();
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(firstRequestId));
        var canceled =
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        Assert.Same(first, canceled.Call);
        first.Fail(new InvalidOperationException("Canceled call unwound."));

        harness.Actor.Tell(new DiscoverTargets(secondRequestId));
        var second = await harness.Discovery.ReadStartedCallAsync();
        second.Complete();

        var completed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryCompleted>();
        Assert.Equal(secondRequestId, completed.Snapshot.RequestId);
        await harness.AssertNoMoreCoordinatorMessagesAsync();
    }

    [Fact]
    public async Task UnexpectedDiscoveryFailureTerminatesActorSystem()
    {
        await using var harness = new ActorHarness();

        harness.Actor.Tell(
            new DiscoverTargets(TargetDiscoveryRequestId.New()));
        var call = await harness.Discovery.ReadStartedCallAsync();

        call.Fail(new InvalidOperationException("Discovery failed."));

        await harness.System.WhenTerminated.WaitAsync(harness.TimeoutToken);
    }

    [Fact]
    public async Task ExpectedDiscoveryFailureReportsRequestId()
    {
        await using var harness = new ActorHarness();
        var requestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(requestId));
        var call = await harness.Discovery.ReadStartedCallAsync();
        call.FailExpected();

        var failed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>();
        Assert.Equal(requestId, failed.RequestId);
    }

    [Fact]
    public async Task OperationTimeoutFailsCurrentAndFutureRequests()
    {
        await using var harness = new ActorHarness();
        var timedOutRequestId = TargetDiscoveryRequestId.New();
        var nextRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(timedOutRequestId));
        var call = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(
            new TargetDiscoveryActor.OperationTimedOut(
                timedOutRequestId));

        var failed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>();
        var canceled =
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        Assert.Equal(timedOutRequestId, failed.RequestId);
        Assert.Same(call, canceled.Call);

        harness.Actor.Tell(new DiscoverTargets(nextRequestId));
        var nextFailed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>();
        Assert.Equal(nextRequestId, nextFailed.RequestId);

        call.Complete();
        await harness.AssertNoMoreDiscoveryEventsAsync();
    }

    [Fact]
    public async Task ConfiguredOperationTimeoutFires()
    {
        await using var harness = new ActorHarness(
            operationTimeout: TimeSpan.FromMilliseconds(100),
            testTimeout: TimeSpan.FromSeconds(2));
        var requestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(requestId));
        var call = await harness.Discovery.ReadStartedCallAsync();

        var failed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>();
        var canceled =
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        Assert.Equal(requestId, failed.RequestId);
        Assert.Same(call, canceled.Call);

        call.Complete();
    }

    [Fact]
    public async Task TimeoutFailsRunningAndPendingRequests()
    {
        await using var harness = new ActorHarness();
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();
        var pendingRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new DiscoverTargets(secondRequestId));
        var second = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new DiscoverTargets(pendingRequestId));
        harness.Actor.Tell(
            new TargetDiscoveryActor.OperationTimedOut(firstRequestId));

        var failures = new[]
        {
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
        };
        var canceled = new[]
        {
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>(),
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>(),
        };

        var expectedFailures =
            new HashSet<TargetDiscoveryRequestId>
            {
                firstRequestId,
                secondRequestId,
                pendingRequestId,
            };
        Assert.True(
            expectedFailures.SetEquals(
                failures.Select(failure => failure.RequestId)));
        Assert.Contains(first, canceled.Select(item => item.Call));
        Assert.Contains(second, canceled.Select(item => item.Call));

        first.Complete();
        second.Complete();
    }

    [Fact]
    public async Task CanceledPredecessorTimeoutDoesNotFailCurrentRequest()
    {
        await using var harness = new ActorHarness();
        var predecessorRequestId = TargetDiscoveryRequestId.New();
        var currentRequestId = TargetDiscoveryRequestId.New();
        var nextRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(predecessorRequestId));
        var predecessor = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(
            new CancelTargetDiscovery(predecessorRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        harness.Actor.Tell(new DiscoverTargets(currentRequestId));
        var current = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(
            new TargetDiscoveryActor.OperationTimedOut(
                predecessorRequestId));
        harness.Actor.Tell(
            new TargetDiscoveryActor.CancellationTimedOut(
                predecessorRequestId));
        current.Complete();

        var completed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryCompleted>();
        Assert.Equal(currentRequestId, completed.Snapshot.RequestId);

        harness.Actor.Tell(new DiscoverTargets(nextRequestId));
        var next = await harness.Discovery.ReadStartedCallAsync();
        next.Complete();
        predecessor.Complete();
    }

    [Fact]
    public async Task SoleExpiredCancellationLeavesOneSlotAvailable()
    {
        await using var harness = new ActorHarness();
        var predecessorRequestId = TargetDiscoveryRequestId.New();
        var nextRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(predecessorRequestId));
        var predecessor = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(
            new CancelTargetDiscovery(predecessorRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        harness.Actor.Tell(
            new TargetDiscoveryActor.CancellationTimedOut(
                predecessorRequestId));

        harness.Actor.Tell(new DiscoverTargets(nextRequestId));
        var next = await harness.Discovery.ReadStartedCallAsync();
        next.Complete();
        var completed =
            await harness.ReadCoordinatorAsync<TargetDiscoveryCompleted>();

        Assert.Equal(nextRequestId, completed.Snapshot.RequestId);
        predecessor.Complete();
    }

    [Fact]
    public async Task ExhaustedCancellationBudgetsFailPendingRequest()
    {
        await using var harness = new ActorHarness();
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();
        var pendingRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(firstRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        harness.Actor.Tell(new DiscoverTargets(secondRequestId));
        var second = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(secondRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        harness.Actor.Tell(new DiscoverTargets(pendingRequestId));

        harness.Actor.Tell(
            new TargetDiscoveryActor.CancellationTimedOut(firstRequestId));
        harness.Actor.Tell(
            new TargetDiscoveryActor.CancellationTimedOut(secondRequestId));

        var failures = new[]
        {
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
        };
        var expectedFailures = new HashSet<TargetDiscoveryRequestId>
        {
            firstRequestId,
            secondRequestId,
            pendingRequestId,
        };
        Assert.True(
            expectedFailures.SetEquals(
                failures.Select(failure => failure.RequestId)));

        var futureRequestId = TargetDiscoveryRequestId.New();
        harness.Actor.Tell(
            new TargetDiscoveryActor.DiscoveryFinished(
                firstRequestId,
                WasCancellationRequested: false,
                TargetDiscoveryResult.Succeeded));
        harness.Actor.Tell(new DiscoverTargets(futureRequestId));
        var futureFailure =
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>();
        Assert.Equal(futureRequestId, futureFailure.RequestId);

        first.Complete();
        second.Complete();
    }

    [Fact]
    public async Task CancellationCallbackFailureTerminatesActorSystem()
    {
        await using var harness = new ActorHarness(
            throwOnCancellation: true);
        var requestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(requestId));
        var call = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(requestId));

        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        await harness.System.WhenTerminated.WaitAsync(harness.TimeoutToken);
        call.Complete();
    }

    [Fact]
    public async Task DuplicateRequestIdTerminatesActorSystem()
    {
        await using var harness = new ActorHarness();
        var requestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(requestId));
        var call = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new DiscoverTargets(requestId));

        var canceled =
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        Assert.Same(call, canceled.Call);
        await harness.System.WhenTerminated.WaitAsync(harness.TimeoutToken);
        call.Complete();
    }

    [Fact]
    public async Task ShutdownCancelsEveryOperationWhenCallbacksThrow()
    {
        await using var harness = new ActorHarness(
            throwOnCancellation: true);

        harness.Actor.Tell(
            new DiscoverTargets(TargetDiscoveryRequestId.New()));
        var first = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(
            new DiscoverTargets(TargetDiscoveryRequestId.New()));
        var second = await harness.Discovery.ReadStartedCallAsync();

        harness.System.Stop(harness.Actor);
        var firstCanceled =
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        var secondCanceled =
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();

        var canceledCalls =
            new[] { firstCanceled.Call, secondCanceled.Call };
        Assert.Contains(first, canceledCalls);
        Assert.Contains(second, canceledCalls);
        first.Complete();
        second.Complete();
    }

    private sealed class ActorHarness : IAsyncDisposable
    {
        private readonly Channel<object> _coordinatorMessages =
            Channel.CreateBounded<object>(
                new BoundedChannelOptions(4)
                {
                    SingleReader = true,
                    SingleWriter = true,
                });
        private readonly CancellationTokenSource _timeout;
        private readonly IActorRef _coordinator;

        public ActorHarness(
            bool throwOnCancellation = false,
            bool blockSynchronousPrefix = false,
            bool blockCancellationCallback = false,
            TimeSpan? operationTimeout = null,
            TimeSpan? testTimeout = null)
        {
            _timeout = new CancellationTokenSource(
                testTimeout ?? TimeSpan.FromSeconds(10));
            System = ActorSystem.Create(
                $"target-discovery-{Guid.NewGuid():N}");
            Discovery = new ControllableTargetDiscovery(
                _timeout.Token,
                throwOnCancellation,
                blockSynchronousPrefix,
                blockCancellationCallback);
            _coordinator = System.ActorOf(
                Props.Create(
                    () => new RecordingActor(
                        _coordinatorMessages.Writer)));
            Actor = System.ActorOf(
                TargetDiscoveryActor.CreateProps(
                    _coordinator,
                    Discovery,
                    operationTimeout ?? TimeSpan.FromHours(1)));
        }

        public ActorSystem System { get; }

        public IActorRef Actor { get; }

        public ControllableTargetDiscovery Discovery { get; }

        public CancellationToken TimeoutToken => _timeout.Token;

        public async Task<T> ReadCoordinatorAsync<T>() =>
            Assert.IsType<T>(
                await _coordinatorMessages.Reader.ReadAsync(TimeoutToken));

        public async Task AssertNoMoreCoordinatorMessagesAsync()
        {
            _coordinator.Tell(PoisonPill.Instance);
            await _coordinatorMessages.Reader.Completion.WaitAsync(
                TimeoutToken);
        }

        public async Task AssertNoMoreDiscoveryEventsAsync()
        {
            await System.Terminate();
            Discovery.CompleteEvents();
            await Discovery.EventsCompletion.WaitAsync(TimeoutToken);
        }

        public async ValueTask DisposeAsync()
        {
            await System.Terminate();
            _timeout.Dispose();
        }
    }

    private sealed class ControllableTargetDiscovery : ITargetDiscovery
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
            bool throwOnCancellation,
            bool blockSynchronousPrefix,
            bool blockCancellationCallback)
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

        public void ReleaseSynchronousPrefix() =>
            _prefixRelease.SetResult();

        public void ReleaseCancellationCallback() =>
            _callbackRelease.SetResult();

        public void CompleteEvents() => _events.Writer.TryComplete();
    }

    private sealed class DiscoveryCall
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

        public void Complete()
        {
            _completion.SetResult(TargetDiscoveryResult.Succeeded);
            _registration.Dispose();
        }

        public void FailExpected()
        {
            _completion.SetResult(TargetDiscoveryResult.Failed);
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

    private abstract record DiscoveryEvent;

    private sealed record DiscoveryStarted(DiscoveryCall Call)
        : DiscoveryEvent;

    private sealed record DiscoveryCanceled(DiscoveryCall Call)
        : DiscoveryEvent;

    private static async Task<(
        DiscoveryCanceled Canceled,
        DiscoveryCall Started)> ReadCancellationAndStartAsync(
        ControllableTargetDiscovery discovery)
    {
        var events = new[]
        {
            await discovery.ReadEventAsync(),
            await discovery.ReadEventAsync(),
        };
        var canceled = Assert.Single(
            events.OfType<DiscoveryCanceled>());
        var started = Assert.Single(
            events.OfType<DiscoveryStarted>());
        return (canceled, started.Call);
    }
}
