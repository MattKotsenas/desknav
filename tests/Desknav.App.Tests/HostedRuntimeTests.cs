using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;

using Akka.Actor;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Desknav.ControlPlane;
using Desknav.UI;

namespace Desknav.App.Tests;

public sealed class HostedRuntimeTests
{
    [Fact]
    public async Task MissingEndpointReturnsUsageExitCode()
    {
        var exitCode = await App.RunAsync(
            [],
            Dispatcher.CurrentDispatcher);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task NonLoopbackEndpointReturnsUsageExitCode()
    {
        var exitCode = await App.RunAsync(
            ["--kanata-endpoint", "192.0.2.1:1234"],
            Dispatcher.CurrentDispatcher);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task CleanEofReturnsSuccessfulApplicationExitCode()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            new RecordingOverlayRenderer());

        try
        {
            var run = App.RunHostAsync(host);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);

            client.Close();

            Assert.Equal(0, await run.WaitAsync(timeout.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TargetCommandPresentsTargetsAndEscapeHidesThem()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new RecordingOverlayRenderer();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer);

        try
        {
            await host.StartAsync(timeout.Token);
            var shutdown = host.WaitForShutdownAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);

            Assert.False(shutdown.IsCompleted);
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
            await shutdown;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FatalOverlayFailureStopsTheHost()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new ControllableFailingOverlayRenderer();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer);

        try
        {
            await host.StartAsync(timeout.Token);
            var shutdown = host.WaitForShutdownAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);

            await WriteTargetCommandAsync(writer);
            await renderer.ActivationStarted.WaitAsync(timeout.Token);
            renderer.FailActivation();

            await shutdown;

            var outcome = host.Services.GetRequiredService<RuntimeOutcome>();
            Assert.Equal(
                "Unexpected overlay failure.",
                Assert.IsType<InvalidOperationException>(
                    outcome.Failure).Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task FatalDiscoveryFailureStopsTheHost()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var discovery = new ControllableFailingTargetDiscovery();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            discovery,
            new RecordingOverlayRenderer());

        try
        {
            await host.StartAsync(timeout.Token);
            var shutdown = host.WaitForShutdownAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);

            await WriteTargetCommandAsync(writer);
            await discovery.Started.WaitAsync(timeout.Token);
            discovery.Fail();

            await shutdown;

            var outcome = host.Services.GetRequiredService<RuntimeOutcome>();
            Assert.Equal(
                "Unexpected discovery failure.",
                Assert.IsType<InvalidOperationException>(
                    outcome.Failure).Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ForcedOverlayStopAttemptsSceneCleanup()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new BlockingCleanupRenderer();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer);

        try
        {
            await host.StartAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);
            await WriteTargetCommandAsync(writer);
            await renderer.Activated.WaitAsync(timeout.Token);

            var system = host.Services.GetRequiredService<ActorSystem>();
            system.ActorSelection("/user/runtime/overlay").Tell(Kill.Instance);

            await renderer.DisposalStarted.WaitAsync(timeout.Token);
            renderer.FinishDisposal();
            await host.WaitForShutdownAsync(timeout.Token);
        }
        finally
        {
            renderer.FinishDisposal();
            listener.Stop();
        }
    }

    [Fact]
    public async Task ForcedDiscoveryStopAttemptsOperationCancellation()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var discovery = new BlockingTargetDiscovery();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            discovery,
            new RecordingOverlayRenderer());

        try
        {
            await host.StartAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);
            await WriteTargetCommandAsync(writer);
            await discovery.Started.WaitAsync(timeout.Token);

            var system = host.Services.GetRequiredService<ActorSystem>();
            system
                .ActorSelection(
                    "/user/runtime/coordinator/target-discovery")
                .Tell(Kill.Instance);

            await discovery.CancellationRequested.WaitAsync(timeout.Token);
            discovery.FinishCancellation();
            await host.WaitForShutdownAsync(timeout.Token);
        }
        finally
        {
            discovery.FinishCancellation();
            listener.Stop();
        }
    }

    [Fact]
    public async Task HostWaitsForOverlayCleanupWithinBudget()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new BlockingCleanupRenderer();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer);

        try
        {
            await host.StartAsync(timeout.Token);
            var shutdown = host.WaitForShutdownAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);

            await WriteTargetCommandAsync(writer);
            await renderer.Activated.WaitAsync(timeout.Token);
            client.Close();
            await renderer.DisposalStarted.WaitAsync(timeout.Token);

            Assert.False(shutdown.IsCompleted);
            renderer.FinishDisposal();
            await shutdown;
            Assert.Null(
                host.Services.GetRequiredService<RuntimeOutcome>().Failure);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HostStopsWhenOverlayCleanupExceedsBudget()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new BlockingCleanupRenderer();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer);

        try
        {
            await host.StartAsync(timeout.Token);
            var shutdown = host.WaitForShutdownAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);

            await WriteTargetCommandAsync(writer);
            await renderer.Activated.WaitAsync(timeout.Token);
            client.Close();
            await renderer.DisposalStarted.WaitAsync(timeout.Token);

            await shutdown;

            Assert.IsType<TimeoutException>(
                host.Services
                    .GetRequiredService<RuntimeOutcome>()
                    .Failure);
        }
        finally
        {
            renderer.FinishDisposal();
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
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            discovery,
            new RecordingOverlayRenderer());

        try
        {
            await host.StartAsync(timeout.Token);
            var shutdown = host.WaitForShutdownAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);

            await WriteTargetCommandAsync(writer);
            await discovery.Started.WaitAsync(timeout.Token);
            client.Close();
            await discovery.CancellationRequested.WaitAsync(timeout.Token);

            Assert.False(shutdown.IsCompleted);

            discovery.FinishCancellation();
            await shutdown;
            Assert.Null(
                host.Services.GetRequiredService<RuntimeOutcome>().Failure);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HostWaitsForActivationBeforeOverlayCleanup()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var renderer = new BlockingActivationRenderer();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            renderer);

        try
        {
            await host.StartAsync(timeout.Token);
            var shutdown = host.WaitForShutdownAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);

            await WriteTargetCommandAsync(writer);
            await renderer.ActivationStarted.WaitAsync(timeout.Token);
            client.Close();

            Assert.False(shutdown.IsCompleted);

            renderer.FinishActivation();
            await renderer.DisposalStarted.WaitAsync(timeout.Token);
            await shutdown;
            Assert.Null(
                host.Services.GetRequiredService<RuntimeOutcome>().Failure);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task UnrelatedIngressCancellationIsFatal()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var parser = new BlockingCancelingFrameParser();
        using var host = CreateHost(
            (IPEndPoint)listener.LocalEndpoint,
            new FakeTargetDiscovery(Targets()),
            new RecordingOverlayRenderer(),
            parser);

        try
        {
            await host.StartAsync(timeout.Token);
            using var client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            await using var writer = CreateWriter(client);
            await writer.WriteLineAsync("{}");
            await parser.Started.WaitAsync(timeout.Token);

            var stop = host.StopAsync(timeout.Token);
            parser.Fail();
            var stopFailure = await Record.ExceptionAsync(() => stop);
            Assert.True(
                stopFailure is null or OperationCanceledException,
                stopFailure?.ToString());

            Assert.IsType<OperationCanceledException>(
                host.Services
                    .GetRequiredService<RuntimeOutcome>()
                    .Failure);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static IHost CreateHost(
        IPEndPoint endpoint,
        ITargetDiscovery discovery,
        IOverlayRenderer renderer,
        IKanataFrameParser? parser = null) =>
        DesknavRuntime.CreateHostBuilder(
                ["--kanata-endpoint", endpoint.ToString()],
                Dispatcher.CurrentDispatcher)
            .ConfigureServices(
                (_, services) =>
                {
                    services.RemoveAll<ITargetDiscovery>();
                    services.RemoveAll<IOverlayRenderer>();
                    services.AddSingleton(discovery);
                    services.AddSingleton(renderer);
                    if (parser is not null)
                    {
                        services.RemoveAll<IKanataFrameParser>();
                        services.AddSingleton(parser);
                    }
                })
            .Build();

    private static StreamWriter CreateWriter(TcpClient client) =>
        new(
            client.GetStream(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };

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

    private sealed class ControllableFailingOverlayRenderer
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
                new PreparedScene(presentation));

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

    private sealed class BlockingActivationRenderer : IOverlayRenderer
    {
        private readonly TaskCompletionSource<bool> _activationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _finishActivation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _disposalStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ActivationStarted => _activationStarted.Task;

        public Task DisposalStarted => _disposalStarted.Task;

        public Task<IPreparedScene> PrepareAsync(
            TargetPresentation presentation,
            CancellationToken cancellationToken) =>
            Task.FromResult<IPreparedScene>(
                new SignalingPreparedScene(_disposalStarted));

        public async Task ActivateAsync(IPreparedScene scene)
        {
            _activationStarted.TrySetResult(true);
            await _finishActivation.Task;
        }

        public void FinishActivation() =>
            _finishActivation.TrySetResult(true);
    }

    private sealed class SignalingPreparedScene(
        TaskCompletionSource<bool> disposalStarted)
        : IPreparedScene
    {
        public ValueTask DisposeAsync()
        {
            disposalStarted.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingCancelingFrameParser : IKanataFrameParser
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _fail =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public KanataServerFrame Parse(string json)
        {
            _started.TrySetResult(true);
            _fail.Task.GetAwaiter().GetResult();
            throw new OperationCanceledException(
                new CancellationToken(canceled: true));
        }

        public void Fail() => _fail.TrySetResult(true);
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

    private sealed class ControllableFailingTargetDiscovery
        : ITargetDiscovery
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _fail =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task<TargetDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            _started.TrySetResult(true);
            await _fail.Task;
            throw new InvalidOperationException(
                "Unexpected discovery failure.");
        }

        public void Fail() => _fail.TrySetResult(true);
    }
}
