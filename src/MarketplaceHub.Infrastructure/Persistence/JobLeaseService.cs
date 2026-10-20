using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class JobLeaseService(AppDbContext db, TokenHasher hasher, TimeProvider timeProvider) : IJobLeaseService
{
    private static readonly TimeSpan MaximumDeferralAge = TimeSpan.FromHours(24);
    private static readonly string[] LockGroupJobTypes = [.. MarketplaceJobTypes.ProcessorJobTypes];
    private static readonly string[] LockGroups = [.. MarketplaceJobTypes.ProcessorJobTypes.Select(MarketplaceSyncExecutionLock.GroupFor)];

    public async Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken, string? onlyJobType = null, Guid? onlyJobId = null, string[]? includedJobTypes = null, string[]? excludedJobTypes = null)
    {
        var now = timeProvider.GetUtcNow();
        includedJobTypes ??= [];
        excludedJobTypes ??= [];
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        // A job whose connection already runs a job of the same execution-lock group is skipped
        // here instead of being leased and then deferred, so the slot takes the next job at once.
        // Two slots leasing at the same instant can still collide; the processor's advisory lock
        // remains the final guard and defers the loser without consuming an attempt.
        // The running jobs are counted once per tenant and the busy connection lock groups gathered once
        // (derived tables the planner joins by hash) instead of a correlated count and lookup for every
        // eligible row before LIMIT 1.
        var job = await db.IntegrationJobs.FromSqlInterpolated($"""
            SELECT candidate.* FROM integration.jobs candidate
            LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS candidate_group(job_type, lock_group)
              ON candidate_group.job_type = candidate."JobType"
            LEFT JOIN (
                SELECT tenant_running."TenantId", count(*) AS running_count
                FROM integration.jobs tenant_running
                WHERE tenant_running."Status" = 'LEASED' AND tenant_running."LeaseExpiresAt" > {now}
                GROUP BY tenant_running."TenantId") tenant_load
              ON tenant_load."TenantId" = candidate."TenantId"
            WHERE candidate."Status" IN ('PENDING', 'RETRY_SCHEDULED')
              AND candidate."AttemptCount" < candidate."MaxAttempts"
              AND candidate."AvailableAt" <= {now}
              AND ({minimumPriority}::int IS NULL OR candidate."Priority" >= {minimumPriority}::int)
              AND ({maximumPriority}::int IS NULL OR candidate."Priority" <= {maximumPriority}::int)
              AND ({onlyJobType}::text IS NULL OR candidate."JobType" = {onlyJobType}::text)
              AND (cardinality({includedJobTypes}::text[]) = 0 OR candidate."JobType" = ANY({includedJobTypes}::text[]))
              AND NOT (candidate."JobType" = ANY({excludedJobTypes}::text[]))
              AND ({onlyJobId}::uuid IS NULL OR candidate."Id" = {onlyJobId}::uuid)
              AND NOT EXISTS (
                  SELECT 1
                  FROM (
                      SELECT DISTINCT running."ConnectionId", running_group.lock_group
                      FROM integration.jobs running
                      JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS running_group(job_type, lock_group)
                        ON running_group.job_type = running."JobType"
                      WHERE running."Status" = 'LEASED'
                        AND running."LeaseExpiresAt" > {now}
                        AND running."ConnectionId" IS NOT NULL) busy
                  WHERE busy."ConnectionId" = candidate."ConnectionId"
                    AND busy.lock_group = candidate_group.lock_group)
              -- A connection + lock group that hit a marketplace quota waits until its backoff ends.
              AND NOT EXISTS (
                  SELECT 1
                  FROM integration.connection_backoffs backoff
                  WHERE backoff."ConnectionId" = candidate."ConnectionId"
                    AND backoff."LockGroup" = candidate_group.lock_group
                    AND backoff."Until" > {now})
            ORDER BY candidate."Priority" ASC,
              -- Fairness: within one priority, the tenant with fewer running jobs goes first, so a
              -- tenant with hundreds of queued jobs cannot keep every slot to itself.
              coalesce(tenant_load.running_count, 0) ASC,
              candidate."AvailableAt" ASC, candidate."CreatedAt" ASC
            FOR UPDATE OF candidate SKIP LOCKED
            LIMIT 1
            """).SingleOrDefaultAsync(cancellationToken);
        if (job is null) { await transaction.CommitAsync(cancellationToken); return null; }
        var token = TokenHasher.NewToken();
        job.Status = JobStatus.Leased;
        job.LeaseTokenHash = hasher.Hash(token);
        job.LeaseExpiresAt = now.Add(leaseDuration);
        job.HeartbeatAt = now;
        job.StartedAt ??= now;
        job.AttemptCount++;
        // The panel learns that the job started now, not only when it finishes.
        job.Version++;
        db.JobAttempts.Add(new JobAttempt { Id = Guid.NewGuid(), TenantId = job.TenantId, JobId = job.Id, AttemptNumber = job.AttemptCount, StartedAt = now });
        AddOutboxEvent(job, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LeasedJob(job.Id, job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, token);
    }

    private async Task RecordConnectionBackoffAsync(Guid connectionId, string jobType, DateTimeOffset now, JobExecutionResult result, CancellationToken cancellationToken)
    {
        var wait = result.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(30);
        if (wait > TimeSpan.FromMinutes(15)) wait = TimeSpan.FromMinutes(15);
        var until = now.Add(wait);
        var lockGroup = MarketplaceSyncExecutionLock.GroupFor(jobType);
        var reason = result.ErrorCode is { Length: > 128 } code ? code[..128] : result.ErrorCode;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO integration.connection_backoffs ("ConnectionId", "LockGroup", "Until", "Reason")
            VALUES ({connectionId}, {lockGroup}, {until}, {reason})
            ON CONFLICT ("ConnectionId", "LockGroup") DO UPDATE
            SET "Until" = GREATEST(integration.connection_backoffs."Until", EXCLUDED."Until"), "Reason" = EXCLUDED."Reason"
            """, cancellationToken);
    }

    public async Task<bool> ReleaseAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.Status != JobStatus.Leased || job.LeaseTokenHash is null || !hasher.Verify(leaseToken, job.LeaseTokenHash)) return false;
        var now = timeProvider.GetUtcNow();
        if (job.LeaseExpiresAt <= now) return false;

        // The interrupted run is refunded: its attempt row is dropped so the next lease
        // re-runs the job under the same attempt number.
        var attempt = await db.JobAttempts.SingleOrDefaultAsync(x => x.JobId == jobId && x.AttemptNumber == job.AttemptCount, cancellationToken);
        if (attempt is not null) db.JobAttempts.Remove(attempt);
        job.AttemptCount = Math.Max(0, job.AttemptCount - 1);
        job.Status = JobStatus.RetryScheduled;
        job.AvailableAt = now;
        job.LastErrorCode = "WORKER_SHUTDOWN";
        job.LastErrorSummary = "Worker yeniden başlatıldı; iş kuyruğa geri bırakıldı.";
        job.LeaseTokenHash = null;
        job.LeaseExpiresAt = null;
        job.HeartbeatAt = null;
        job.Version++;
        AddOutboxEvent(job, now);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteAsync(Guid jobId, string leaseToken, JobExecutionResult result, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.Status != JobStatus.Leased || job.LeaseTokenHash is null || !hasher.Verify(leaseToken, job.LeaseTokenHash)) return false;
        var now = timeProvider.GetUtcNow();
        if (job.LeaseExpiresAt <= now) return false;
        if (result.BackOffConnection && job.ConnectionId is { } backoffConnectionId)
            await RecordConnectionBackoffAsync(backoffConnectionId, job.JobType, now, result, cancellationToken);

        // A deferred job never ran, so its attempt is refunded. Jobs deferred for over a day fall
        // back to normal attempt accounting so a permanently busy lock cannot loop forever. A finished
        // slice of a long job (Continue) is progress, not waiting: a large import or a 90-day
        // reconciliation may take more than a day of slices and must not die of its age.
        if (RefundsAttempt(result, now - job.CreatedAt))
        {
            var deferredAttempt = await db.JobAttempts.SingleOrDefaultAsync(x => x.JobId == jobId && x.AttemptNumber == job.AttemptCount, cancellationToken);
            if (deferredAttempt is not null) db.JobAttempts.Remove(deferredAttempt);
            job.AttemptCount = Math.Max(0, job.AttemptCount - 1);
            job.Status = JobStatus.RetryScheduled;
            job.AvailableAt = now.Add(result.RetryAfter is { } delay && delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(30));
            job.LastErrorCode = result.ErrorCode;
            job.LastErrorSummary = result.ErrorSummary;
            job.LeaseTokenHash = null;
            job.LeaseExpiresAt = null;
            job.HeartbeatAt = null;
            job.Version++;
            AddOutboxEvent(job, now);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

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

        AddOutboxEvent(job, now);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static bool RefundsAttempt(JobExecutionResult result, TimeSpan jobAge) =>
        result.Kind == JobCompletionKind.Retry && result.RefundAttempt && (result.Continues || jobAge < MaximumDeferralAge);

    private static TimeSpan EffectiveRetryDelay(IntegrationJob job, JobExecutionResult result)
    {
        var requested = result.RetryAfter;
        if (job.JobType is InvoicingJobTypes.MarketplaceDelivery or InvoicingJobTypes.ManualOrderInvoiceDelivery)
            return InvoiceDeliveryRetryPolicy.DelayAfterAttempt(job.AttemptCount, requested);

        // Product approval is a bounded, read-only reconciliation poll. Its processor
        // deliberately asks for five minutes; applying the generic terminal one-hour
        // backoff would prevent the seven-day acceptance window from being observed.
        // Provider/network retries still follow the generic policy and Retry-After.
        if (job.JobType == MarketplaceJobTypes.ProductApprovalReconcile
            && string.Equals(result.ErrorCode, "PRODUCT_APPROVAL_PENDING", StringComparison.Ordinal)
            && requested.HasValue
            && requested.Value > TimeSpan.Zero)
            // Approvals usually land within minutes, so poll every minute for the first
            // attempts and fall back to the processor's slower cadence afterwards.
            return job.AttemptCount < 10 && requested.Value > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : requested.Value;

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
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // An operator may have cancelled the job while this heartbeat was in flight.
            // Treat the stale lease as lost so the worker cancels its local execution.
            return false;
        }
    }

    public async Task<int> ReapExpiredAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hasReapableJobs = await db.IntegrationJobs.AsNoTracking().AnyAsync(x =>
            x.Status == JobStatus.Leased && x.LeaseExpiresAt < now
            || (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled) && x.AttemptCount >= x.MaxAttempts,
            cancellationToken);
        if (!hasReapableJobs) return 0;

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        // Both sets are locked as they are read, so an operator cancelling one of them waits for this pass
        // instead of racing it. The two sets are still saved separately and a row that conflicts anyway is
        // left out on its own: one row can no longer cancel the recovery of every other expired lease.
        var exhausted = await db.IntegrationJobs
            .FromSqlInterpolated($"SELECT * FROM integration.jobs WHERE \"Status\" IN ('PENDING', 'RETRY_SCHEDULED') AND \"AttemptCount\" >= \"MaxAttempts\" FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);
        var changes = new List<(IntegrationJob Job, IntegrationOutboxEvent Event, JobAttempt? Attempt)>();
        foreach (var job in exhausted)
        {
            job.Status = JobStatus.Dead;
            job.CompletedAt = now;
            job.LastErrorCode ??= "MAX_ATTEMPTS_EXHAUSTED";
            job.Version++;
            changes.Add((job, AddOutboxEvent(job, now), null));
        }
        var reaped = await SaveReapedAsync(changes, cancellationToken);

        var expired = await db.IntegrationJobs
            .FromSqlInterpolated($"SELECT * FROM integration.jobs WHERE \"Status\" = 'LEASED' AND \"LeaseExpiresAt\" < {now} ORDER BY \"LeaseExpiresAt\" ASC FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);
        changes.Clear();
        foreach (var job in expired)
        {
            var attempt = await db.JobAttempts.SingleOrDefaultAsync(x => x.JobId == job.Id && x.AttemptNumber == job.AttemptCount, cancellationToken);
            if (attempt is not null && attempt.CompletedAt is null) { attempt.CompletedAt = now; attempt.Succeeded = false; attempt.ErrorCode = "LEASE_EXPIRED"; }
            job.LastErrorCode = "LEASE_EXPIRED"; job.LeaseTokenHash = null; job.LeaseExpiresAt = null; job.HeartbeatAt = null; job.Version++;
            if (job.AttemptCount >= job.MaxAttempts) { job.Status = JobStatus.Dead; job.CompletedAt = now; }
            else { job.Status = JobStatus.RetryScheduled; job.AvailableAt = now.Add(JobRetryPolicy.DelayAfterAttempt(job.AttemptCount, job.Id)); }
            changes.Add((job, AddOutboxEvent(job, now), attempt));
        }
        reaped += await SaveReapedAsync(changes, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return reaped;
    }

    // Saves one reaper set. A row another writer changed meanwhile (concurrency token) is dropped together
    // with its outbox event and attempt update, and the rest is saved again; the next pass looks at it afresh.
    private async Task<int> SaveReapedAsync(List<(IntegrationJob Job, IntegrationOutboxEvent Event, JobAttempt? Attempt)> changes, CancellationToken cancellationToken)
    {
        while (changes.Count > 0)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return changes.Count;
            }
            catch (DbUpdateConcurrencyException exception)
            {
                var conflicting = exception.Entries.Select(entry => entry.Entity).OfType<IntegrationJob>().ToHashSet();
                if (conflicting.Count == 0) throw;
                foreach (var change in changes.Where(change => conflicting.Contains(change.Job)).ToArray())
                {
                    db.Entry(change.Job).State = EntityState.Detached;
                    db.Entry(change.Event).State = EntityState.Detached;
                    if (change.Attempt is not null) db.Entry(change.Attempt).State = EntityState.Detached;
                    changes.Remove(change);
                }
            }
        }
        return 0;
    }

    private IntegrationOutboxEvent AddOutboxEvent(IntegrationJob job, DateTimeOffset now)
    {
        var metadata = IntegrationJobMetadataPolicy.Apply(job);
        var outboxEvent = new IntegrationOutboxEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = job.TenantId,
            ResourceType = metadata.ResourceType,
            OperationType = metadata.OperationType,
            AggregateType = "IntegrationJob",
            AggregateId = job.Id,
            AggregateVersion = job.Version,
            PayloadJson = JsonSerializer.Serialize(new { jobId = job.Id, jobType = job.JobType, status = JobStatusText(job.Status), version = job.Version }),
            CreatedAt = now,
            NextAttemptAt = now
        };
        db.IntegrationOutboxEvents.Add(outboxEvent);
        return outboxEvent;
    }

    private static string JobStatusText(JobStatus status) => status == JobStatus.RetryScheduled ? "RETRY_SCHEDULED" : status == JobStatus.ManualReview ? "MANUAL_REVIEW" : status.ToString().ToUpperInvariant();
}
