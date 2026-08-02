using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class JobLeaseService(AppDbContext db, TokenHasher hasher, TimeProvider timeProvider) : IJobLeaseService
{
    public async Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
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
        var job = await db.IntegrationJobs
            .FromSqlInterpolated($"SELECT * FROM integration.jobs WHERE \"Status\" IN ('PENDING', 'RETRY_SCHEDULED') AND \"AttemptCount\" < \"MaxAttempts\" AND \"AvailableAt\" <= {now} ORDER BY \"Priority\" ASC, \"AvailableAt\" ASC, \"CreatedAt\" ASC FOR UPDATE SKIP LOCKED LIMIT 1")
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

    public async Task<bool> CompleteAsync(Guid jobId, string leaseToken, bool succeeded, string? errorCode, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.Status != JobStatus.Leased || job.LeaseTokenHash is null || !hasher.Verify(leaseToken, job.LeaseTokenHash)) return false;
        var now = timeProvider.GetUtcNow();
        if (job.LeaseExpiresAt <= now) return false;
        job.Status = succeeded ? JobStatus.Succeeded : errorCode == "UNSUPPORTED_JOB_TYPE" ? JobStatus.Dead : JobStatus.Blocked;
        job.CompletedAt = now;
        job.LastErrorCode = errorCode;
        job.LeaseTokenHash = null; job.LeaseExpiresAt = null; job.HeartbeatAt = null;
        var attempt = await db.JobAttempts.SingleAsync(x => x.JobId == jobId && x.AttemptNumber == job.AttemptCount, cancellationToken);
        attempt.CompletedAt = now; attempt.Succeeded = succeeded; attempt.ErrorCode = errorCode;
        await db.SaveChangesAsync(cancellationToken);
        return true;
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
