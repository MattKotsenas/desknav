namespace Desknav.App;

/// <summary>
/// Carries the first unrecoverable runtime failure to the composition root so
/// the process can return a failure exit code. Each host owns one instance.
/// </summary>
internal sealed class RuntimeOutcome
{
    private Exception? _failure;

    public Exception? Failure => Volatile.Read(ref _failure);

    public void RecordFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Interlocked.CompareExchange(ref _failure, failure, null);
    }
}
