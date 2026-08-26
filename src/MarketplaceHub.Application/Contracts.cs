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
}

public interface IJobLeaseService
{
    Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, int? maximumPriority, CancellationToken cancellationToken);
    Task<bool> HeartbeatAsync(Guid jobId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid jobId, string leaseToken, JobExecutionResult result, CancellationToken cancellationToken);
    Task<int> ReapExpiredAsync(CancellationToken cancellationToken);
}

public sealed record LeasedJob(Guid Id, Guid TenantId, Guid? ConnectionId, string JobType, string PayloadJson, string CorrelationId, string LeaseToken);
