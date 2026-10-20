namespace MarketplaceHub.Domain;

public enum JobStatus { Pending, Leased, RetryScheduled, Blocked, ManualReview, Succeeded, Dead, Cancelled }
public enum IssueStatus { Open, Acknowledged, Resolved }

public sealed class IntegrationJob
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ConnectionId { get; set; }
    public required string JobType { get; set; }
    public string ResourceType { get; set; } = "jobs";
    public string OperationType { get; set; } = "execute";
    public string TriggerType { get; set; } = "system";
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
    public int ProgressCurrent { get; set; }
    public int? ProgressTotal { get; set; }
    public int? ProgressPercent { get; set; }
    public string? ProgressLabel { get; set; }
    public int ProgressReceived { get; set; }
    public int ProgressProcessed { get; set; }
    public int ProgressSkipped { get; set; }
    public int ProgressFailed { get; set; }
    // Consecutive write conflicts (a version conflict or a duplicate insert) of this job; any other outcome
    // resets it. The first few version conflicts are deferred without an attempt, later ones retry normally.
    public int ConflictCount { get; set; }
    // The connection execution-lock group the job runs under when it differs from its job type's group (a
    // return decision read-back serializes with the return reads). The lease query and the quota backoff use
    // the same group as the lock; null means the job type's group.
    public string? LockGroup { get; set; }
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
    // What the effect changes, so an unclear outcome can be checked by reading the marketplace:
    // the connection, the local package, order or claim, the action and the value it should leave.
    public Guid? ConnectionId { get; set; }
    public Guid? TargetId { get; set; }
    public string? Action { get; set; }
    public string? ExpectedValue { get; set; }
    // APPLIED, or UNRESOLVED when the marketplace could not confirm it in time; null while open.
    public string? Outcome { get; set; }
    // Verification bookkeeping: how many reads left the outcome unclear, the verification time actually spent
    // (a gap while the worker was stopped does not count) and when the last read ran.
    public int UnclearChecks { get; set; }
    public int VerificationSeconds { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
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
    // When the issue was last resolved; retention deletes a resolved issue only some time after this.
    public DateTimeOffset? ResolvedAt { get; set; }
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
    public string? ResponseBody { get; set; }
    // A replay answers with the same Content-Type, ETag and Location as the first response, so the
    // client keeps the version it needs for its next If-Match and the address of what was created.
    public string? ResponseContentType { get; set; }
    public string? ResponseETag { get; set; }
    public string? ResponseLocation { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
