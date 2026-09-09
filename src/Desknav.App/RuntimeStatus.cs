namespace Desknav.App;

internal sealed class RuntimeStatus
{
    private Exception? _failure;

    public Exception? Failure => Volatile.Read(ref _failure);

    public void RecordFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Interlocked.CompareExchange(ref _failure, failure, null);
    }
}
