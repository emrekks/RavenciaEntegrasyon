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
    Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken, string? onlyJobType = null, Guid? onlyJobId = null, string[]? includedJobTypes = null, string[]? excludedJobTypes = null);
    Task<bool> HeartbeatAsync(Guid jobId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid jobId, string leaseToken, JobExecutionResult result, CancellationToken cancellationToken);
    Task<int> ReapExpiredAsync(CancellationToken cancellationToken);
    /// <summary>Hands a leased job back to the queue (e.g. on worker shutdown) without consuming an attempt.</summary>
    Task<bool> ReleaseAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken);
}

// QueueWait: how long the job waited between becoming runnable and this lease (slot, lock group or backoff wait).
public sealed record LeasedJob(Guid Id, Guid TenantId, Guid? ConnectionId, string JobType, string PayloadJson, string CorrelationId, string LeaseToken, TimeSpan? QueueWait = null);
