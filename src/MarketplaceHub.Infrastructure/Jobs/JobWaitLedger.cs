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
// inline invoice run, a second worker) is not seen here, so a figure can be low, never invented.
public sealed class JobWaitLedger(TimeProvider clock)
{
    public static readonly TimeSpan HistoryKept = TimeSpan.FromHours(6);
    private const int MaximumSpellsPerKey = 1_024;
    private const int MaximumRunEnds = 50_000;

    private readonly object gate = new();
    private readonly Dictionary<(Guid ConnectionId, string LockGroup), List<Span>> busy = [];
    private readonly Dictionary<Guid, (Guid ConnectionId, string LockGroup, DateTimeOffset From)> running = [];
    private readonly Dictionary<(Guid ConnectionId, string LockGroup), List<Span>> backoffs = [];
    private readonly Dictionary<Guid, DateTimeOffset> runEnded = [];

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
        lock (gate) running[jobId] = (connection, lockGroup, at);
    }

    // The job's run ended (its outcome written, or it was handed back): its group is free from now. The end is kept for the
    // job's next attempt (how long it was not due after this run).
    public void RunEnded(Guid jobId, DateTimeOffset at)
    {
        lock (gate)
        {
            if (running.Remove(jobId, out var run)) Add(busy, (run.ConnectionId, run.LockGroup), new Span(run.From, at));
            runEnded[jobId] = at;
            if (runEnded.Count > MaximumRunEnds)
            {
                var keepAfter = at - TimeSpan.FromHours(24);
                foreach (var stale in runEnded.Where(item => item.Value < keepAfter).Select(item => item.Key).ToList()) runEnded.Remove(stale);
                if (runEnded.Count > MaximumRunEnds)
                    foreach (var oldest in runEnded.OrderBy(item => item.Value).Take(runEnded.Count - MaximumRunEnds).Select(item => item.Key).ToList()) runEnded.Remove(oldest);
            }
        }
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

    private static void Add(Dictionary<(Guid, string), List<Span>> store, (Guid, string) key, Span span)
    {
        if (span.To <= span.From) return;
        if (!store.TryGetValue(key, out var spans)) store[key] = spans = [];
        spans.Add(span);
        var keepAfter = span.To - HistoryKept;
        spans.RemoveAll(item => item.To < keepAfter);
        if (spans.Count > MaximumSpellsPerKey) spans.RemoveRange(0, spans.Count - MaximumSpellsPerKey);
    }

    // The attempt's wait split by known cause (JobAttemptStart.Waits).
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

        List<Span> holdSpans = [];
        if (QuotaHold is { } hold && query.ConnectionId is { } holdConnection && query.Priority > hold.HeldAbovePriority && hold.MaximumHold > TimeSpan.Zero)
        {
            // The lease holds a job only for MaximumHold after it became runnable.
            var holdTo = from + hold.MaximumHold < to ? from + hold.MaximumHold : to;
            var keys = Pressure.LearnedKeys(holdConnection, query.JobType);
            holdSpans.AddRange(Pressure.FullSpells(keys, from, holdTo, to).Select(spell => new Span(spell.From, spell.To < holdTo ? spell.To : holdTo)));
        }

        IReadOnlyList<Span>? slotSpans = null;
        if (SlotSpells is { } slots && LanesFor is { } lanesFor)
        {
            var lanes = lanesFor(query.JobType, query.Priority);
            slotSpans = lanes.Count == 0 ? [] : slots(lanes, from, to);
        }

        var lockSet = Merge(lockSpans, from, to);
        var backoffSet = Merge(backoffSpans, from, to);
        var holdSet = Merge(holdSpans, from, to);
        var counted = lockSet;
        var backoff = Except(backoffSet, counted);
        counted = Merge([.. counted, .. backoffSet], from, to);
        var quotaHold = Except(holdSet, counted);
        counted = Merge([.. counted, .. holdSet], from, to);
        int? slot = slotSpans is null ? null : Milliseconds(Except(Merge(slotSpans, from, to), counted));
        return new JobWaitBreakdown(notDue, Milliseconds(Length(lockSet)), Milliseconds(backoff), Milliseconds(quotaHold), slot);
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
    internal static List<Span> Merge(IEnumerable<Span> spans, DateTimeOffset from, DateTimeOffset to)
    {
        var merged = new List<Span>();
        foreach (var span in spans.Select(item => new Span(item.From < from ? from : item.From, item.To > to ? to : item.To)).Where(item => item.To > item.From).OrderBy(item => item.From))
        {
            if (merged.Count > 0 && span.From <= merged[^1].To)
                merged[^1] = merged[^1] with { To = span.To > merged[^1].To ? span.To : merged[^1].To };
            else merged.Add(span);
        }
        return merged;
    }

    internal static TimeSpan Length(IEnumerable<Span> merged) => merged.Aggregate(TimeSpan.Zero, (total, span) => total + (span.To - span.From));

    // The length of the merged set a that lies outside the merged set b.
    internal static TimeSpan Except(List<Span> a, List<Span> b)
    {
        var total = TimeSpan.Zero;
        foreach (var span in a)
        {
            var covered = TimeSpan.Zero;
            foreach (var other in b)
            {
                var start = other.From > span.From ? other.From : span.From;
                var end = other.To < span.To ? other.To : span.To;
                if (end > start) covered += end - start;
            }
            total += span.To - span.From - covered;
        }
        return total;
    }

    public static int Milliseconds(TimeSpan value) => (int)Math.Clamp(Math.Round(value.TotalMilliseconds), 0, int.MaxValue);
}
