namespace MarketplaceHub.Application;

public sealed record TenantContext(Guid UserId, Guid TenantId, string Role);

public interface ITenantContextAccessor
{
    TenantContext? Current { get; }
}

public interface IPrivateFileStorage
{
    Task<string> SaveAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken);
    // Writes the file under a temporary name next to its final place. The caller commits it once the database row that
    // points at RelativePath is saved; disposing it uncommitted removes the temporary file, so a failed database write
    // never leaves a file under a name a row could point at. Stores without staging write the final file at once.
    async Task<IStagedPrivateFile> StageAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken) =>
        new CommittedPrivateFile(await SaveAsync(tenantId, relativeName, mimeType, content, maximumBytes, cancellationToken));
    // Temporary files older than the given time: one whose final path a row records (isReferenced) was saved but not
    // renamed, and is renamed now; any other was never saved (a process that died in between) and is removed.
    Task<StagedSweepResult> SweepStagedAsync(DateTimeOffset olderThan, Func<string, CancellationToken, Task<bool>> isReferenced, CancellationToken cancellationToken) =>
        Task.FromResult(new StagedSweepResult(0, 0, 0));
    Task<Stream> OpenReadAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken);
    // Removes a stored file of the tenant; false when it was already gone.
    Task<bool> DeleteAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken);
}

public interface IStagedPrivateFile : IAsyncDisposable
{
    // The stored path the database row records (the final name).
    string RelativePath { get; }
    // Moves the temporary file to its final name; call it after the database commit.
    void Commit();
}

// Removed: never-saved temporary files deleted. Completed: saved ones renamed to their final name. Kept: saved ones whose
// rename failed again (they stay; nothing is deleted).
public sealed record StagedSweepResult(int Removed, int Completed, int Kept);

public sealed class CommittedPrivateFile(string relativePath) : IStagedPrivateFile
{
    public string RelativePath { get; } = relativePath;
    public void Commit() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public interface IJobLeaseService
{
    Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken, string? onlyJobType = null, Guid? onlyJobId = null, string[]? includedJobTypes = null, string[]? excludedJobTypes = null, JobQuotaHold? quotaHold = null, JobFairness? fairness = null, JobBulkhead? bulkhead = null, JobAttemptStart? attemptStart = null, JobConnectionCap? connectionCap = null, JobDeferral? deferral = null);
    Task<bool> HeartbeatAsync(Guid jobId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid jobId, string leaseToken, JobExecutionResult result, CancellationToken cancellationToken, JobAttemptRun? run = null);
    Task<int> ReapExpiredAsync(CancellationToken cancellationToken);
    /// <summary>Hands a leased job back to the queue (e.g. on worker shutdown) without consuming an attempt.</summary>
    Task<bool> ReleaseAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken);
}

// Quota-aware leasing (worker lanes step 1, 19 Nov 2026): jobs of these connection + job type pairs are not leased while the
// marketplace quota they used last time is full (another job of the same quota has been waiting for a token for a while), so
// they do not sit in a worker slot waiting for it. Only background work is held: a job of priority HeldAbovePriority or
// lower (user actions, new-order reads, webhooks, writes: the worker passes its hot priority ceiling) is never held, because
// a bulk user action can keep a foreground bucket busy for minutes and a new order must still be read meanwhile. A job is
// held at most MaximumHold after it became runnable, then leased as before (no starvation). The pairs come from the
// worker's QuotaPressure; ConnectionIds[i] goes with JobTypes[i].
public sealed record JobQuotaHold(IReadOnlyList<Guid> ConnectionIds, IReadOnlyList<string> JobTypes, TimeSpan MaximumHold, int HeldAbovePriority = JobQuotaHold.DefaultHeldAbovePriority)
{
    // WorkerLanes.DefaultHotPriorityCeiling: the hot lane's highest priority number.
    public const int DefaultHeldAbovePriority = 2;
    public static readonly JobQuotaHold None = new([], [], TimeSpan.Zero);
    public bool IsEmpty => ConnectionIds.Count == 0 || MaximumHold <= TimeSpan.Zero;
}

// Connection fairness (worker lanes step 2, 19 Nov 2026): within one priority, after the tenant with fewer running jobs, the
// connection with fewer running jobs goes first, then the one that was leased a job longest ago (LastLeasedAt, from the
// worker; a connection it has not leased for since it started counts as never leased). User actions (priority
// BulkUserAction or lower) keep their order. A job runnable for Aging or longer goes ahead of the fairness order, oldest
// first, so no job waits for ever behind other connections. ConnectionIds[i] goes with LastLeasedAt[i].
public sealed record JobFairness(TimeSpan Aging, IReadOnlyList<Guid> ConnectionIds, IReadOnlyList<DateTimeOffset> LastLeasedAt);

// Per-marketplace bulkheads (worker lanes step 2), counted per tenant and marketplace (a marketplace quota belongs to the
// seller account, so one tenant never limits another):
//   - a job of a class (JobTypes[i] belongs to Classes[i]) whose priority is within ClassMinimumPriority..ClassMaximumPriority
//     is not leased while ClassCaps[j] jobs of its class (ClassNames[j]) in that priority range already run for the same
//     tenant and marketplace; jobs of the class outside the range (a background recovery read, a first import) neither
//     count nor are limited;
//   - with LaneCap set, a job is not leased while that many jobs of the leasing lane run for the same tenant and marketplace.
// User actions and jobs without a connection are never limited.
public sealed record JobBulkhead(IReadOnlyList<string> JobTypes, IReadOnlyList<string> Classes, IReadOnlyList<string> ClassNames, IReadOnlyList<int> ClassCaps, int? LaneCap,
    int ClassMinimumPriority = 0, int ClassMaximumPriority = JobQuotaHold.DefaultHeldAbovePriority)
{
    public bool IsEmpty => (ClassNames.Count == 0 || JobTypes.Count == 0) && LaneCap is null;
}

// Queue phase 2 (27 Nov 2026), the dispatcher's per-store cap: a capped job is not leased while Cap capped jobs of its
// connection already run. A job is capped (and counted) when it matches one of the Rules, the filters of the capped lanes
// (DispatcherLanes.ConnectionCap): priority within MinimumPriority..MaximumPriority (null: open), its type one of IncludedJobTypes
// (empty: any) and none of ExcludedJobTypes. Counted from the LEASED rows in the database, so the lease query stays the one
// source of truth; the dispatcher leases one job at a time, so two of its own leases never both see Cap - 1. Null: no cap (the
// lane models before the dispatcher).
public sealed record JobConnectionCap(int Cap, IReadOnlyList<JobConnectionCap.Rule> Rules)
{
    public sealed record Rule(int? MinimumPriority, int? MaximumPriority, IReadOnlyList<string> IncludedJobTypes, IReadOnlyList<string> ExcludedJobTypes);

    public bool Caps(string jobType, int priority) => Rules.Any(rule =>
        (rule.MinimumPriority is not { } minimum || priority >= minimum)
        && (rule.MaximumPriority is not { } maximum || priority <= maximum)
        && (rule.IncludedJobTypes.Count == 0 || rule.IncludedJobTypes.Contains(jobType))
        && !rule.ExcludedJobTypes.Contains(jobType));
}

// Queue phase 2 (review of 27 Nov 2026): a job is not leased while its connection has a runnable job of one of AheadJobTypes
// queued in the same lock group (not under a quota backoff). The dispatcher passes it for the status scans and the background
// reads, so a store's order read goes before its next status scan or order reconciliation ("orders" lock group). Null: none.
public sealed record JobDeferral(IReadOnlyList<string> AheadJobTypes);

// QueueWait: how long the job waited between becoming runnable and this lease (slot, lock group or backoff wait).
// Priority, LockGroup (the group the lease's busy check uses; null when the job has none) and EligibleAt (when it became
// runnable for this attempt) let the worker record what held it (queue phase 0).
public sealed record LeasedJob(Guid Id, Guid TenantId, Guid? ConnectionId, string JobType, string PayloadJson, string CorrelationId, string LeaseToken, TimeSpan? QueueWait = null,
    int Priority = 0, string? LockGroup = null, DateTimeOffset? EligibleAt = null);

// Queue phase 0 (27 Nov 2026), measurement only. What the leasing worker knows about the attempt it starts: its lane, whether
// the slot was borrowed, and (Waits) why the job waited, asked once the job is picked and written with the attempt row in the
// lease's own save. Nothing in it changes which job is leased or when. A null value is unknown, never a guess.
public sealed record JobAttemptStart(string? Lane, bool? Borrowed, Func<JobWaitQuery, JobWaitBreakdown>? Waits = null);

// The picked job as the wait attribution needs it. LockGroup is the group the lease's busy check compares (null: none).
// Fresh: the job never ran, failed or was retried (its first lease after it was queued).
public sealed record JobWaitQuery(Guid JobId, Guid? ConnectionId, string JobType, int Priority, string? LockGroup, DateTimeOffset CreatedAt, DateTimeOffset EligibleAt, DateTimeOffset LeasedAt, bool Fresh);

// Milliseconds of the attempt's wait per known cause (JobAttempt.WaitLockMs ...), and NotDueMs before EligibleAt.
public sealed record JobWaitBreakdown(int? NotDueMs, int? LockMs, int? BackoffMs, int? QuotaHoldMs, int? SlotMs)
{
    public static readonly JobWaitBreakdown Unknown = new(null, null, null, null, null);
}

// What the worker measured inside the run, written with the attempt's completion: rate-limit token waits.
public sealed record JobAttemptRun(int? QuotaWaitMs, int? QuotaWaits);
