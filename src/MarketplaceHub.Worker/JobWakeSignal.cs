namespace MarketplaceHub.Worker;

// Wakes every idle lease loop at once. A loop takes Current before it polls the queue, so a
// pulse that arrives between the poll and the wait is not lost.
public sealed class JobWakeSignal
{
    private TaskCompletionSource current = NewSource();

    public Task Current => Volatile.Read(ref current).Task;

    public void Pulse() => Interlocked.Exchange(ref current, NewSource()).TrySetResult();

    // Returns true when woken by a pulse, false when the delay elapsed first.
    public static async Task<bool> WaitAsync(Task wake, TimeSpan delay, CancellationToken cancellationToken)
    {
        var timer = Task.Delay(delay, cancellationToken);
        var completed = await Task.WhenAny(wake, timer);
        if (completed == timer) await timer;
        return completed == wake;
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
