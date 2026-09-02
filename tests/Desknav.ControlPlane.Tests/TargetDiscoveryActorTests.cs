using System.Threading.Channels;

using Akka.Actor;
using Akka.Configuration;
using Akka.TestKit;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class TargetDiscoveryActorTests
{
    private static readonly TimeSpan OperationTimeout =
        TimeSpan.FromMinutes(1);

    [Fact]
    public async Task OperationDisposalWaitsForExecution()
    {
        using var cancellation = new CancellationTokenSource();
        var timeout = new RecordingCancelable();
        var execution = new TaskCompletionSource();
        var operation =
            new TargetDiscoveryActor.DiscoveryOperation(
                cancellation,
                timeout,
                execution.Task);
        Assert.False(timeout.IsDisposed);

        var disposal = operation.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        execution.SetResult();
        await disposal;

        Assert.True(timeout.IsCancellationRequested);
        Assert.True(timeout.IsDisposed);
        Assert.Throws<ObjectDisposedException>(
            () => _ = cancellation.Token);
    }

    [Fact]
    public async Task OperationDisposalWaitsForCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var timeout = new RecordingCancelable();
        var callbackStarted = new TaskCompletionSource();
        var callbackRelease = new TaskCompletionSource();
        using var registration = cancellation.Token.Register(
            () =>
            {
                callbackStarted.SetResult();
                callbackRelease.Task.GetAwaiter().GetResult();
            });
        var operation =
            new TargetDiscoveryActor.DiscoveryOperation(
                cancellation,
                timeout,
                Task.CompletedTask);
        Assert.False(timeout.IsDisposed);

        Assert.True(
            operation.TryRequestCancellation(
                out var cancellationTask));
        await callbackStarted.Task;
        var disposal = operation.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        callbackRelease.SetResult();
        await cancellationTask;
        await disposal;

        Assert.True(timeout.IsCancellationRequested);
        Assert.True(timeout.IsDisposed);
        Assert.Throws<ObjectDisposedException>(
            () => _ = cancellation.Token);
    }

    [Fact]
    public async Task ReplacingTimeoutDisposesSupersededHandle()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new RecordingCancelable();
        var replacement = new RecordingCancelable();
        var operation =
            new TargetDiscoveryActor.DiscoveryOperation(
                cancellation,
                original,
                Task.CompletedTask);
        Assert.False(original.IsDisposed);

        operation.ReplaceTimeout(replacement);

        Assert.True(original.IsCancellationRequested);
        Assert.True(original.IsDisposed);
        Assert.False(replacement.IsCancellationRequested);

        await operation.DisposeAsync();

        Assert.True(replacement.IsCancellationRequested);
        Assert.True(replacement.IsDisposed);
        Assert.Throws<ObjectDisposedException>(
            operation.CancelTimeout);
        Assert.Throws<ObjectDisposedException>(
            () => operation.ReplaceTimeout(
                new RecordingCancelable()));
        Assert.Throws<ObjectDisposedException>(
            () => operation.TryRequestCancellation(out _));
    }

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
            await harness.Discovery.ReadCancellationAndStartAsync();

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
            operationTimeout: OperationTimeout,
            useVirtualTime: true);
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();
        var supersededPendingRequestId = TargetDiscoveryRequestId.New();
        var pendingRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        harness.Scheduler.Advance(OperationTimeout / 2);
        harness.Actor.Tell(new CancelTargetDiscovery(firstRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        await harness.FlushActorAsync();

        harness.Actor.Tell(new DiscoverTargets(secondRequestId));
        var second = await harness.Discovery.ReadStartedCallAsync();
        harness.Actor.Tell(new CancelTargetDiscovery(secondRequestId));
        await harness.Discovery.ReadEventAsync<DiscoveryCanceled>();
        await harness.FlushActorAsync();
        harness.Actor.Tell(
            new DiscoverTargets(supersededPendingRequestId));
        await harness.FlushActorAsync();

        harness.Scheduler.Advance(OperationTimeout / 2);
        harness.Actor.Tell(
            new CancelTargetDiscovery(firstRequestId));
        await harness.FlushActorAsync();
        harness.Scheduler.Advance(
            OperationTimeout / 2 - TimeSpan.FromTicks(1));
        harness.Actor.Tell(new DiscoverTargets(pendingRequestId));
        // This response proves the pending request was handled before expiry.
        var superseded =
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>();
        Assert.Equal(
            supersededPendingRequestId,
            superseded.RequestId);

        harness.Scheduler.Advance(TimeSpan.FromTicks(1));

        var failures = new[]
        {
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
        };
        AssertFailedRequestIds(
            failures,
            firstRequestId,
            secondRequestId,
            pendingRequestId);

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
            await harness.Discovery.ReadCancellationAndStartAsync();
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
            operationTimeout: OperationTimeout,
            useVirtualTime: true);
        var firstRequestId = TargetDiscoveryRequestId.New();
        var secondRequestId = TargetDiscoveryRequestId.New();

        harness.Actor.Tell(new DiscoverTargets(firstRequestId));
        var first = await harness.Discovery.ReadStartedCallAsync();
        harness.Scheduler.Advance(
            OperationTimeout - TimeSpan.FromTicks(1));

        harness.Actor.Tell(new DiscoverTargets(secondRequestId));
        var second = await harness.Discovery.ReadStartedCallAsync();
        harness.Scheduler.Advance(TimeSpan.FromTicks(1));

        var failures = new[]
        {
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
            await harness.ReadCoordinatorAsync<TargetDiscoveryFailed>(),
        };
        var canceled = new[]
        {
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>(),
            await harness.Discovery.ReadEventAsync<DiscoveryCanceled>(),
        };
        AssertFailedRequestIds(
            failures,
            firstRequestId,
            secondRequestId);
        Assert.Contains(first, canceled.Select(item => item.Call));
        Assert.Contains(second, canceled.Select(item => item.Call));

        first.Complete();
        second.Complete();
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

        AssertFailedRequestIds(
            failures,
            firstRequestId,
            secondRequestId,
            pendingRequestId);
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
        AssertFailedRequestIds(
            failures,
            firstRequestId,
            secondRequestId,
            pendingRequestId);

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
        private static readonly Config TestSchedulerConfig =
            ConfigurationFactory.ParseString(
                """
                akka.scheduler.implementation =
                    "Akka.TestKit.TestScheduler, Akka.TestKit"
                """);

        private readonly Channel<object> _coordinatorMessages =
            RecordingActor.CreateChannel(4);
        private readonly CancellationTokenSource _timeout;
        private readonly IActorRef _coordinator;

        public ActorHarness(
            bool throwOnCancellation = false,
            bool blockSynchronousPrefix = false,
            bool blockCancellationCallback = false,
            TimeSpan? operationTimeout = null,
            bool useVirtualTime = false)
        {
            _timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var systemName = $"target-discovery-{Guid.NewGuid():N}";
            System = useVirtualTime
                ? ActorSystem.Create(systemName, TestSchedulerConfig)
                : ActorSystem.Create(systemName);
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

        public TestScheduler Scheduler =>
            (TestScheduler)System.Scheduler;

        public CancellationToken TimeoutToken => _timeout.Token;

        public async Task<T> ReadCoordinatorAsync<T>() =>
            Assert.IsType<T>(
                await _coordinatorMessages.Reader.ReadAsync(TimeoutToken));

        public async Task FlushActorAsync() =>
            await ActorTestHelpers.FlushAsync(Actor, TimeoutToken);

        public async Task AssertNoMoreCoordinatorMessagesAsync()
        {
            _coordinator.Tell(PoisonPill.Instance);
            await _coordinatorMessages.Reader.Completion.WaitAsync(
                TimeoutToken);
        }

        public async Task AssertNoMoreDiscoveryEventsAsync()
        {
            await System.Terminate().WaitAsync(TimeoutToken);
            Discovery.CompleteEvents();
            await Discovery.EventsCompletion.WaitAsync(TimeoutToken);
        }

        public async ValueTask DisposeAsync()
        {
            using var cleanupTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await System
                    .Terminate()
                    .WaitAsync(cleanupTimeout.Token);
            }
            finally
            {
                _timeout.Dispose();
            }
        }
    }

    private sealed class RecordingCancelable : ICancelable, IDisposable
    {
        public bool IsCancellationRequested { get; private set; }

        public bool IsDisposed { get; private set; }

        public CancellationToken Token =>
            throw new NotSupportedException();

        public void Cancel()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            IsCancellationRequested = true;
        }

        public void Cancel(bool throwOnFirstException) => Cancel();

        public void CancelAfter(TimeSpan delay)
        {
            throw new NotSupportedException();
        }

        public void CancelAfter(int millisecondsDelay)
        {
            throw new NotSupportedException();
        }

        public void Dispose() => IsDisposed = true;
    }

    private static void AssertFailedRequestIds(
        IEnumerable<TargetDiscoveryFailed> failures,
        params TargetDiscoveryRequestId[] expected)
    {
        var actual = failures
            .Select(failure => failure.RequestId)
            .ToHashSet();
        if (actual.SetEquals(expected))
        {
            return;
        }

        var missing = expected.Except(actual);
        var unexpected = actual.Except(expected);
        Assert.Fail(
            $"Failed request IDs - missing: {FormatRequestIds(missing)};"
            + $" unexpected: {FormatRequestIds(unexpected)}.");
    }

    private static string FormatRequestIds(
        IEnumerable<TargetDiscoveryRequestId> requestIds)
    {
        var formatted = string.Join(
            ", ",
            requestIds
                .Select(requestId => requestId.Value)
                .Order());
        return formatted.Length == 0 ? "(none)" : formatted;
    }
}
