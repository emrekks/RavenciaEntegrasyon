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
//     (JobQuotaHold), only background work (never user actions, new-order reads, webhooks or writes) and any job at
//     most the maximum hold after it became runnable;
//   - Released fires when the last waiter of a bucket that was full got its token or gave up, so the lanes poll at once
//     and the held jobs are leased without delay (a job past the maximum hold is taken at its lane's next poll);
//   - at most MaximumLearnedPairs pairs are kept; past it the least recently learned tenth is dropped (PairsTrimmed).
// The running job that found the quota full keeps its slot and its place (nothing is cut off mid-run), the quota itself is
// still counted only by the shared limiter, so no request goes out faster than before. Process-wide (one worker process);
// the API's instant actions take tokens too but never register here, so they are never held.
public sealed class QuotaPressure
{
    public static QuotaPressure Shared { get; } = new();

    private readonly object gate = new();
    private readonly Dictionary<string, Bucket> buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid ConnectionId, string JobType), Learned> learned = new();
    internal const int MaximumLearnedPairs = 10_000;
    private const int MaximumKeysPerPair = 16;
    private long learnSequence;

    // Raised (outside the lock) when a bucket that was full at some moment of its wait has no waiter left.
    public event Action? Released;
    // Raised with the number of pairs dropped when the learned pairs reached MaximumLearnedPairs.
    public event Action<int>? PairsTrimmed;

    // Minimum wait that marks a bucket full (Worker:QuotaAwareLeasing:SaturationSeconds).
    public TimeSpan Saturation { get; set; } = DefaultSaturation;
    public static readonly TimeSpan DefaultSaturation = TimeSpan.FromSeconds(10);

    public Waiter BeginWait(string key, DateTimeOffset now)
    {
        var waiter = new Waiter(this, key, now);
        lock (gate)
        {
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new Bucket();
            bucket.Waiters.Add(waiter);
        }
        return waiter;
    }

    internal void EndWait(Waiter waiter, DateTimeOffset now)
    {
        var released = false;
        lock (gate)
        {
            if (!buckets.TryGetValue(waiter.Key, out var bucket) || !bucket.Waiters.Remove(waiter)) return;
            // Any waiter of this busy spell that waited the saturation time made the bucket full (and may have held jobs).
            if (now - waiter.Since >= Saturation)
            {
                bucket.WasFull = true;
                RememberFull(waiter.Key, waiter.Since + Saturation, now);
            }
            if (bucket.Waiters.Count == 0)
            {
                buckets.Remove(waiter.Key);
                released = bucket.WasFull;
            }
        }
        if (released) Released?.Invoke();
    }

    public bool IsFull(string key, DateTimeOffset now)
    {
        lock (gate)
            return buckets.TryGetValue(key, out var bucket) && bucket.Waiters.Exists(waiter => now - waiter.Since >= Saturation);
    }

    // The buckets a connection's job type took tokens from in its last run; an empty run (no marketplace request) leaves the
    // last known set in place. Past MaximumLearnedPairs the least recently learned tenth is dropped.
    public void Learn(Guid connectionId, string jobType, IReadOnlyCollection<string> keys, DateTimeOffset? now = null)
    {
        if (keys.Count == 0) return;
        string[] kept = [.. keys.Take(MaximumKeysPerPair)];
        // Queue phase 0: when the pair's buckets last changed (the same buckets learned again keep their moment), so a wait
        // during which they changed is not attributed to the quota hold with buckets the hold did not use.
        var changedAt = learned.TryGetValue((connectionId, jobType), out var previous) && previous.Keys.Order(StringComparer.Ordinal).SequenceEqual(kept.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            ? previous.ChangedAt
            : now ?? DateTimeOffset.UtcNow;
        learned[(connectionId, jobType)] = new Learned(kept, Interlocked.Increment(ref learnSequence), changedAt);
        if (learned.Count <= MaximumLearnedPairs) return;
        var oldest = learned.OrderBy(entry => entry.Value.Sequence).Take(MaximumLearnedPairs / 10).Select(entry => entry.Key).ToList();
        var dropped = oldest.Count(pair => learned.TryRemove(pair, out _));
        if (dropped > 0) PairsTrimmed?.Invoke(dropped);
    }

    internal int LearnedPairs => learned.Count;

    // The connection + job type pairs that use a bucket which is full now; jobs of priority heldAbovePriority or lower are
    // never held (JobQuotaHold.HeldAbovePriority).
    public JobQuotaHold Held(DateTimeOffset now, TimeSpan maximumHold, int heldAbovePriority = JobQuotaHold.DefaultHeldAbovePriority)
    {
        string[] full;
        lock (gate)
            full = [.. buckets.Where(entry => entry.Value.Waiters.Exists(waiter => now - waiter.Since >= Saturation)).Select(entry => entry.Key)];
        if (full.Length == 0) return JobQuotaHold.None;
        var connections = new List<Guid>();
        var jobTypes = new List<string>();
        foreach (var (pair, value) in learned)
        {
            if (!value.Keys.Any(key => Array.IndexOf(full, key) >= 0)) continue;
            connections.Add(pair.ConnectionId);
            jobTypes.Add(pair.JobType);
        }
        return connections.Count == 0 ? JobQuotaHold.None : new JobQuotaHold(connections, jobTypes, maximumHold, heldAbovePriority);
    }

    // Queue phase 0: when each bucket was full (a waiter past the saturation time), for the last FullHistoryKept, so the worker
    // can tell how long a leased job was held for its quota. Measurement only: Held above does not read it.
    public static readonly TimeSpan FullHistoryKept = TimeSpan.FromHours(6);
    private const int MaximumFullSpellsPerKey = 512;
    private readonly Dictionary<string, List<(DateTimeOffset From, DateTimeOffset To)>> fullSpells = new(StringComparer.Ordinal);

    // Called under the lock.
    private void RememberFull(string key, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return;
        var keepAfter = to - FullHistoryKept;
        if (!fullSpells.TryGetValue(key, out var spells))
        {
            // At most MaximumFullSpellKeys buckets: past it the stale ones go, then the least recently full down to 80 %.
            if (fullSpells.Count >= MaximumFullSpellKeys)
            {
                foreach (var stale in fullSpells.Where(item => item.Value.Count == 0 || item.Value[^1].To < keepAfter).Select(item => item.Key).ToList()) fullSpells.Remove(stale);
                if (fullSpells.Count >= MaximumFullSpellKeys)
                    foreach (var oldest in fullSpells.OrderBy(item => item.Value[^1].To).Take(fullSpells.Count - (MaximumFullSpellKeys * 4 / 5)).Select(item => item.Key).ToList()) fullSpells.Remove(oldest);
            }
            fullSpells[key] = spells = [];
        }
        spells.Add((from, to));
        // Spells come in end order: the stale ones are a prefix; past the cap down to 80 % of it.
        var drop = 0;
        while (drop < spells.Count && spells[drop].To < keepAfter) drop++;
        if (spells.Count - drop > MaximumFullSpellsPerKey) drop = spells.Count - (MaximumFullSpellsPerKey * 4 / 5);
        if (drop > 0) spells.RemoveRange(0, drop);
    }

    private const int MaximumFullSpellKeys = 10_000;

    // The spells in which any of the keys was full, between from and to (a waiter still past the saturation time counts up to now).
    public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> FullSpells(IReadOnlyCollection<string> keys, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var result = new List<(DateTimeOffset From, DateTimeOffset To)>();
        if (keys.Count == 0 || to <= from) return result;
        lock (gate)
            foreach (var key in keys)
            {
                if (fullSpells.TryGetValue(key, out var spells))
                    result.AddRange(spells.Where(spell => spell.To > from && spell.From < to));
                if (buckets.TryGetValue(key, out var bucket))
                    foreach (var waiter in bucket.Waiters)
                        if (now - waiter.Since >= Saturation && waiter.Since + Saturation < to) result.Add((waiter.Since + Saturation, now));
            }
        return result;
    }

    // The buckets a connection's job type learned from its last run (empty: none learned).
    public IReadOnlyCollection<string> LearnedKeys(Guid connectionId, string jobType) =>
        learned.TryGetValue((connectionId, jobType), out var value) ? value.Keys : [];

    // The same, with the moment those buckets were first learned as they are (null: none learned).
    public (IReadOnlyCollection<string> Keys, DateTimeOffset? ChangedAt) LearnedKeysSince(Guid connectionId, string jobType) =>
        learned.TryGetValue((connectionId, jobType), out var value) ? (value.Keys, value.ChangedAt) : ([], null);

    private sealed class Bucket
    {
        public List<Waiter> Waiters { get; } = [];
        public bool WasFull { get; set; }
    }

    private sealed record Learned(string[] Keys, long Sequence, DateTimeOffset ChangedAt);

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
// Queue phase 0 (27 Nov 2026): the scope also names the running job (an order it stores records it, Order.CreatedByJobId)
// and adds up the run's rate-limit token waits (the shared limiter's and the adapters' in-process ones) for its attempt row.
public static class JobQuotaScope
{
    private static readonly AsyncLocal<Scope?> current = new();

    internal static Scope? Current => current.Value;

    // The job whose run this is (null outside a worker job).
    public static Guid? CurrentJobId => current.Value?.JobId;

    public static Scope Enter(Guid? jobId = null)
    {
        var scope = new Scope(current.Value, jobId);
        current.Value = scope;
        return scope;
    }

    // Adds one finished token wait to the running job's scope (nothing outside a job).
    internal static void RecordTokenWait(TimeSpan waited) => current.Value?.AddTokenWait(waited);

    public sealed class Scope : IDisposable
    {
        private readonly Scope? previous;
        private readonly HashSet<string> keys = new(StringComparer.Ordinal);
        private long tokenWaitTicks;
        private int tokenWaits;

        internal Scope(Scope? previous, Guid? jobId = null)
        {
            this.previous = previous;
            JobId = jobId;
        }

        public Guid? JobId { get; }

        public IReadOnlyCollection<string> Keys
        {
            get { lock (keys) return [.. keys]; }
        }

        // Total time the run's requests waited for a rate-limit token, and how many requests had to wait.
        public TimeSpan TokenWait => TimeSpan.FromTicks(Interlocked.Read(ref tokenWaitTicks));
        public int TokenWaits => Volatile.Read(ref tokenWaits);

        internal void Record(string key)
        {
            lock (keys) keys.Add(key);
        }

        internal void AddTokenWait(TimeSpan waited)
        {
            if (waited <= TimeSpan.Zero) return;
            Interlocked.Add(ref tokenWaitTicks, waited.Ticks);
            Interlocked.Increment(ref tokenWaits);
        }

        public void Dispose() => current.Value = previous;
    }
}
