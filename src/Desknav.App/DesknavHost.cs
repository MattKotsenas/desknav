using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;

using Akka.Actor;

using Desknav.ControlPlane;
using Desknav.UI;

namespace Desknav.App;

internal sealed class DesknavHost
{
    private readonly KanataTcpIngress _ingress;
    private readonly ITargetDiscovery _targetDiscovery;
    private readonly IOverlayRenderer _overlayRenderer;
    private readonly TimeSpan _targetDiscoveryTimeout;

    public DesknavHost(
        IPEndPoint kanataEndpoint,
        ITargetDiscovery targetDiscovery,
        IOverlayRenderer overlayRenderer,
        TimeSpan targetDiscoveryTimeout)
    {
        ArgumentNullException.ThrowIfNull(kanataEndpoint);
        ArgumentNullException.ThrowIfNull(targetDiscovery);
        ArgumentNullException.ThrowIfNull(overlayRenderer);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            targetDiscoveryTimeout,
            TimeSpan.Zero);

        _ingress = new KanataTcpIngress(kanataEndpoint);
        _targetDiscovery = targetDiscovery;
        _overlayRenderer = overlayRenderer;
        _targetDiscoveryTimeout = targetDiscoveryTimeout;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var shutdown =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        var system = ActorSystem.Create("desknav");
        var applicationFailures = new ConcurrentQueue<Exception>();
        var applicationFailure =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        void ReportFailure(Exception exception)
        {
            applicationFailures.Enqueue(exception);
            applicationFailure.TrySetResult(true);
        }

        var overlayProps = OverlayActor.CreateProps(
            _overlayRenderer,
            ReportFailure,
            out var overlayShutdown);
        var discoveryProps = TargetDiscoveryActor.CreateProps(
            _targetDiscovery,
            _targetDiscoveryTimeout,
            ReportFailure,
            out var discoveryShutdown);

        var overlayOwner = system.ActorOf(overlayProps, "overlay");
        var coordinator = system.ActorOf(
            NavigationCoordinator.CreateProps(
                discoveryProps,
                ActorRefs.Nobody,
                overlayOwner,
                ReportFailure),
            "coordinator");
        var kanataActor = system.ActorOf(
            KanataActor.CreateProps(coordinator),
            "kanata");
        var ingress = _ingress.RunAsync(
            kanataActor,
            shutdown.Token);
        var completed = await Task.WhenAny(
                ingress,
                system.WhenTerminated,
                applicationFailure.Task)
            .ConfigureAwait(false);
        var systemEndedUnexpectedly =
            completed == system.WhenTerminated;

        shutdown.Cancel();
        var ingressFailure = await CaptureFailureAsync(ingress)
            .ConfigureAwait(false);
        await system.Terminate().ConfigureAwait(false);
        var overlayCleanupFailure =
            CaptureFailureAsync(overlayShutdown);
        var discoveryCleanupFailure =
            CaptureFailureAsync(discoveryShutdown);
        await Task.WhenAll(
                overlayCleanupFailure,
                discoveryCleanupFailure)
            .ConfigureAwait(false);
        var failure = CombineFailures(
            [.. applicationFailures],
            NonCancellation(ingressFailure),
            overlayCleanupFailure.Result,
            discoveryCleanupFailure.Result);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (systemEndedUnexpectedly)
        {
            throw new InvalidOperationException(
                "The Desknav actor system terminated unexpectedly.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<Exception?> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return operation.Exception ?? exception;
        }
    }

    private static Exception? NonCancellation(Exception? exception) =>
        exception is OperationCanceledException
            ? null
            : exception;

    private static Exception? CombineFailures(
        IReadOnlyCollection<Exception> componentFailures,
        params Exception?[] candidates)
    {
        var failures = new List<Exception>();
        var seen = new HashSet<Exception>(
            ReferenceEqualityComparer.Instance);
        foreach (var componentFailure in componentFailures)
        {
            AddFailure(componentFailure, failures, seen);
        }
        foreach (var candidate in candidates)
        {
            AddFailure(candidate, failures, seen);
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(
                "Multiple Desknav components failed.",
                failures),
        };
    }

    private static void AddFailure(
        Exception? candidate,
        List<Exception> failures,
        HashSet<Exception> seen)
    {
        if (candidate is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                AddFailure(inner, failures, seen);
            }
            return;
        }

        if (candidate is OperationCanceledException cancellation
            && seen.Add(candidate))
        {
            failures.Add(
                new InvalidOperationException(
                    "A Desknav component canceled unexpectedly.",
                    cancellation));
        }
        else if (candidate is not null && seen.Add(candidate))
        {
            failures.Add(candidate);
        }
    }

}
