using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

using Desknav.ControlPlane;
using Desknav.UI;

namespace Desknav.App.Tests;

public sealed class DesknavHostTests
{
    [Fact]
    public async Task TargetCommandPresentsTargetsAndEscapeHidesThem()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new RecordingOverlayRenderer();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer,
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            Assert.False(run.IsCompleted);
            Assert.False(renderer.TryReadActivation(out _));

            await WriteTargetCommandAsync(writer);

            var visible = Assert.IsType<TargetPresentation.Visible>(
                await renderer.ReadActivationAsync(timeout.Token));
            var target = Assert.Single(visible.Map.Targets);
            Assert.Equal(
                new PhysicalRect(10, 20, 31, 41),
                target.Target.Bounds);
            Assert.False(string.IsNullOrEmpty(target.Label.Value));

            await writer.WriteLineAsync(
                """{"MessagePush":{"message":["gesture","command","esc"]}}""");
            await writer.WriteLineAsync(
                """{"LayerChange":{"new":"base"}}""");

            Assert.IsType<TargetPresentation.Hidden>(
                await renderer.ReadActivationAsync(timeout.Token));

            client.Close();
            await run.WaitAsync(timeout.Token);
        }
        finally
        {
            listener.Stop();
            timeout.Cancel();
        }
    }

    [Fact]
    public async Task CancellationStopsAConnectedHost()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var shutdown = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            new RecordingOverlayRenderer(),
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(shutdown.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            Assert.False(run.IsCompleted);

            shutdown.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(timeout.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task OwnerFailureWinsAConcurrentDisconnect()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new ControllableFailingOverlayRenderer();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer,
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await renderer.ActivationStarted.WaitAsync(timeout.Token);
            client.Close();
            renderer.FailActivation();

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => run.WaitAsync(timeout.Token));
            Assert.Contains(
                "Unexpected overlay failure.",
                exception.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task OwnerAndCleanupFailuresAreBothReported()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new ControllableFailingOverlayRenderer(
            failSceneDisposal: true);
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer,
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await renderer.ActivationStarted.WaitAsync(timeout.Token);
            renderer.FailActivation();

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => run.WaitAsync(timeout.Token));
            Assert.Collection(
                exception.InnerExceptions,
                failure => Assert.Equal(
                    "Unexpected overlay failure.",
                    failure.Message),
                failure => Assert.Equal(
                    "Unexpected scene disposal failure.",
                    failure.Message));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task PreparationFailureBeforeDisconnectIsReported()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new ControllableFailingPreparationRenderer();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer,
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await renderer.PreparationStarted.WaitAsync(timeout.Token);
            renderer.FailPreparation();
            await renderer.FailureCompleted.WaitAsync(timeout.Token);
            client.Close();

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => run.WaitAsync(timeout.Token));
            Assert.Contains(
                "Unexpected preparation failure.",
                exception.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task DiscoveryFailureBeforeDisconnectIsReported()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var discovery = new ControllableFailingTargetDiscovery();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            discovery,
            new RecordingOverlayRenderer(),
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await discovery.Started.WaitAsync(timeout.Token);
            discovery.Fail();
            await discovery.FailureCompleted.WaitAsync(timeout.Token);
            client.Close();

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => run.WaitAsync(timeout.Token));
            Assert.Contains(
                "Unexpected discovery failure.",
                exception.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CanceledPreparationBeforeDisconnectIsReported()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new PreCanceledPreparationRenderer();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer,
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await renderer.Started.WaitAsync(timeout.Token);
            client.Close();

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => run.WaitAsync(timeout.Token));
            Assert.IsNotAssignableFrom<OperationCanceledException>(exception);
            Assert.Contains(
                "A Desknav component canceled unexpectedly.",
                exception.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CanceledActivationBeforeDisconnectIsReported()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new PreCanceledActivationRenderer();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer,
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await renderer.Started.WaitAsync(timeout.Token);
            client.Close();

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => run.WaitAsync(timeout.Token));
            Assert.IsNotAssignableFrom<OperationCanceledException>(exception);
            Assert.Contains(
                "A Desknav component canceled unexpectedly.",
                exception.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CanceledDiscoveryBeforeDisconnectIsReported()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var discovery = new PreCanceledTargetDiscovery();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            discovery,
            new RecordingOverlayRenderer(),
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await discovery.Started.WaitAsync(timeout.Token);
            client.Close();

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => run.WaitAsync(timeout.Token));
            Assert.IsNotAssignableFrom<OperationCanceledException>(exception);
            Assert.Contains(
                "A Desknav component canceled unexpectedly.",
                exception.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HostWaitsForOverlayCleanup()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new BlockingCleanupRenderer();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer,
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await renderer.Activated.WaitAsync(timeout.Token);
            client.Close();
            await renderer.DisposalStarted.WaitAsync(timeout.Token);

            Assert.False(run.IsCompleted);

            renderer.FinishDisposal();
            await run.WaitAsync(timeout.Token);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task UnexpectedCleanupCancellationIsReportedAsFailure()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new CancelingCleanupRenderer();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer,
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await renderer.Activated.WaitAsync(timeout.Token);
            client.Close();

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => run.WaitAsync(timeout.Token));
            Assert.IsNotAssignableFrom<OperationCanceledException>(exception);
            var failures = exception is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions
                : [exception];
            Assert.All(
                failures,
                failure =>
                {
                    Assert.Equal(
                        "A Desknav component canceled unexpectedly.",
                        failure.Message);
                    Assert.IsAssignableFrom<OperationCanceledException>(
                        failure.InnerException);
                });
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HostWaitsForDiscoveryCancellation()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var discovery = new BlockingTargetDiscovery();
        var host = new DesknavHost(
            (IPEndPoint)listener.LocalEndpoint,
            discovery,
            new RecordingOverlayRenderer(),
            TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);

        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            await WriteTargetCommandAsync(writer);
            await discovery.Started.WaitAsync(timeout.Token);
            client.Close();
            await discovery.CancellationRequested.WaitAsync(timeout.Token);

            Assert.False(run.IsCompleted);

            discovery.FinishCancellation();
            await run.WaitAsync(timeout.Token);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteTargetCommandAsync(StreamWriter writer)
    {
        await writer.WriteLineAsync(
            """{"LayerChange":{"new":"command"}}""");
        await writer.WriteLineAsync(
            """{"MessagePush":{"message":["gesture","command","spc"]}}""");
        await writer.WriteLineAsync(
            """{"LayerChange":{"new":"pointer"}}""");
        await writer.WriteLineAsync(
            """{"MessagePush":{"message":["gesture","pointer","f"]}}""");
        await writer.WriteLineAsync(
            """{"LayerChange":{"new":"command"}}""");
    }

    private static DesktopTarget[] Targets() =>
        [
            new(
                TargetId.New(),
                new PhysicalRect(10, 20, 31, 41)),
        ];

    private sealed class FakeTargetDiscovery(
        IReadOnlyCollection<DesktopTarget> targets)
        : ITargetDiscovery
    {
        public Task<TargetDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<TargetDiscoveryResult>(
                new TargetDiscoveryResult.Succeeded([.. targets]));
    }

    private sealed class RecordingOverlayRenderer : IOverlayRenderer
    {
        private readonly Channel<TargetPresentation> _activations =
            Channel.CreateUnbounded<TargetPresentation>();

        public Task<IPreparedScene> PrepareAsync(
            TargetPresentation presentation,
            CancellationToken cancellationToken) =>
            Task.FromResult<IPreparedScene>(
                new PreparedScene(presentation));

        public Task ActivateAsync(IPreparedScene scene)
        {
            var prepared = Assert.IsType<PreparedScene>(scene);
            Assert.True(_activations.Writer.TryWrite(prepared.Presentation));
            return Task.CompletedTask;
        }

        public ValueTask<TargetPresentation> ReadActivationAsync(
            CancellationToken cancellationToken) =>
            _activations.Reader.ReadAsync(cancellationToken);

        public bool TryReadActivation(
            out TargetPresentation? presentation) =>
            _activations.Reader.TryRead(out presentation);
    }

    private sealed class PreparedScene(TargetPresentation presentation)
        : IPreparedScene
    {
        public TargetPresentation Presentation { get; } = presentation;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControllableFailingOverlayRenderer(
        bool failSceneDisposal = false)
        : IOverlayRenderer
    {
        private readonly TaskCompletionSource<bool> _activationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _failActivation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ActivationStarted => _activationStarted.Task;

        public Task<IPreparedScene> PrepareAsync(
            TargetPresentation presentation,
            CancellationToken cancellationToken) =>
            Task.FromResult<IPreparedScene>(
                failSceneDisposal
                    ? new FailingPreparedScene()
                    : new PreparedScene(presentation));

        public async Task ActivateAsync(IPreparedScene scene)
        {
            _activationStarted.TrySetResult(true);
            await _failActivation.Task;
            throw new InvalidOperationException(
                "Unexpected overlay failure.");
        }

        public void FailActivation() =>
            _failActivation.TrySetResult(true);
    }

    private sealed class FailingPreparedScene : IPreparedScene
    {
        public ValueTask DisposeAsync() =>
            ValueTask.FromException(
                new InvalidOperationException(
                    "Unexpected scene disposal failure."));
    }

    private sealed class ControllableFailingPreparationRenderer
        : IOverlayRenderer
    {
        private readonly TaskCompletionSource<bool> _preparationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _failPreparation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _failureCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PreparationStarted => _preparationStarted.Task;

        public Task FailureCompleted => _failureCompleted.Task;

        public Task<IPreparedScene> PrepareAsync(
            TargetPresentation presentation,
            CancellationToken cancellationToken)
        {
            var execution = FailPreparationAsync();
            _ = execution.ContinueWith(
                _ => _failureCompleted.TrySetResult(true),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return execution;
        }

        public Task ActivateAsync(IPreparedScene scene) =>
            throw new NotSupportedException();

        public void FailPreparation() =>
            _failPreparation.TrySetResult(true);

        private async Task<IPreparedScene> FailPreparationAsync()
        {
            _preparationStarted.TrySetResult(true);
            await _failPreparation.Task;
            throw new InvalidOperationException(
                "Unexpected preparation failure.");
        }
    }

    private sealed class PreCanceledPreparationRenderer : IOverlayRenderer
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<IPreparedScene> PrepareAsync(
            TargetPresentation presentation,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult(true);
            return Task.FromCanceled<IPreparedScene>(
                new CancellationToken(canceled: true));
        }

        public Task ActivateAsync(IPreparedScene scene) =>
            throw new NotSupportedException();
    }

    private sealed class PreCanceledActivationRenderer : IOverlayRenderer
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<IPreparedScene> PrepareAsync(
            TargetPresentation presentation,
            CancellationToken cancellationToken) =>
            Task.FromResult<IPreparedScene>(
                new PreparedScene(presentation));

        public Task ActivateAsync(IPreparedScene scene)
        {
            _started.TrySetResult(true);
            return Task.FromCanceled(
                new CancellationToken(canceled: true));
        }
    }

    private sealed class BlockingCleanupRenderer : IOverlayRenderer
    {
        private readonly TaskCompletionSource<bool> _activated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _disposalStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _finishDisposal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Activated => _activated.Task;

        public Task DisposalStarted => _disposalStarted.Task;

        public Task<IPreparedScene> PrepareAsync(
            TargetPresentation presentation,
            CancellationToken cancellationToken) =>
            Task.FromResult<IPreparedScene>(
                new BlockingPreparedScene(
                    _disposalStarted,
                    _finishDisposal));

        public Task ActivateAsync(IPreparedScene scene)
        {
            _activated.TrySetResult(true);
            return Task.CompletedTask;
        }

        public void FinishDisposal() =>
            _finishDisposal.TrySetResult(true);
    }

    private sealed class CancelingCleanupRenderer : IOverlayRenderer
    {
        private readonly TaskCompletionSource<bool> _activated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Activated => _activated.Task;

        public Task<IPreparedScene> PrepareAsync(
            TargetPresentation presentation,
            CancellationToken cancellationToken) =>
            Task.FromResult<IPreparedScene>(
                new CancelingPreparedScene());

        public Task ActivateAsync(IPreparedScene scene)
        {
            _activated.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class CancelingPreparedScene : IPreparedScene
    {
        public ValueTask DisposeAsync() =>
            ValueTask.FromCanceled(
                new CancellationToken(canceled: true));
    }

    private sealed class BlockingPreparedScene(
        TaskCompletionSource<bool> disposalStarted,
        TaskCompletionSource<bool> finishDisposal)
        : IPreparedScene
    {
        public async ValueTask DisposeAsync()
        {
            disposalStarted.TrySetResult(true);
            await finishDisposal.Task;
        }
    }

    private sealed class BlockingTargetDiscovery : ITargetDiscovery
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _finishCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task CancellationRequested =>
            _cancellationRequested.Task;

        public async Task<TargetDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            _started.TrySetResult(true);
            using var registration = cancellationToken.Register(
                () => _cancellationRequested.TrySetResult(true));
            await _finishCancellation.Task;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "Discovery completed without cancellation.");
        }

        public void FinishCancellation() =>
            _finishCancellation.TrySetResult(true);
    }

    private sealed class ControllableFailingTargetDiscovery : ITargetDiscovery
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _fail =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _failureCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task FailureCompleted => _failureCompleted.Task;

        public Task<TargetDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            var execution = FailAsync();
            _ = execution.ContinueWith(
                _ => _failureCompleted.TrySetResult(true),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return execution;
        }

        public void Fail() => _fail.TrySetResult(true);

        private async Task<TargetDiscoveryResult> FailAsync()
        {
            _started.TrySetResult(true);
            await _fail.Task;
            throw new InvalidOperationException(
                "Unexpected discovery failure.");
        }
    }

    private sealed class PreCanceledTargetDiscovery : ITargetDiscovery
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task<TargetDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            _started.TrySetResult(true);
            return Task.FromCanceled<TargetDiscoveryResult>(
                new CancellationToken(canceled: true));
        }
    }
}
