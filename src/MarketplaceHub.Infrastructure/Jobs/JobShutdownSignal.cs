namespace MarketplaceHub.Infrastructure.Jobs;

// KUYRUK-1 (16 Nov 2026): a job that writes to a marketplace is not cancelled by a worker shutdown (Worker.FinishOnShutdown),
// so a request in flight is never cut off. But such a job may still be queueing for its marketplace quota (Trendyol waits
// up to ten minutes, the other quota-paced clients five), long past the host's 40 s shutdown timeout: the process was
// then killed, the job's lease expired (LEASE_EXPIRED, an attempt lost) and its effect record stayed open although nothing
// had been sent. The Worker enters this signal with its stopping token around such a job; a quota wait observes it
// (QuotaWaitShutdown) and gives up while no quota token is held, so the request ends as a local quota wait (never sent:
// the effect record is released, the attempt kept) and the Worker hands the job back to the queue. Once the quota token
// is taken the request goes out and finishes exactly as before; the shutdown no longer touches it.
public static class JobShutdownSignal
{
    private static readonly AsyncLocal<Scope?> current = new();

    internal static Scope? Current => current.Value;

    public static Scope Enter(CancellationToken shutdown)
    {
        var scope = new Scope(shutdown, current.Value);
        current.Value = scope;
        return scope;
    }

    public sealed class Scope : IDisposable
    {
        private readonly Scope? previous;
        private int quotaWaitsStopped;

        internal Scope(CancellationToken token, Scope? previous)
        {
            Token = token;
            this.previous = previous;
        }

        internal CancellationToken Token { get; }

        // How many requests of the job the shutdown stopped before their quota token (none of them was sent).
        public int QuotaWaitsStopped => Volatile.Read(ref quotaWaitsStopped);

        internal void RecordStop() => Interlocked.Increment(ref quotaWaitsStopped);

        public void Dispose() => current.Value = previous;
    }
}

// One request's quota wait under the job's shutdown signal. The shutdown cancels the request's own timeout source only
// while no quota token is held; QuotaAcquired (called from the rate-limit handler's quota callback, right before the send)
// closes that window under the same lock, so a request that got its token is never cancelled by the shutdown. A shutdown
// that came before the request started (StoppedBeforeStart) lets the caller answer a local quota wait without sending.
// Without a signal (the API, jobs that do not write) nothing changes.
internal sealed class QuotaWaitShutdown : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource source;
    private readonly JobShutdownSignal.Scope? scope;
    private readonly CancellationTokenRegistration registration;
    private bool quotaAcquired;
    private bool stopped;

    public QuotaWaitShutdown(CancellationTokenSource source)
    {
        this.source = source;
        scope = JobShutdownSignal.Current;
        if (scope is null || !scope.Token.CanBeCanceled) return;
        if (scope.Token.IsCancellationRequested)
        {
            StoppedBeforeStart = true;
            Stop();
            return;
        }
        registration = scope.Token.Register(Stop);
    }

    public bool StoppedBeforeStart { get; }

    public void QuotaAcquired()
    {
        lock (gate) quotaAcquired = true;
    }

    private void Stop()
    {
        lock (gate)
        {
            if (quotaAcquired || stopped) return;
            stopped = true;
            scope!.RecordStop();
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }
    }

    public void Dispose() => registration.Dispose();
}
