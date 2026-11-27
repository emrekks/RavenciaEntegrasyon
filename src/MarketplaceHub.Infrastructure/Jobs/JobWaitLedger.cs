using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Jobs;

// Queue phase 0 (27 Nov 2026), measurement only: why a leased job waited, from what this worker process itself saw. Nothing
// here is read by the lease, the pool or the quota hold; it only answers JobAttemptStart.Waits with the attempt row.
// The process records, in memory:
//   - each run of a job that has a lock group, from its lease to its end (the lease's busy check skips the connection's other
//     jobs of that group meanwhile);
//   - each quota backoff it wrote (the lease skips the connection and group until it ends);
//   - (QuotaPressure) when each quota bucket was full, and (the lane pool, through SlotSpells) when a lane had no free slot.
// A leased job's wait (EligibleAt to the lease) is split into those causes, each moment counted once, in the order lock,
// backoff, quota hold, slot. Each figure is time the process knows that cause held the job; the rest of the wait has no
// known cause. The figures are null when the wait began before the process started or before the history it keeps
// (HistoryKept), and the slot figure is null without a lane pool (legacy lanes). A run another process leased (the API's
// inline invoice run, a second worker) is not seen here, so a figure can be under-stated; it is never over-stated: the quota
// hold figure is null when the job's learned buckets changed during its wait (the hold then used buckets no longer known).
// Every collection is bounded (review of 27 Nov 2026): spans per connection and group MaximumSpellsPerKey, connection and
// group keys MaximumKeys, run ends MaximumRunEnds (a time-ordered queue, O(1) amortised per call), running jobs
// MaximumRunning (in practice the worker's slots); a store past its cap is trimmed to 80 % of it at once.
public sealed class JobWaitLedger(TimeProvider clock)
{
    public static readonly TimeSpan HistoryKept = TimeSpan.FromHours(6);
    internal const int MaximumSpellsPerKey = 1_024;
    internal const int MaximumKeys = 20_000;
    internal const int MaximumRunEnds = 50_000;
    internal const int MaximumRunning = 4_096;

    private readonly object gate = new();
    private readonly Dictionary<(Guid ConnectionId, string LockGroup), List<Span>> busy = [];
    private readonly Dictionary<Guid, (Guid ConnectionId, string LockGroup, DateTimeOffset From)> running = [];
    private readonly Dictionary<(Guid ConnectionId, string LockGroup), List<Span>> backoffs = [];
    private readonly Dictionary<Guid, DateTimeOffset> runEnded = [];
    private readonly Queue<(Guid JobId, DateTimeOffset At)> runEndOrder = new();

    public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();

    // How the quota hold is set up now (null: quota-aware leasing is off, nothing is held).
    public QuotaHoldShape? QuotaHold { get; set; }

    // The spells in which none of the lanes had a free slot (null: not known, e.g. legacy lanes without a pool).
    public Func<IReadOnlyList<string>, DateTimeOffset, DateTimeOffset, IReadOnlyList<Span>?>? SlotSpells { get; set; }

    // The lanes that could lease a job of this type and priority.
    public Func<string, int, IReadOnlyList<string>>? LanesFor { get; set; }

    public QuotaPressure Pressure { get; set; } = QuotaPressure.Shared;

    public sealed record QuotaHoldShape(TimeSpan MaximumHold, int HeldAbovePriority);

    public readonly record struct Span(DateTimeOffset From, DateTimeOffset To);

    // A job was leased: while it runs, its connection's other jobs of its lock group are skipped by the lease.
    public void RunStarted(Guid jobId, Guid? connectionId, string? lockGroup, DateTimeOffset at)
    {
        if (connectionId is not { } connection || string.IsNullOrEmpty(lockGroup)) return;
        lock (gate)
        {
            running[jobId] = (connection, lockGroup, at);
            // A run whose end never came (cannot happen: the worker ends every run in a finally) would otherwise stay.
            if (running.Count > MaximumRunning)
                foreach (var oldest in running.OrderBy(item => item.Value.From).Take(running.Count - (MaximumRunning * 4 / 5)).Select(item => item.Key).ToList())
                    running.Remove(oldest);
        }
    }

    // The job's run ended (its outcome written, or it was handed back): its group is free from now. The end is kept for the
    // job's next attempt (how long it was not due after this run).
    public void RunEnded(Guid jobId, DateTimeOffset at)
    {
        lock (gate)
        {
            if (running.Remove(jobId, out var run)) Add(busy, (run.ConnectionId, run.LockGroup), new Span(run.From, at));
            runEnded[jobId] = at;
            runEndOrder.Enqueue((jobId, at));
            // Oldest first: past a day, or past the cap down to 80 % of it. An entry a later end of the same job replaced is
            // only dropped from the queue.
            var keepAfter = at - TimeSpan.FromHours(24);
            var trimTo = runEndOrder.Count > MaximumRunEnds ? MaximumRunEnds * 4 / 5 : int.MaxValue;
            while (runEndOrder.TryPeek(out var oldest) && (oldest.At < keepAfter || runEndOrder.Count > trimTo))
            {
                runEndOrder.Dequeue();
                if (runEnded.TryGetValue(oldest.JobId, out var stored) && stored == oldest.At) runEnded.Remove(oldest.JobId);
            }
        }
    }

    internal int RunEndCount
    {
        get { lock (gate) return runEnded.Count; }
    }

    // This process wrote a quota backoff for the connection and group (JobLeaseService.ConnectionBackoffWait).
    public void BackoffRecorded(Guid connectionId, string lockGroup, DateTimeOffset from, DateTimeOffset until)
    {
        if (string.IsNullOrEmpty(lockGroup) || until <= from) return;
        lock (gate) Add(backoffs, (connectionId, lockGroup), new Span(from, until));
    }

    // A completion of the leased job that asked for a quota backoff (JobLeaseService records it under the job's stored lock
    // group, else its type's group, for JobLeaseService.ConnectionBackoffWait from the completion).
    public void BackoffRecorded(LeasedJob job, JobExecutionResult result, DateTimeOffset at)
    {
        if (!result.BackOffConnection || job.ConnectionId is not { } connectionId) return;
        // The lease's busy group is the stored group when there is one; a job without one is backed off under its type's group.
        var group = job.LockGroup ?? Persistence.MarketplaceSyncExecutionLock.GroupFor(job.JobType);
        BackoffRecorded(connectionId, group, at, at + Persistence.JobLeaseService.ConnectionBackoffWait(result));
    }

    // Called under the lock. Spans come in end order (a run's or a backoff's end), so the stale ones are a prefix.
    private static void Add(Dictionary<(Guid, string), List<Span>> store, (Guid, string) key, Span span)
    {
        if (span.To <= span.From) return;
        if (!store.TryGetValue(key, out var spans))
        {
            if (store.Count >= MaximumKeys) TrimKeys(store, span.To - HistoryKept);
            store[key] = spans = [];
        }
        spans.Add(span);
        var keepAfter = span.To - HistoryKept;
        var stale = 0;
        while (stale < spans.Count && spans[stale].To < keepAfter) stale++;
        if (spans.Count - stale > MaximumSpellsPerKey) stale = spans.Count - (MaximumSpellsPerKey * 4 / 5);
        if (stale > 0) spans.RemoveRange(0, stale);
    }

    // Keys whose history ended before keepAfter go; if that is not enough, the least recently used down to 80 % of the cap.
    private static void TrimKeys(Dictionary<(Guid, string), List<Span>> store, DateTimeOffset keepAfter)
    {
        foreach (var stale in store.Where(item => item.Value.Count == 0 || item.Value[^1].To < keepAfter).Select(item => item.Key).ToList()) store.Remove(stale);
        if (store.Count < MaximumKeys) return;
        foreach (var oldest in store.OrderBy(item => item.Value[^1].To).Take(store.Count - (MaximumKeys * 4 / 5)).Select(item => item.Key).ToList()) store.Remove(oldest);
    }

    // The attempt's wait split by known cause (JobAttemptStart.Waits).
    // The spans the last Attribute call read (its work is linear in them, a sort aside): a test measures the caps by it.
    internal int LastSpanCount { get; private set; }

    public JobWaitBreakdown Attribute(JobWaitQuery query)
    {
        var notDue = NotDue(query);
        var from = query.EligibleAt;
        var to = query.LeasedAt;
        // A wait that began before this process saw anything (or before the history it keeps) has no known causes.
        if (from < StartedAt || from < to - HistoryKept) return new JobWaitBreakdown(notDue, null, null, null, null);
        if (to <= from) return new JobWaitBreakdown(notDue, 0, 0, 0, SlotSpells is null ? null : 0);

        List<Span> lockSpans = [], backoffSpans = [];
        if (query.ConnectionId is { } connection && !string.IsNullOrEmpty(query.LockGroup))
            lock (gate)
            {
                var key = (connection, query.LockGroup);
                if (busy.TryGetValue(key, out var done)) lockSpans.AddRange(done);
                foreach (var (jobId, run) in running)
                    if (jobId != query.JobId && run.ConnectionId == connection && run.LockGroup == query.LockGroup) lockSpans.Add(new Span(run.From, to));
                if (backoffs.TryGetValue(key, out var held)) backoffSpans.AddRange(held);
            }

        // The quota hold: only above the hold's priority, only for MaximumHold after the job became runnable, and only from
        // the buckets the pair had learned all through the wait. Buckets that changed during it make the figure unknown.
        List<Span> holdSpans = [];
        var holdKnown = true;
        if (QuotaHold is { } hold && query.ConnectionId is { } holdConnection && query.Priority > hold.HeldAbovePriority && hold.MaximumHold > TimeSpan.Zero)
        {
            var holdTo = from + hold.MaximumHold < to ? from + hold.MaximumHold : to;
            var (keys, changedAt) = Pressure.LearnedKeysSince(holdConnection, query.JobType);
            if (changedAt is { } changed && changed > from) holdKnown = false;
            else holdSpans.AddRange(Pressure.FullSpells(keys, from, holdTo, to).Select(spell => new Span(spell.From, spell.To < holdTo ? spell.To : holdTo)));
        }

        IReadOnlyList<Span>? slotSpans = null;
        if (SlotSpells is { } slots && LanesFor is { } lanesFor)
        {
            var lanes = lanesFor(query.JobType, query.Priority);
            slotSpans = lanes.Count == 0 ? [] : slots(lanes, from, to);
        }

        LastSpanCount = lockSpans.Count + backoffSpans.Count + holdSpans.Count + (slotSpans?.Count ?? 0);
        var lockSet = Merge(lockSpans, from, to);
        var backoffSet = Merge(backoffSpans, from, to);
        var holdSet = Merge(holdSpans, from, to);
        var counted = lockSet;
        var backoff = Except(backoffSet, counted);
        counted = Union(counted, backoffSet);
        int? quotaHold = holdKnown ? Milliseconds(Except(holdSet, counted)) : null;
        counted = Union(counted, holdSet);
        int? slot = slotSpans is null ? null : Milliseconds(Except(Merge(slotSpans, from, to), counted));
        return new JobWaitBreakdown(notDue, Milliseconds(Length(lockSet)), Milliseconds(backoff), quotaHold, slot);
    }

    // How long the job was not due before EligibleAt: since its previous run ended (seen by this process), or since it was
    // queued when it never ran, failed or was retried. Otherwise not known.
    private int? NotDue(JobWaitQuery query)
    {
        DateTimeOffset? since;
        lock (gate) since = runEnded.TryGetValue(query.JobId, out var ended) ? ended : null;
        since ??= query.Fresh ? query.CreatedAt : null;
        return since is { } start ? Milliseconds(query.EligibleAt > start ? query.EligibleAt - start : TimeSpan.Zero) : null;
    }

    // The spans clipped to [from, to], sorted and merged.
    // Spans arrive almost sorted (recorded in end order), so a sort is only done when one is out of place.
    internal static List<Span> Merge(IEnumerable<Span> spans, DateTimeOffset from, DateTimeOffset to)
    {
        var clipped = new List<Span>();
        var sorted = true;
        foreach (var item in spans)
        {
            if (item.To <= from || item.From >= to) continue;
            var span = new Span(item.From < from ? from : item.From, item.To > to ? to : item.To);
            if (clipped.Count > 0 && span.From < clipped[^1].From) sorted = false;
            clipped.Add(span);
        }
        if (!sorted) clipped.Sort((left, right) => left.From.CompareTo(right.From));
        var merged = new List<Span>(clipped.Count);
        foreach (var span in clipped)
        {
            if (merged.Count > 0 && span.From <= merged[^1].To)
            {
                if (span.To > merged[^1].To) merged[^1] = merged[^1] with { To = span.To };
            }
            else merged.Add(span);
        }
        return merged;
    }

    // The union of two merged sets, merged (one pass over both).
    internal static List<Span> Union(List<Span> a, List<Span> b)
    {
        var result = new List<Span>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
        {
            var next = j >= b.Count || (i < a.Count && a[i].From <= b[j].From) ? a[i++] : b[j++];
            if (result.Count > 0 && next.From <= result[^1].To)
            {
                if (next.To > result[^1].To) result[^1] = result[^1] with { To = next.To };
            }
            else result.Add(next);
        }
        return result;
    }

    internal static TimeSpan Length(IEnumerable<Span> merged) => merged.Aggregate(TimeSpan.Zero, (total, span) => total + (span.To - span.From));

    // The length of the merged set a that lies outside the merged set b (both sorted and merged): one pass over both.
    internal static TimeSpan Except(List<Span> a, List<Span> b)
    {
        var total = TimeSpan.Zero;
        var j = 0;
        foreach (var span in a)
        {
            var covered = TimeSpan.Zero;
            while (j < b.Count && b[j].To <= span.From) j++;
            for (var k = j; k < b.Count && b[k].From < span.To; k++)
            {
                var start = b[k].From > span.From ? b[k].From : span.From;
                var end = b[k].To < span.To ? b[k].To : span.To;
                if (end > start) covered += end - start;
            }
            total += span.To - span.From - covered;
        }
        return total;
    }

    public static int Milliseconds(TimeSpan value) => (int)Math.Clamp(Math.Round(value.TotalMilliseconds), 0, int.MaxValue);
}
