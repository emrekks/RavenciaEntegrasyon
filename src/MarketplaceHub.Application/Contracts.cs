namespace MarketplaceHub.Application;

public sealed record TenantContext(Guid UserId, Guid TenantId, string Role);

public interface ITenantContextAccessor
{
    TenantContext? Current { get; }
}

public interface IPrivateFileStorage
{
    Task<string> SaveAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken);
    // Removes a stored file of the tenant; false when it was already gone.
    Task<bool> DeleteAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken);
}

public interface IJobLeaseService
{
    Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken, string? onlyJobType = null, Guid? onlyJobId = null, string[]? includedJobTypes = null, string[]? excludedJobTypes = null, JobQuotaHold? quotaHold = null);
    Task<bool> HeartbeatAsync(Guid jobId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid jobId, string leaseToken, JobExecutionResult result, CancellationToken cancellationToken);
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

// QueueWait: how long the job waited between becoming runnable and this lease (slot, lock group or backoff wait).
public sealed record LeasedJob(Guid Id, Guid TenantId, Guid? ConnectionId, string JobType, string PayloadJson, string CorrelationId, string LeaseToken, TimeSpan? QueueWait = null);
