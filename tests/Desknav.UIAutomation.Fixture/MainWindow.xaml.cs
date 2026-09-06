using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Desknav.UIAutomation.Fixture;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ContentRendered += HandleContentRendered;
    }

    private async void HandleContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= HandleContentRendered;
        await Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ContextIdle);

        var windowHandle =
            new WindowInteropHelper(this).Handle.ToInt64();
        await Console.Out.WriteLineAsync(
            $"{windowHandle} {Environment.ProcessId}");
        await Console.Out.FlushAsync();

        _ = Task.Run(ReadCommands);
    }

    private void ReadCommands()
    {
        while (Console.In.ReadLine() is { } command)
        {
            switch (command)
            {
                case "minimize":
                    Dispatcher.Invoke(
                        () => WindowState = WindowState.Minimized);
                    SignalReady("minimized");
                    break;
                case "move-offscreen":
                    Dispatcher.Invoke(
                        () =>
                        {
                            WindowState = WindowState.Normal;
                            Left = -100_000;
                            Top = -100_000;
                        });
                    SignalReady("moved-offscreen");
                    break;
                default:
                    Dispatcher.Invoke(Close);
                    return;
            }
        }

        Dispatcher.Invoke(Close);
    }

    private void SignalReady(string state)
    {
        Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle)
            .Task
            .GetAwaiter()
            .GetResult();
        Console.Out.WriteLine(state);
        Console.Out.Flush();
    }
}