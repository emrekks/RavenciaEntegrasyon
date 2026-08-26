using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class JobLeaseService(AppDbContext db, TokenHasher hasher, TimeProvider timeProvider) : IJobLeaseService
{
    public async Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, int? maximumPriority, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await db.IntegrationJobs
            .Where(x => (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled) && x.AttemptCount >= x.MaxAttempts)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, JobStatus.Dead)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.LastErrorCode, x => x.LastErrorCode ?? "MAX_ATTEMPTS_EXHAUSTED")
                .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        var job = maximumPriority is null
            ? await db.IntegrationJobs
                .FromSqlInterpolated($"SELECT * FROM integration.jobs WHERE \"Status\" IN ('PENDING', 'RETRY_SCHEDULED') AND \"AttemptCount\" < \"MaxAttempts\" AND \"AvailableAt\" <= {now} ORDER BY \"Priority\" ASC, \"AvailableAt\" ASC, \"CreatedAt\" ASC FOR UPDATE SKIP LOCKED LIMIT 1")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.IntegrationJobs
                .FromSqlInterpolated($"SELECT * FROM integration.jobs WHERE \"Status\" IN ('PENDING', 'RETRY_SCHEDULED') AND \"AttemptCount\" < \"MaxAttempts\" AND \"AvailableAt\" <= {now} AND \"Priority\" <= {maximumPriority.Value} ORDER BY \"Priority\" ASC, \"AvailableAt\" ASC, \"CreatedAt\" ASC FOR UPDATE SKIP LOCKED LIMIT 1")
                .SingleOrDefaultAsync(cancellationToken);
        if (job is null) { await transaction.CommitAsync(cancellationToken); return null; }
        var token = TokenHasher.NewToken();
        job.Status = JobStatus.Leased;
        job.LeaseTokenHash = hasher.Hash(token);
        job.LeaseExpiresAt = now.Add(leaseDuration);
        job.HeartbeatAt = now;
        job.StartedAt ??= now;
        job.AttemptCount++;
        db.JobAttempts.Add(new JobAttempt { Id = Guid.NewGuid(), TenantId = job.TenantId, JobId = job.Id, AttemptNumber = job.AttemptCount, StartedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LeasedJob(job.Id, job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, token);
    }

    public async Task<bool> CompleteAsync(Guid jobId, string leaseToken, JobExecutionResult result, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.Status != JobStatus.Leased || job.LeaseTokenHash is null || !hasher.Verify(leaseToken, job.LeaseTokenHash)) return false;
        var now = timeProvider.GetUtcNow();
        if (job.LeaseExpiresAt <= now) return false;

        var kind = result.Kind;
        if (kind == JobCompletionKind.Retry && job.AttemptCount >= job.MaxAttempts) kind = JobCompletionKind.Dead;
        job.Status = kind switch
        {
            JobCompletionKind.Succeeded => JobStatus.Succeeded,
            JobCompletionKind.Retry => JobStatus.RetryScheduled,
            JobCompletionKind.Blocked => JobStatus.Blocked,
            JobCompletionKind.ManualReview => JobStatus.ManualReview,
            _ => JobStatus.Dead
        };
        job.CompletedAt = kind == JobCompletionKind.Retry ? null : now;
        job.AvailableAt = kind == JobCompletionKind.Retry
            ? now.Add(EffectiveRetryDelay(job, result))
            : job.AvailableAt;
        job.LastErrorCode = result.ErrorCode;
        job.LastErrorSummary = result.ErrorSummary;
        job.LeaseTokenHash = null;
        job.LeaseExpiresAt = null;
        job.HeartbeatAt = null;
        job.Version++;

        var attempt = await db.JobAttempts.SingleAsync(x => x.JobId == jobId && x.AttemptNumber == job.AttemptCount, cancellationToken);
        attempt.CompletedAt = now;
        attempt.Succeeded = kind == JobCompletionKind.Succeeded;
        attempt.ErrorCode = kind == JobCompletionKind.Dead && result.Kind == JobCompletionKind.Retry
            ? result.ErrorCode ?? "MAX_ATTEMPTS_EXHAUSTED"
            : result.ErrorCode;
        attempt.ErrorSummary = result.ErrorSummary;
        if (kind == JobCompletionKind.Dead && result.Kind == JobCompletionKind.Retry)
        {
            job.LastErrorCode = attempt.ErrorCode;
            job.LastErrorSummary ??= "Maksimum otomatik deneme sayısına ulaşıldı.";
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static TimeSpan EffectiveRetryDelay(IntegrationJob job, JobExecutionResult result)
    {
        var requested = result.RetryAfter;
        // Product approval is a bounded, read-only reconciliation poll. Its processor
        // deliberately asks for five minutes; applying the generic terminal one-hour
        // backoff would prevent the seven-day acceptance window from being observed.
        // Provider/network retries still follow the generic policy and Retry-After.
        if (job.JobType == MarketplaceJobTypes.ProductApprovalReconcile
            && string.Equals(result.ErrorCode, "PRODUCT_APPROVAL_PENDING", StringComparison.Ordinal)
            && requested.HasValue
            && requested.Value > TimeSpan.Zero)
            return requested.Value;

        var policyDelay = JobRetryPolicy.DelayAfterAttempt(job.AttemptCount, job.Id);
        if (requested is null || requested <= TimeSpan.Zero) return policyDelay;
        var bounded = requested > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : requested.Value;
        return bounded > policyDelay ? bounded : policyDelay;
    }

    public async Task<bool> HeartbeatAsync(Guid jobId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken)
    {
        if (extension <= TimeSpan.Zero || extension > TimeSpan.FromMinutes(5)) return false;
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (job is null || job.Status != JobStatus.Leased || job.LeaseTokenHash is null || job.LeaseExpiresAt <= now || !hasher.Verify(leaseToken, job.LeaseTokenHash)) return false;
        job.HeartbeatAt = now; job.LeaseExpiresAt = now.Add(extension);
        await db.SaveChangesAsync(cancellationToken); return true;
    }

    public async Task<int> ReapExpiredAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        var expired = await db.IntegrationJobs
            .FromSqlInterpolated($"SELECT * FROM integration.jobs WHERE \"Status\" = 'LEASED' AND \"LeaseExpiresAt\" < {now} ORDER BY \"LeaseExpiresAt\" ASC FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);
        foreach (var job in expired)
        {
            var attempt = await db.JobAttempts.SingleOrDefaultAsync(x => x.JobId == job.Id && x.AttemptNumber == job.AttemptCount, cancellationToken);
            if (attempt is not null && attempt.CompletedAt is null) { attempt.CompletedAt = now; attempt.Succeeded = false; attempt.ErrorCode = "LEASE_EXPIRED"; }
            job.LastErrorCode = "LEASE_EXPIRED"; job.LeaseTokenHash = null; job.LeaseExpiresAt = null; job.HeartbeatAt = null; job.Version++;
            if (job.AttemptCount >= job.MaxAttempts) { job.Status = JobStatus.Dead; job.CompletedAt = now; }
            else { job.Status = JobStatus.RetryScheduled; job.AvailableAt = now.Add(JobRetryPolicy.DelayAfterAttempt(job.AttemptCount, job.Id)); }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return expired.Count;
    }
}
