namespace MarketplaceHub.Domain;

public enum JobStatus { Pending, Leased, RetryScheduled, Blocked, Succeeded, Dead, Cancelled }
public enum IssueStatus { Open, Acknowledged, Resolved }

public sealed class IntegrationJob
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ConnectionId { get; set; }
    public required string JobType { get; set; }
    public required string PayloadJson { get; set; }
    public int PayloadVersion { get; set; }
    public required string PayloadHash { get; set; }
    public required string JobDedupKey { get; set; }
    public required string EffectIdempotencyKey { get; set; }
    public int Priority { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTimeOffset AvailableAt { get; set; }
    public string? LeaseTokenHash { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = JobRetryPolicy.DefaultMaxAttempts;
    public string? LastErrorCode { get; set; }
    public string? LastErrorSummary { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; }
}

public sealed class JobAttempt
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid JobId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorSummary { get; set; }
}

public sealed class InboxMessage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Source { get; set; }
    public required string ExternalMessageId { get; set; }
    public required string PayloadHash { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

public sealed class ExternalEffectRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string EffectType { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public string? TargetId { get; set; }
    public required string Reason { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OperationalIssue
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public required string DedupeKey { get; set; }
    public required string Code { get; set; }
    public required string Summary { get; set; }
    public IssueStatus Status { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int OccurrenceCount { get; set; }
}

public sealed class FeatureFlag
{
    public required string Key { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class FileAsset
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Classification { get; set; }
    public required string RelativePath { get; set; }
    public string? OriginalNameSafe { get; set; }
    public required string MimeType { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256 { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class ApiIdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string RouteTemplate { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public required string State { get; set; }
    public int? ResponseStatus { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
