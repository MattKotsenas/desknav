using System.Windows.Threading;

namespace Desknav.UI.Wpf.Tests;

internal sealed class WpfDispatcherThread : IAsyncDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _started = new();
    private readonly Thread _thread;

    public WpfDispatcherThread()
    {
        _thread = new Thread(
            () =>
            {
                _started.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            })
        {
            IsBackground = true,
            Name = "Desknav WPF test dispatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Dispatcher Dispatcher => _started.Task.GetAwaiter().GetResult();

    public async Task<T> InvokeAsync<T>(Func<T> action) =>
        await Dispatcher.InvokeAsync(action).Task;

    public async ValueTask DisposeAsync()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            await Dispatcher.InvokeAsync(
                () => Dispatcher.BeginInvokeShutdown(
                    DispatcherPriority.Normal));
        }

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException(
                "The WPF test dispatcher did not stop.");
        }
    }
}