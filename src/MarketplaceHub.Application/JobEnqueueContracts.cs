using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// KUYRUK-8 (15 Nov 2026): one way to build a new job row. A request names what the caller decides (type, payload, keys,
// priority, trigger, attempts, times, lock group); the factory fills what every job shares (payload version and hash,
// the first version). A field left null keeps the entity's default, so a caller that never set it still does not:
// - Priority null is 0; TriggerType null is "system" (AppDbContext rewrites an unset trigger from the job type on save);
// - MaxAttempts null is JobRetryPolicy.DefaultMaxAttempts; CreatedAt null is the database clock;
// - EffectIdempotencyKey null is the dedup key; LockGroup null is the job type's group.
public sealed record IntegrationJobRequest(
    Guid TenantId,
    Guid? ConnectionId,
    string JobType,
    string PayloadJson,
    string DedupKey,
    string CorrelationId,
    DateTimeOffset AvailableAt)
{
    public Guid? Id { get; init; }
    public string? EffectIdempotencyKey { get; init; }
    public int? Priority { get; init; }
    public string? TriggerType { get; init; }
    public int? MaxAttempts { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? LockGroup { get; init; }
    public string? ResourceType { get; init; }
    public string? OperationType { get; init; }
}

// How an enqueue treats a job that already has the same (TenantId, JobType, JobDedupKey). The unique index on those
// three columns is the backstop either way.
public enum JobDedupPolicy
{
    // The caller has already decided that a new job is wanted (its own open-job, prefix, payload or replay check, or a key
    // that is unique by construction: a timestamp, a Guid, a version). The job is added as it is.
    CallerChecked,
    // A row with the same key in any status is the job: nothing is added and its id is returned.
    ReuseAnyWithKey
}

public static class IntegrationJobFactory
{
    public static IntegrationJob New(IntegrationJobRequest request)
    {
        var job = new IntegrationJob
        {
            Id = request.Id ?? Guid.CreateVersion7(),
            TenantId = request.TenantId,
            ConnectionId = request.ConnectionId,
            JobType = request.JobType,
            PayloadJson = request.PayloadJson,
            PayloadVersion = 1,
            PayloadHash = PayloadHash(request.PayloadJson),
            JobDedupKey = request.DedupKey,
            EffectIdempotencyKey = request.EffectIdempotencyKey ?? request.DedupKey,
            AvailableAt = request.AvailableAt,
            CorrelationId = request.CorrelationId,
            LockGroup = request.LockGroup,
            Version = 1
        };
        if (request.Priority is { } priority) job.Priority = priority;
        if (request.TriggerType is { } trigger) job.TriggerType = trigger;
        if (request.MaxAttempts is { } maxAttempts) job.MaxAttempts = maxAttempts;
        if (request.CreatedAt is { } createdAt) job.CreatedAt = createdAt;
        if (request.ResourceType is { } resourceType) job.ResourceType = resourceType;
        if (request.OperationType is { } operationType) job.OperationType = operationType;
        return job;
    }

    // The SHA-256 of the payload's UTF-8 bytes in upper-case hex, as every writer has stored it.
    public static string PayloadHash(string payloadJson) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
}
