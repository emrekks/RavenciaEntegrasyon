using System.Collections.Concurrent;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Jobs;

// Worker lanes step 1 (19 Nov 2026), quota-aware leasing. A job waiting for a marketplace quota token used to wait inside its
// worker slot (SharedRateLimits.WaitAsync), so a full quota of one marketplace kept other marketplaces' jobs of the same lane
// queued behind it. This registry knows, in the worker process, which quota buckets are full right now and which jobs use
// them:
//   - a bucket is full while a request has been waiting for its token for at least the saturation time (a short wait is the
//     ordinary spacing between requests, not a full quota);
//   - the buckets a job takes tokens from are learned from its last run (JobQuotaScope), per connection and job type;
//   - Held lists the connection + job type pairs whose learned buckets are full; the lease skips their queued jobs
//     (JobQuotaHold), user actions never and any job at most the maximum hold after it became runnable;
//   - Released fires when the last waiter of a full bucket got its token or gave up, so the lanes poll at once and the
//     held jobs are leased without delay.
// The running job that found the quota full keeps its slot and its place (nothing is cut off mid-run), the quota itself is
// still counted only by the shared limiter, so no request goes out faster than before. Process-wide (one worker process);
// the API's instant actions take tokens too but never register here, so they are never held.
public sealed class QuotaPressure
{
    public static QuotaPressure Shared { get; } = new();

    private readonly object gate = new();
    private readonly Dictionary<string, List<Waiter>> waiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid ConnectionId, string JobType), string[]> learned = new();
    private const int MaximumLearnedPairs = 10_000;
    private const int MaximumKeysPerPair = 16;

    // Raised (outside the lock) when a bucket that was full has no waiter left.
    public event Action? Released;

    // Minimum wait that marks a bucket full (Worker:QuotaAwareLeasing:SaturationSeconds).
    public TimeSpan Saturation { get; set; } = DefaultSaturation;
    public static readonly TimeSpan DefaultSaturation = TimeSpan.FromSeconds(10);

    public Waiter BeginWait(string key, DateTimeOffset now)
    {
        var waiter = new Waiter(this, key, now);
        lock (gate)
        {
            if (!waiters.TryGetValue(key, out var list)) waiters[key] = list = [];
            list.Add(waiter);
        }
        return waiter;
    }

    internal void EndWait(Waiter waiter, DateTimeOffset now)
    {
        var released = false;
        lock (gate)
        {
            if (!waiters.TryGetValue(waiter.Key, out var list) || !list.Remove(waiter)) return;
            if (list.Count == 0)
            {
                waiters.Remove(waiter.Key);
                // The bucket was full while this (its last) waiter waited: held jobs may go now.
                released = now - waiter.Since >= Saturation;
            }
        }
        if (released) Released?.Invoke();
    }

    public bool IsFull(string key, DateTimeOffset now)
    {
        lock (gate)
            return waiters.TryGetValue(key, out var list) && list.Exists(waiter => now - waiter.Since >= Saturation);
    }

    // The buckets a connection's job type took tokens from in its last run; an empty run (no marketplace request) leaves the
    // last known set in place.
    public void Learn(Guid connectionId, string jobType, IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) return;
        if (learned.Count >= MaximumLearnedPairs && !learned.ContainsKey((connectionId, jobType))) return;
        learned[(connectionId, jobType)] = [.. keys.Take(MaximumKeysPerPair)];
    }

    // The connection + job type pairs that use a bucket which is full now.
    public JobQuotaHold Held(DateTimeOffset now, TimeSpan maximumHold)
    {
        string[] full;
        lock (gate)
            full = [.. waiters.Where(entry => entry.Value.Exists(waiter => now - waiter.Since >= Saturation)).Select(entry => entry.Key)];
        if (full.Length == 0) return JobQuotaHold.None;
        var connections = new List<Guid>();
        var jobTypes = new List<string>();
        foreach (var (pair, keys) in learned)
        {
            if (!keys.Any(key => Array.IndexOf(full, key) >= 0)) continue;
            connections.Add(pair.ConnectionId);
            jobTypes.Add(pair.JobType);
        }
        return connections.Count == 0 ? JobQuotaHold.None : new JobQuotaHold(connections, jobTypes, maximumHold);
    }

    // One request waiting for a token of one bucket; disposing it ends the wait.
    public sealed class Waiter : IDisposable
    {
        private readonly QuotaPressure owner;
        private int ended;

        internal Waiter(QuotaPressure owner, string key, DateTimeOffset since)
        {
            this.owner = owner;
            Key = key;
            Since = since;
        }

        public string Key { get; }
        public DateTimeOffset Since { get; }

        public void End(DateTimeOffset now)
        {
            if (Interlocked.Exchange(ref ended, 1) == 0) owner.EndWait(this, now);
        }

        public void Dispose() => End(DateTimeOffset.UtcNow);
    }
}

// The quota buckets one job run takes tokens from (QuotaPressure.Learn). The worker enters it around a job; the shared rate
// limiter's wait records each bucket it is asked for while it is active. Outside a job (the API) nothing is recorded.
public static class JobQuotaScope
{
    private static readonly AsyncLocal<Scope?> current = new();

    internal static Scope? Current => current.Value;

    public static Scope Enter()
    {
        var scope = new Scope(current.Value);
        current.Value = scope;
        return scope;
    }

    public sealed class Scope : IDisposable
    {
        private readonly Scope? previous;
        private readonly HashSet<string> keys = new(StringComparer.Ordinal);

        internal Scope(Scope? previous) => this.previous = previous;

        public IReadOnlyCollection<string> Keys
        {
            get { lock (keys) return [.. keys]; }
        }

        internal void Record(string key)
        {
            lock (keys) keys.Add(key);
        }

        public void Dispose() => current.Value = previous;
    }
}
