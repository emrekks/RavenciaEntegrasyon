using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class JobLeaseService(AppDbContext db, TokenHasher hasher, TimeProvider timeProvider) : IJobLeaseService
{
    private static readonly TimeSpan MaximumDeferralAge = TimeSpan.FromHours(24);
    private static readonly string[] LockGroupJobTypes = [.. MarketplaceJobTypes.ProcessorJobTypes];
    private static readonly string[] LockGroups = [.. MarketplaceJobTypes.ProcessorJobTypes.Select(MarketplaceSyncExecutionLock.GroupFor)];

    public async Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken, string? onlyJobType = null, Guid? onlyJobId = null, string[]? includedJobTypes = null, string[]? excludedJobTypes = null, JobQuotaHold? quotaHold = null, JobFairness? fairness = null, JobBulkhead? bulkhead = null, JobAttemptStart? attemptStart = null, JobConnectionCap? connectionCap = null, JobDeferral? deferral = null)
    {
        // Queue phase 2, the dispatcher's per-store cap (JobConnectionCap): off (null) for the earlier lane models, so their
        // lease is the same query as before (every cap term is constant false).
        var capOn = connectionCap is { Cap: > 0 };
        var cap = connectionCap?.Cap ?? 0;
        // Each rule as parallel arrays; its types as "|TYPE|TYPE|" ("" = any included type, none excluded).
        var capRules = connectionCap?.Rules ?? [];
        var capMinimums = capRules.Select(rule => rule.MinimumPriority ?? int.MinValue).ToArray();
        var capMaximums = capRules.Select(rule => rule.MaximumPriority ?? int.MaxValue).ToArray();
        var capIncluded = capRules.Select(rule => rule.IncludedJobTypes.Count == 0 ? "" : "|" + string.Join('|', rule.IncludedJobTypes) + "|").ToArray();
        var capExcluded = capRules.Select(rule => "|" + string.Join('|', rule.ExcludedJobTypes) + "|").ToArray();
        // Queue phase 2, deferral (JobDeferral): off (null) unless the dispatcher passes it for its status scans.
        var deferOn = deferral is { AheadJobTypes.Count: > 0 };
        var aheadJobTypes = deferral?.AheadJobTypes.ToArray() ?? [];
        var aheadMinimum = deferral?.MinimumAheadPriority ?? 0;
        var aheadMaximum = deferral?.MaximumAheadPriority ?? 0;
        var now = timeProvider.GetUtcNow();
        includedJobTypes ??= [];
        excludedJobTypes ??= [];
        // Quota-aware leasing (JobQuotaHold): the held pairs and the moment before which a held job became runnable too long
        // ago to be held any longer. No pairs: the condition matches nothing.
        var hold = quotaHold is { IsEmpty: false } ? quotaHold : JobQuotaHold.None;
        var heldConnections = hold.ConnectionIds.ToArray();
        var heldJobTypes = hold.JobTypes.ToArray();
        var holdRunnableAfter = now - hold.MaximumHold;
        // Connection fairness (JobFairness): candidates are taken per connection instead of per tenant, so one connection's
        // older jobs cannot fill the candidate list, and the order puts the least served connection first. Off: the same
        // candidates and order as before (every fairness term is constant).
        var fair = fairness is not null;
        var fairConnections = fairness?.ConnectionIds.ToArray() ?? [];
        var fairLeasedAt = fairness?.LastLeasedAt.ToArray() ?? [];
        var agedBefore = fairness is null ? DateTimeOffset.MinValue : now - fairness.Aging;
        var candidateLimit = fair ? FairCandidateLimit : CandidateLimit;
        // Bulkheads (JobBulkhead): at most N running jobs of a class, or of the leasing lane, per marketplace. The count is read
        // under READ COMMITTED, so two slots leasing at the same instant may both see N - 1 and run N + 1 for a moment. Left so
        // (24 Nov 2026): a class's jobs of one connection share a lock group (the busy check and the processor's advisory lock
        // keep them apart), live has one connection per tenant and marketplace (no overlapping class job in 7 days), and the
        // marketplace's request rate is kept by its quota limiter, not by this cap. Serialising the lease (an advisory lock
        // and a recount) would cost every lease a round trip for a case that cannot happen today.
        var bulk = bulkhead is { IsEmpty: false } ? bulkhead : null;
        var bulkJobTypes = bulk?.JobTypes.ToArray() ?? [];
        var bulkClasses = bulk?.Classes.ToArray() ?? [];
        var bulkClassNames = bulk?.ClassNames.ToArray() ?? [];
        var bulkClassCaps = bulk?.ClassCaps.ToArray() ?? [];
        var laneCap = bulk?.LaneCap;
        var classMinimumPriority = bulk?.ClassMinimumPriority ?? 0;
        var classMaximumPriority = bulk?.ClassMaximumPriority ?? 0;
        var bulkOn = bulk is not null;
        var heldAbovePriority = Math.Max(JobPriorityPolicy.BulkUserAction, hold.HeldAbovePriority);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        // A job whose connection already runs a job of the same execution-lock group, or whose connection and
        // group wait out a quota backoff, is skipped here instead of being leased and then deferred, so the slot
        // takes the next job at once. Two slots leasing at the same instant can still collide; the processor's
        // advisory lock remains the final guard. A job's stored LockGroup wins over its type's group, so the
        // lease checks the same group the lock takes.
        // Each tenant with runnable jobs contributes only its first CandidateLimit runnable rows in (Priority,
        // AvailableAt) order (a top-N sort per tenant instead of sorting every eligible row), and the tenant
        // fairness order is applied to those: one tenant's hundreds of older jobs can no longer fill the whole
        // candidate list and keep the other tenants out of it. The status, availability and attempt conditions
        // are repeated on the locked row so a row another slot leased meanwhile is re-checked after its lock is taken.
        var job = await db.IntegrationJobs.FromSqlInterpolated($"""
            WITH busy AS (
                SELECT DISTINCT running."ConnectionId", coalesce(running."LockGroup", running_group.lock_group) AS lock_group
                FROM integration.jobs running
                LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS running_group(job_type, lock_group)
                  ON running_group.job_type = running."JobType"
                WHERE running."Status" = 'LEASED'
                  AND running."LeaseExpiresAt" > {now}
                  AND running."ConnectionId" IS NOT NULL),
            -- Bulkheads: the running jobs with their marketplace (only read when a bulkhead is set).
            running_platform AS (
                SELECT running."TenantId", running."JobType", running."Priority", running_connection."PlatformCode" AS platform
                FROM integration.jobs running
                JOIN integration.platform_connections running_connection ON running_connection."Id" = running."ConnectionId"
                WHERE {bulkOn}
                  AND running."Status" = 'LEASED'
                  AND running."LeaseExpiresAt" > {now}),
            -- Queue phase 2: each connection's running jobs that count toward the per-store cap (only read when it is set).
            capped_load AS (
                SELECT running."ConnectionId", count(*) AS running_count
                FROM integration.jobs running
                WHERE {capOn}
                  AND running."Status" = 'LEASED'
                  AND running."LeaseExpiresAt" > {now}
                  AND running."ConnectionId" IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM unnest({capMinimums}::int[], {capMaximums}::int[], {capIncluded}::text[], {capExcluded}::text[]) AS rule(minimum, maximum, included, excluded)
                      WHERE running."Priority" BETWEEN rule.minimum AND rule.maximum
                        AND (rule.included = '' OR strpos(rule.included, '|' || running."JobType" || '|') > 0)
                        AND strpos(rule.excluded, '|' || running."JobType" || '|') = 0)
                GROUP BY running."ConnectionId"),
            -- One candidate owner per tenant, or per tenant and connection with connection fairness.
            queued_tenants AS (
                SELECT DISTINCT queued."TenantId", CASE WHEN {fair} THEN queued."ConnectionId" END AS "ConnectionId"
                FROM integration.jobs queued
                WHERE queued."Status" IN ('PENDING', 'RETRY_SCHEDULED')
                  AND queued."AvailableAt" <= {now}),
            picks AS (
                SELECT tenant_pick.*
                FROM queued_tenants
                CROSS JOIN LATERAL (
                SELECT owner_pick."Id", owner_pick."TenantId", owner_pick."ConnectionId", owner_pick."Priority", owner_pick."AvailableAt", owner_pick."CreatedAt"
                FROM (
                SELECT candidate."Id", candidate."TenantId", candidate."ConnectionId", candidate."Priority", candidate."AvailableAt", candidate."CreatedAt",
                       -- With connection fairness only the first job of each lock group is a candidate: two slots woken
                       -- together would otherwise take the same connection's next job of a group the first slot is
                       -- leasing (SKIP LOCKED), and that lease is deferred by the lock. Jobs without a group stand alone.
                       row_number() OVER (
                           PARTITION BY coalesce(candidate."LockGroup", candidate_group.lock_group, candidate."Id"::text)
                           ORDER BY candidate."Priority" ASC, candidate."AvailableAt" ASC, candidate."CreatedAt" ASC) AS group_rank
                FROM integration.jobs candidate
                LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS candidate_group(job_type, lock_group)
                  ON candidate_group.job_type = candidate."JobType"
                LEFT JOIN integration.platform_connections candidate_connection
                  ON {bulkOn} AND candidate_connection."Id" = candidate."ConnectionId"
                WHERE candidate."TenantId" = queued_tenants."TenantId"
                  AND (NOT {fair} OR candidate."ConnectionId" IS NOT DISTINCT FROM queued_tenants."ConnectionId")
                  -- Bulkheads, per tenant and marketplace (a marketplace's quota is the seller account's, so one tenant
                  -- never limits another): a class counts and limits only jobs in its priority range (the range of the
                  -- dedicated lane it stands for: a recovery read in the background lane, a first import or a user's
                  -- refresh neither counts nor is limited); a lane cap counts the leasing lane's own jobs. Never a user
                  -- action, never a job without a connection.
                  AND NOT ({bulkOn} AND candidate."Priority" > {JobPriorityPolicy.BulkUserAction}
                      AND candidate_connection."PlatformCode" IS NOT NULL
                      AND ((candidate."Priority" BETWEEN {classMinimumPriority} AND {classMaximumPriority} AND EXISTS (
                              SELECT 1
                              FROM unnest({bulkJobTypes}::text[], {bulkClasses}::text[]) AS candidate_class(job_type, class_name)
                              JOIN unnest({bulkClassNames}::text[], {bulkClassCaps}::int[]) AS class_cap(class_name, cap)
                                ON class_cap.class_name = candidate_class.class_name
                              WHERE candidate_class.job_type = candidate."JobType"
                                AND class_cap.cap <= (
                                    SELECT count(*)
                                    FROM running_platform
                                    JOIN unnest({bulkJobTypes}::text[], {bulkClasses}::text[]) AS running_class(job_type, class_name)
                                      ON running_class.job_type = running_platform."JobType"
                                    WHERE running_class.class_name = candidate_class.class_name
                                      AND running_platform."TenantId" = candidate."TenantId"
                                      AND running_platform."Priority" BETWEEN {classMinimumPriority} AND {classMaximumPriority}
                                      AND running_platform.platform = candidate_connection."PlatformCode")))
                          OR ({laneCap}::int IS NOT NULL AND {laneCap}::int <= (
                              SELECT count(*)
                              FROM running_platform
                              WHERE running_platform.platform = candidate_connection."PlatformCode"
                                AND running_platform."TenantId" = candidate."TenantId"
                                AND ({minimumPriority}::int IS NULL OR running_platform."Priority" >= {minimumPriority}::int)
                                AND ({maximumPriority}::int IS NULL OR running_platform."Priority" <= {maximumPriority}::int)
                                AND (cardinality({includedJobTypes}::text[]) = 0 OR running_platform."JobType" = ANY({includedJobTypes}::text[]))
                                AND NOT (running_platform."JobType" = ANY({excludedJobTypes}::text[]))))))
                  AND candidate."Status" IN ('PENDING', 'RETRY_SCHEDULED')
                  AND candidate."AttemptCount" < candidate."MaxAttempts"
                  AND candidate."AvailableAt" <= {now}
                  AND ({minimumPriority}::int IS NULL OR candidate."Priority" >= {minimumPriority}::int)
                  AND ({maximumPriority}::int IS NULL OR candidate."Priority" <= {maximumPriority}::int)
                  AND ({onlyJobType}::text IS NULL OR candidate."JobType" = {onlyJobType}::text)
                  AND (cardinality({includedJobTypes}::text[]) = 0 OR candidate."JobType" = ANY({includedJobTypes}::text[]))
                  AND NOT (candidate."JobType" = ANY({excludedJobTypes}::text[]))
                  AND ({onlyJobId}::uuid IS NULL OR candidate."Id" = {onlyJobId}::uuid)
                  AND NOT EXISTS (
                      SELECT 1 FROM busy
                      WHERE busy."ConnectionId" = candidate."ConnectionId"
                        AND busy.lock_group = coalesce(candidate."LockGroup", candidate_group.lock_group))
                  -- A connection + lock group that hit a marketplace quota waits until its backoff ends.
                  AND NOT EXISTS (
                      SELECT 1
                      FROM integration.connection_backoffs backoff
                      WHERE backoff."ConnectionId" = candidate."ConnectionId"
                        AND backoff."LockGroup" = coalesce(candidate."LockGroup", candidate_group.lock_group)
                        AND backoff."Until" > {now})
                  -- Queue phase 2, per-store cap: a capped job waits while its connection already runs the cap's number of
                  -- capped jobs.
                  AND NOT ({capOn}
                      AND candidate."ConnectionId" IS NOT NULL
                      AND EXISTS (
                          SELECT 1 FROM unnest({capMinimums}::int[], {capMaximums}::int[], {capIncluded}::text[], {capExcluded}::text[]) AS rule(minimum, maximum, included, excluded)
                          WHERE candidate."Priority" BETWEEN rule.minimum AND rule.maximum
                            AND (rule.included = '' OR strpos(rule.included, '|' || candidate."JobType" || '|') > 0)
                            AND strpos(rule.excluded, '|' || candidate."JobType" || '|') = 0)
                      AND {cap} <= coalesce((SELECT capped_load.running_count FROM capped_load WHERE capped_load."ConnectionId" = candidate."ConnectionId"), 0))
                  -- Queue phase 2, deferral: a job waits while its connection has a runnable job of an ahead type queued.
                  AND NOT ({deferOn}
                      AND candidate."ConnectionId" IS NOT NULL
                      AND EXISTS (
                          SELECT 1 FROM integration.jobs ahead
                          LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS ahead_group(job_type, lock_group)
                            ON ahead_group.job_type = ahead."JobType"
                          WHERE ahead."ConnectionId" = candidate."ConnectionId"
                            AND ahead."Id" <> candidate."Id"
                            AND ahead."JobType" = ANY({aheadJobTypes}::text[])
                            AND ahead."Priority" BETWEEN {aheadMinimum} AND {aheadMaximum}
                            AND coalesce(ahead."LockGroup", ahead_group.lock_group) = coalesce(candidate."LockGroup", candidate_group.lock_group)
                            AND ahead."Status" IN ('PENDING', 'RETRY_SCHEDULED')
                            AND ahead."AvailableAt" <= {now}
                            AND ahead."AttemptCount" < ahead."MaxAttempts"
                            AND NOT EXISTS (
                                SELECT 1 FROM integration.connection_backoffs ahead_backoff
                                WHERE ahead_backoff."ConnectionId" = ahead."ConnectionId"
                                  AND ahead_backoff."LockGroup" = coalesce(ahead."LockGroup", ahead_group.lock_group)
                                  AND ahead_backoff."Until" > {now})
                            -- the job ahead is leasable now: not held by the per-store cap (the quota hold never holds its range)
                            AND NOT ({capOn}
                                AND EXISTS (
                                    SELECT 1 FROM unnest({capMinimums}::int[], {capMaximums}::int[], {capIncluded}::text[], {capExcluded}::text[]) AS ahead_rule(minimum, maximum, included, excluded)
                                    WHERE ahead."Priority" BETWEEN ahead_rule.minimum AND ahead_rule.maximum
                                      AND (ahead_rule.included = '' OR strpos(ahead_rule.included, '|' || ahead."JobType" || '|') > 0)
                                      AND strpos(ahead_rule.excluded, '|' || ahead."JobType" || '|') = 0)
                                AND {cap} <= coalesce((SELECT capped_load.running_count FROM capped_load WHERE capped_load."ConnectionId" = ahead."ConnectionId"), 0))))
                  -- Quota-aware leasing: a job whose marketplace quota is full right now waits in the queue, not in a slot.
                  -- Background work only (never a user action, a new-order read, a webhook or a write), and never longer
                  -- than the hold allows since the job became runnable.
                  AND NOT (candidate."Priority" > {heldAbovePriority}
                      AND greatest(candidate."AvailableAt", candidate."CreatedAt") > {holdRunnableAfter}
                      AND EXISTS (
                          SELECT 1 FROM unnest({heldConnections}::uuid[], {heldJobTypes}::text[]) AS held(connection_id, job_type)
                          WHERE held.connection_id = candidate."ConnectionId" AND held.job_type = candidate."JobType"))
                  -- Paneli Tamamen Sıfırla runs alone: from its request to its end nothing else of the tenant starts,
                  -- the scheduler's reads included (they would bring back the orders being deleted).
                  AND NOT EXISTS (
                      SELECT 1 FROM integration.jobs panel_reset
                      WHERE panel_reset."TenantId" = candidate."TenantId"
                        AND panel_reset."JobType" = {PanelJobTypes.FactoryReset}
                        AND panel_reset."Status" IN ('PENDING', 'LEASED', 'RETRY_SCHEDULED')
                        AND panel_reset."Id" <> candidate."Id"
                        -- Two open resets (queued before the request lock) do not block each other for ever: the
                        -- oldest one runs, the later one waits for it.
                        AND (candidate."JobType" <> {PanelJobTypes.FactoryReset}
                          OR (panel_reset."CreatedAt", panel_reset."Id") < (candidate."CreatedAt", candidate."Id")))
                ) owner_pick
                WHERE NOT {fair} OR owner_pick.group_rank = 1
                ORDER BY owner_pick."Priority" ASC, owner_pick."AvailableAt" ASC, owner_pick."CreatedAt" ASC
                LIMIT {candidateLimit}) tenant_pick),
            -- Connection fairness orders jobs of a connection only: user actions and jobs without a connection keep the
            -- earlier order (priority, tenant load, age).
            fair_picks AS (
                SELECT picks.*,
                       {fair} AND picks."Priority" > {JobPriorityPolicy.BulkUserAction} AND picks."ConnectionId" IS NOT NULL AS fair_job,
                       {fair} AND picks."Priority" > {JobPriorityPolicy.BulkUserAction} AND picks."ConnectionId" IS NOT NULL
                           AND greatest(picks."AvailableAt", picks."CreatedAt") <= {agedBefore} AS aged
                FROM picks)
            SELECT job.* FROM integration.jobs job
            JOIN fair_picks picks ON picks."Id" = job."Id"
            LEFT JOIN (
                SELECT tenant_running."TenantId", count(*) AS running_count
                FROM integration.jobs tenant_running
                WHERE tenant_running."Status" = 'LEASED' AND tenant_running."LeaseExpiresAt" > {now}
                GROUP BY tenant_running."TenantId") tenant_load
              ON tenant_load."TenantId" = picks."TenantId"
            LEFT JOIN (
                SELECT connection_running."ConnectionId", count(*) AS running_count
                FROM integration.jobs connection_running
                WHERE {fair} AND connection_running."Status" = 'LEASED' AND connection_running."LeaseExpiresAt" > {now}
                  AND connection_running."ConnectionId" IS NOT NULL
                GROUP BY connection_running."ConnectionId") connection_load
              ON connection_load."ConnectionId" = picks."ConnectionId"
            LEFT JOIN unnest({fairConnections}::uuid[], {fairLeasedAt}::timestamptz[]) AS last_lease(connection_id, leased_at)
              ON last_lease.connection_id = picks."ConnectionId"
            WHERE job."Status" IN ('PENDING', 'RETRY_SCHEDULED')
              AND job."AttemptCount" < job."MaxAttempts"
              AND job."AvailableAt" <= {now}
            ORDER BY picks."Priority" ASC,
              -- No starvation: a job runnable for the fairness aging time goes ahead, oldest first.
              picks.aged DESC,
              CASE WHEN picks.aged THEN greatest(picks."AvailableAt", picks."CreatedAt") END ASC,
              -- Fairness: within one priority, the tenant with fewer running jobs goes first, so a
              -- tenant with hundreds of queued jobs cannot keep every slot to itself.
              coalesce(tenant_load.running_count, 0) ASC,
              -- Connection fairness: then the connection with fewer running jobs, then the one served longest ago.
              CASE WHEN picks.fair_job THEN coalesce(connection_load.running_count, 0) ELSE 0 END ASC,
              CASE WHEN picks.fair_job THEN coalesce(last_lease.leased_at, '-infinity'::timestamptz) END ASC,
              picks."AvailableAt" ASC, picks."CreatedAt" ASC
            FOR UPDATE OF job SKIP LOCKED
            LIMIT 1
            """).SingleOrDefaultAsync(cancellationToken);
        if (job is null) { await transaction.CommitAsync(cancellationToken); return null; }
        var token = TokenHasher.NewToken();
        var firstLease = job.StartedAt is null;
        var queueWait = QueueWait(job, now);
        // Queue phase 0: read before the job changes (the attempt count and error are those the job waited with).
        var eligibleAt = EligibleAt(job);
        var busyLockGroup = BusyLockGroupOf(job);
        var waits = AttemptWaits(attemptStart, new JobWaitQuery(job.Id, job.ConnectionId, job.JobType, job.Priority, busyLockGroup, job.CreatedAt, eligibleAt, now,
            Fresh: job.AttemptCount == 0 && job.LastErrorCode is null && job.RetriedAt is null));
        JobStateMachine.MoveTo(job, JobStatus.Leased);
        job.LeaseTokenHash = hasher.Hash(token);
        job.LeaseExpiresAt = now.Add(leaseDuration);
        job.HeartbeatAt = now;
        job.StartedAt ??= now;
        job.AttemptCount++;
        job.Version++;
        db.JobAttempts.Add(new JobAttempt
        {
            Id = Guid.NewGuid(),
            TenantId = job.TenantId,
            JobId = job.Id,
            AttemptNumber = job.AttemptCount,
            StartedAt = now,
            EligibleAt = eligibleAt,
            Lane = attemptStart?.Lane is { Length: > 0 } lane ? lane[..Math.Min(lane.Length, 40)] : null,
            Borrowed = attemptStart?.Borrowed,
            NotDueMs = waits.NotDueMs,
            WaitLockMs = waits.LockMs,
            WaitBackoffMs = waits.BackoffMs,
            WaitQuotaHoldMs = waits.QuotaHoldMs,
            WaitSlotMs = waits.SlotMs
        });
        // The panel learns that the job started, once: later leases of the same job (retries, slices of a long
        // run) would only make every open screen refetch its job list again.
        if (firstLease) AddOutboxEvent(job, now);
        // The dispatcher's lease timeout stops before the row is locked, never at COMMIT (JobAttemptStart.FinishOnceLocked).
        var finishToken = attemptStart?.FinishOnceLocked == true ? CancellationToken.None : cancellationToken;
        await db.SaveChangesAsync(finishToken);
        await transaction.CommitAsync(finishToken);
        return new LeasedJob(job.Id, job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, token, queueWait, job.Priority, busyLockGroup, eligibleAt);
    }

    // Queue phase 0: when the job became runnable for the attempt being leased (the later of AvailableAt and CreatedAt, the
    // moment QueueWait counts from), kept on the attempt row because the job's AvailableAt is overwritten by the next retry.
    internal static DateTimeOffset EligibleAt(IntegrationJob job) => job.AvailableAt > job.CreatedAt ? job.AvailableAt : job.CreatedAt;

    // The lock group the lease's busy check compares for this job: its stored group, else its job type's group when the type
    // is a processor type (the SQL's unnest map), else none.
    internal static string? BusyLockGroupOf(IntegrationJob job) =>
        job.LockGroup ?? (LockGroupJobTypes.Contains(job.JobType, StringComparer.Ordinal) ? MarketplaceSyncExecutionLock.GroupFor(job.JobType) : null);

    // The worker's wait attribution, guarded: a failure to attribute is recorded as unknown, never as a failed lease.
    internal static JobWaitBreakdown AttemptWaits(JobAttemptStart? attemptStart, JobWaitQuery query)
    {
        if (attemptStart?.Waits is not { } waits) return JobWaitBreakdown.Unknown;
        try { return waits(query) ?? JobWaitBreakdown.Unknown; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return JobWaitBreakdown.Unknown; }
    }

    internal const int CandidateLimit = 50;
    // With connection fairness the candidates are taken per connection (and per tenant for jobs without one).
    internal const int FairCandidateLimit = 20;

    // How long the job has been runnable: since it became available (or was created, whichever is later).
    internal static TimeSpan QueueWait(IntegrationJob job, DateTimeOffset now)
    {
        var runnableSince = job.AvailableAt > job.CreatedAt ? job.AvailableAt : job.CreatedAt;
        return now > runnableSince ? now - runnableSince : TimeSpan.Zero;
    }

    // The lock group a job runs under: its stored group, otherwise its job type's group.
    internal static string LockGroupOf(IntegrationJob job) =>
        string.IsNullOrWhiteSpace(job.LockGroup) ? MarketplaceSyncExecutionLock.GroupFor(job.JobType) : job.LockGroup;

    // How long a quota backoff a result asks for holds its connection and lock group: its Retry-After, else 30 s, at most
    // 15 minutes (the worker's wait attribution reads the same window).
    internal static TimeSpan ConnectionBackoffWait(JobExecutionResult result)
    {
        var wait = result.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(30);
        return wait > TimeSpan.FromMinutes(15) ? TimeSpan.FromMinutes(15) : wait;
    }

    private async Task RecordConnectionBackoffAsync(Guid connectionId, string lockGroup, DateTimeOffset now, JobExecutionResult result, CancellationToken cancellationToken)
    {
        var until = now.Add(ConnectionBackoffWait(result));
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
        JobStateMachine.MoveTo(job, JobStatus.RetryScheduled);
        job.AvailableAt = now;
        job.LastErrorCode = "WORKER_SHUTDOWN";
        job.LastErrorSummary = "Sunucu yeniden başlatıldı; işlem sıraya geri alındı ve kendiliğinden devam edecek.";
        job.LeaseTokenHash = null;
        job.LeaseExpiresAt = null;
        job.HeartbeatAt = null;
        job.Version++;
        AddOutboxEvent(job, now);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteAsync(Guid jobId, string leaseToken, JobExecutionResult result, CancellationToken cancellationToken, JobAttemptRun? run = null)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.Status != JobStatus.Leased || job.LeaseTokenHash is null || !hasher.Verify(leaseToken, job.LeaseTokenHash)) return false;
        var now = timeProvider.GetUtcNow();
        if (job.LeaseExpiresAt <= now) return false;
        // Consecutive write conflicts are counted on the job: only the first few version conflicts go without an
        // attempt, and a duplicate insert that keeps coming back goes to review.
        (result, job.ConflictCount) = MarketplaceHub.Infrastructure.Jobs.JobConflictPolicy.Apply(result, job.ConflictCount);
        if (result.BackOffConnection && job.ConnectionId is { } backoffConnectionId)
            await RecordConnectionBackoffAsync(backoffConnectionId, LockGroupOf(job), now, result, cancellationToken);

        // A deferred job never ran, so its attempt is refunded. Jobs deferred for over a day fall
        // back to normal attempt accounting so a permanently busy lock cannot loop forever. A finished
        // slice of a long job (Continue) is progress, not waiting: a large import or a 90-day
        // reconciliation may take more than a day of slices and must not die of its age.
        if (RefundsAttempt(result, AllowanceAge(job, now)))
        {
            var deferredAttempt = await db.JobAttempts.SingleOrDefaultAsync(x => x.JobId == jobId && x.AttemptNumber == job.AttemptCount, cancellationToken);
            if (deferredAttempt is not null) db.JobAttempts.Remove(deferredAttempt);
            job.AttemptCount = Math.Max(0, job.AttemptCount - 1);
            JobStateMachine.MoveTo(job, JobStatus.RetryScheduled);
            var requested = result.RetryAfter is { } delay && delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(30);
            job.AvailableAt = now.Add(result.Continues ? requested : DeferralDelay(requested, job.Id, job.Version));
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
        // A waiting check (the marketplace is still processing what it accepted) is not a failed try: while the job is
        // young it gives the attempt back by raising the limit, so a long batch or approval is not killed by its own
        // polls. Its attempt row stays, as the record of the check.
        var waiting = JobWaitingPolicy.IsWaiting(result);
        if (waiting && AllowanceAge(job, now) < JobWaitingPolicy.FreeWaitingWindow(job.JobType))
        {
            job.MaxAttempts++;
            job.WaitingChecks++;
        }
        if (kind == JobCompletionKind.Retry && job.AttemptCount >= job.MaxAttempts) kind = JobCompletionKind.Dead;
        JobStateMachine.MoveTo(job, kind switch
        {
            JobCompletionKind.Succeeded => JobStatus.Succeeded,
            JobCompletionKind.Retry => JobStatus.RetryScheduled,
            JobCompletionKind.Blocked when CancelsWhenSuperseded(result.ErrorCode) => JobStatus.Cancelled,
            JobCompletionKind.Blocked => JobStatus.Blocked,
            JobCompletionKind.ManualReview => JobStatus.ManualReview,
            _ => JobStatus.Dead
        });
        job.CompletedAt = kind == JobCompletionKind.Retry ? null : now;
        job.AvailableAt = kind == JobCompletionKind.Retry
            ? now.Add(waiting ? WaitingRetryDelay(job, result) : EffectiveRetryDelay(job, result))
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
        // Queue phase 0: the run's rate-limit token waits, written with the completion (null when the caller measured none).
        if (run is not null)
        {
            attempt.QuotaWaitMs = run.QuotaWaitMs;
            attempt.QuotaWaits = run.QuotaWaits;
        }
        if (kind == JobCompletionKind.Dead && result.Kind == JobCompletionKind.Retry)
        {
            job.LastErrorCode = attempt.ErrorCode;
            job.LastErrorSummary ??= "Maksimum otomatik deneme sayısına ulaşıldı.";
        }

        AddOutboxEvent(job, now);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Jobs deferred at the same instant (simultaneous webhooks of one package losing the same lock) used to wake at
    // the same instant and collide again, one getting through per round (audit 2026-10-24 Q1). A deferral is spread
    // over up to 30 % more than asked, at least 3 s and at most 5 minutes more, never less: a quota or Retry-After
    // wait is a minimum. The spread comes from a hash of the job id and its version, which changes with every lease,
    // so the same jobs do not keep landing in the same order. A slice of a long job keeps its own short pause.
    // A product update, archive or approval check that stopped before any call because a newer job of the same product
    // carries a newer payload ends cancelled, not blocked: the newer job is the product's work and its result, nothing is
    // left for anyone to settle (scan 23 Nov 2026: seven PRODUCT_UPDATE jobs of 14 Nov stayed BLOCKED). The code stays on
    // the job, so the panel still says the newer update replaced it.
    internal static readonly IReadOnlySet<string> SupersededStopCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PRODUCT_UPDATE_SUPERSEDED", "PRODUCT_ARCHIVE_SUPERSEDED", "PRODUCT_APPROVAL_SUPERSEDED"
    };

    internal static bool CancelsWhenSuperseded(string? errorCode) => errorCode is not null && SupersededStopCodes.Contains(errorCode);

    internal static TimeSpan DeferralDelay(TimeSpan requested, Guid jobId, long version)
    {
        var spread = TimeSpan.FromTicks(requested.Ticks * 3 / 10);
        if (spread < MinimumDeferralSpread) spread = MinimumDeferralSpread;
        if (spread > MaximumDeferralSpread) spread = MaximumDeferralSpread;
        var hash = 2166136261u;
        foreach (var value in jobId.ToByteArray()) hash = (hash ^ value) * 16777619u;
        foreach (var value in BitConverter.GetBytes(version)) hash = (hash ^ value) * 16777619u;
        return requested + TimeSpan.FromTicks(spread.Ticks * (hash % 1001) / 1000);
    }

    private static readonly TimeSpan MinimumDeferralSpread = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumDeferralSpread = TimeSpan.FromMinutes(5);

    // The age the deferral and waiting allowances are measured by: since the job was created, or since an operator
    // last retried it by hand, whichever is later.
    internal static TimeSpan AllowanceAge(IntegrationJob job, DateTimeOffset now) =>
        now - (job.RetriedAt is { } retried && retried > job.CreatedAt ? retried : job.CreatedAt);

    internal static bool RefundsAttempt(JobExecutionResult result, TimeSpan jobAge) =>
        result.Kind == JobCompletionKind.Retry && result.RefundAttempt && (result.Continues || jobAge < MaximumDeferralAge);

    // A waiting check runs again when its processor asked, not on the failure backoff (which grew a 15-second batch
    // poll to minutes after three checks). An approval check keeps its own cadence below; every other wait slows down
    // only after a few checks (JobWaitingPolicy.Delay), and never waits longer than an hour.
    internal static TimeSpan WaitingRetryDelay(IntegrationJob job, JobExecutionResult result)
    {
        if (job.JobType == MarketplaceJobTypes.ProductApprovalReconcile || job.JobType is InvoicingJobTypes.MarketplaceDelivery or InvoicingJobTypes.ManualOrderInvoiceDelivery)
            return EffectiveRetryDelay(job, result);
        var delay = JobWaitingPolicy.Delay(result.RetryAfter, job.WaitingChecks);
        return delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay;
    }

    internal static TimeSpan EffectiveRetryDelay(IntegrationJob job, JobExecutionResult result)
    {
        var requested = result.RetryAfter;
        if (job.JobType is InvoicingJobTypes.MarketplaceDelivery or InvoicingJobTypes.ManualOrderInvoiceDelivery)
            return InvoiceDeliveryRetryPolicy.DelayAfterAttempt(job.AttemptCount, requested);

        // Product approval is a bounded, read-only reconciliation poll. Its processor
        // asks for its own backoff (five minutes, then 15, hourly and six-hourly, see
        // ProductApprovalReconciliationPolicy.PendingRecheckDelay); applying the generic terminal one-hour
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
            JobStateMachine.MoveTo(job, JobStatus.Dead);
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
            if (job.AttemptCount >= job.MaxAttempts) { JobStateMachine.MoveTo(job, JobStatus.Dead); job.CompletedAt = now; }
            else { JobStateMachine.MoveTo(job, JobStatus.RetryScheduled); job.AvailableAt = now.Add(JobRetryPolicy.DelayAfterAttempt(job.AttemptCount, job.Id)); }
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

    internal const string JobEventResource = JobStateMachine.JobEventResource;

    private IntegrationOutboxEvent AddOutboxEvent(IntegrationJob job, DateTimeOffset now) => JobStateMachine.Publish(db, job, now);

    // The "jobs" event for a job's state change (JobStateMachine.NewJobEvent), kept here for the writers that name it.
    internal static IntegrationOutboxEvent NewJobEvent(IntegrationJob job, DateTimeOffset now) => JobStateMachine.NewJobEvent(job, now);
}
