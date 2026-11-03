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

// How long an idle lease loop waits before it polls the queue again. A notification ends the wait for every job a
// row change makes runnable, and the due-time wake (Worker.RunDueJobWakeAsync) pulses when a job becomes runnable
// only because time passed (its AvailableAt, the end of a connection's quota backoff), so the poll is the fallback.
public static class JobIdleWait
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Maximum = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MinimumDueWait = TimeSpan.FromMilliseconds(50);

    // A wake starts again from the first delay; each empty poll without one doubles it, up to the maximum.
    public static TimeSpan Next(TimeSpan current, bool woken) =>
        woken ? First : TimeSpan.FromTicks(Math.Min(Maximum.Ticks, Math.Max(First.Ticks, current.Ticks * 2)));

    // How long the due-time wake sleeps: until the next moment a waiting job can become runnable, at most the
    // maximum (a job queued meanwhile with a later AvailableAt is found by the next read).
    public static TimeSpan UntilDue(DateTimeOffset? nextDue, DateTimeOffset now)
    {
        if (nextDue is not { } due) return Maximum;
        var wait = due - now;
        return wait < MinimumDueWait ? MinimumDueWait : wait > Maximum ? Maximum : wait;
    }
}
