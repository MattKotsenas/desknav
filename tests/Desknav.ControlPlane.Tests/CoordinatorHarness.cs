using System.Globalization;

using Akka.Actor;
using Akka.TestKit;

namespace Desknav.ControlPlane.Tests;

internal sealed class CoordinatorHarness : IAsyncDisposable
{
    private readonly XunitTestKitAssertions _assertions = new();

    private CoordinatorHarness(ActorSystem actorSystem)
    {
        ActorSystem = actorSystem;
        PointerUi = CreateProbe("pointer-ui");
        Kanata = CreateProbe("kanata");
        Coordinator = actorSystem.ActorOf(
            PointAtTargetCoordinator.Props(PointerUi.Ref, Kanata.Ref),
            "point-at-target");
    }

    public ActorSystem ActorSystem { get; }

    public IActorRef Coordinator { get; }

    public TestProbe Kanata { get; }

    public TestProbe PointerUi { get; }

    public static CoordinatorHarness Create()
    {
        var actorSystem = ActorSystem.Create(
            $"desknav-tests-{Guid.NewGuid():N}");
        return new CoordinatorHarness(actorSystem);
    }

    public TestProbe CreateProbe(string name) =>
        new(ActorSystem, _assertions, name);

    public async ValueTask DisposeAsync()
    {
        await ActorSystem.Terminate();
    }

    private sealed class XunitTestKitAssertions : ITestKitAssertions
    {
        public void Fail(string format, params object[] args) =>
            Assert.Fail(Format(format, args));

        public void AssertTrue(
            bool condition,
            string format,
            params object[] args) =>
            Assert.True(condition, Format(format, args));

        public void AssertFalse(
            bool condition,
            string format,
            params object[] args) =>
            Assert.False(condition, Format(format, args));

        public void AssertEqual<T>(
            T expected,
            T actual,
            string format,
            params object[] args) =>
            Assert.Equal(expected, actual);

        public void AssertEqual<T>(
            T expected,
            T actual,
            Func<T, T, bool> comparer,
            string format,
            params object[] args) =>
            Assert.True(comparer(expected, actual), Format(format, args));

        public Exception AssertThrows(Action action)
        {
            var exception = Record.Exception(action);
            Assert.NotNull(exception);
            return exception;
        }

        public TException AssertThrows<TException>(Action action)
            where TException : Exception =>
            Assert.Throws<TException>(action);

        public async Task<Exception> AssertThrowsAsync(Func<Task> action)
        {
            var exception = await Record.ExceptionAsync(action);
            Assert.NotNull(exception);
            return exception;
        }

        public Task<TException> AssertThrowsAsync<TException>(
            Func<Task> action)
            where TException : Exception =>
            Assert.ThrowsAsync<TException>(action);

        private static string Format(string format, object[] args) =>
            args.Length is 0
                ? format
                : string.Format(CultureInfo.InvariantCulture, format, args);
    }
}