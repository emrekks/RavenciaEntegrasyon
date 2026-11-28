using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Persistence;

// Read-only queue health: waits per worker lane, how full each marketplace quota runs and how fast orders reach the panel.
public sealed class QueueHealthService(AppDbContext db, IOptions<TrendyolOptions> trendyolOptions, TimeProvider timeProvider, IConfiguration? configuration = null,
    IOptions<MarketplaceHub.Infrastructure.Adapters.N11.N11Options>? n11Options = null, IOptions<MarketplaceHub.Infrastructure.Adapters.Pazarama.PazaramaOptions>? pazaramaOptions = null,
    IOptions<MarketplaceHub.Infrastructure.Adapters.PttAvm.PttAvmOptions>? pttAvmOptions = null) : IQueueHealthService
{
    private readonly QuotaBucketCatalog buckets = new(trendyolOptions.Value, n11Options?.Value ?? new(), pazaramaOptions?.Value ?? new(), pttAvmOptions?.Value ?? new());

    // The worker's hot lane ceiling (Worker:HotPriorityCeiling, read the same way as the worker), so the lanes shown
    // here are the ones the worker leases by.
    private readonly int hotPriorityCeiling = Math.Clamp(configuration?.GetValue("Worker:HotPriorityCeiling", WorkerLanes.DefaultHotPriorityCeiling) ?? WorkerLanes.DefaultHotPriorityCeiling, 0, 5);

    private static readonly string[] LockGroupJobTypes = [.. MarketplaceJobTypes.ProcessorJobTypes];
    private static readonly string[] LockGroups = [.. MarketplaceJobTypes.ProcessorJobTypes.Select(MarketplaceSyncExecutionLock.GroupFor)];

    public async Task<IReadOnlyList<LaneMetricView>> LanesAsync(Guid tenantId, TimeSpan window, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var since = now - window;
        // Queue phase 2: the dispatcher's work-type lanes when the worker runs it (Worker:LaneModel=dispatcher, shipped in the
        // api's settings with the worker's), else the earlier lanes.
        var dispatcherModel = WorkerLaneModel.IsDispatcher(configuration);
        var dispatcherSettings = DispatcherSettings.From(configuration);
        var lanes = WorkerLaneModel.Lanes(configuration, hotPriorityCeiling);
        string LaneOf(string jobType, int priority) => WorkerLaneModel.LaneFor(configuration, jobType, priority, hotPriorityCeiling);
        // The lanes are the worker's own (WorkerLanes): a job belongs to the first lane that would lease it.
        var lane = LaneCaseSql(WorkerLaneModel.ClassifyingLanes(lanes), out var laneParameters);
        var parameters = new List<object>
        {
            new Npgsql.NpgsqlParameter("tenant", tenantId),
            new Npgsql.NpgsqlParameter("since", since)
        };
        parameters.AddRange(laneParameters);
        // The lane expression is built from the lane definitions only; every value in it is a parameter.
        var sql = "SELECT " + lane + """
             AS "Lane",
                   count(*)::int AS "Attempts",
                   avg(wait.seconds) AS "AverageWaitSeconds",
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY wait.seconds) AS "P95WaitSeconds",
                   count(*) FILTER (WHERE causes.known)::int AS "CauseAttempts",
                   coalesce(sum(causes.wait_ms) FILTER (WHERE causes.known), 0)::double precision / 1000 AS "CauseWaitSeconds",
                   sum(attempt."WaitLockMs") FILTER (WHERE causes.known)::double precision / 1000 AS "LockSeconds",
                   sum(attempt."WaitBackoffMs") FILTER (WHERE causes.known)::double precision / 1000 AS "BackoffSeconds",
                   sum(attempt."WaitQuotaHoldMs") FILTER (WHERE causes.known)::double precision / 1000 AS "QuotaHoldSeconds",
                   sum(attempt."WaitSlotMs") FILTER (WHERE causes.known)::double precision / 1000 AS "SlotSeconds"
            FROM integration.job_attempts attempt
            JOIN integration.jobs job ON job."Id" = attempt."JobId"
            -- Queue phase 0: an attempt that recorded when its job became runnable (EligibleAt) is measured from it; an older
            -- row keeps the earlier rule (a first attempt of a job that never retried, from the job's AvailableAt).
            CROSS JOIN LATERAL (SELECT CASE
                WHEN attempt."AttemptNumber" = 1 AND attempt."EligibleAt" IS NOT NULL
                    THEN greatest(0, EXTRACT(EPOCH FROM attempt."StartedAt" - attempt."EligibleAt"))::double precision
                WHEN attempt."AttemptNumber" = 1 AND attempt."EligibleAt" IS NULL AND job."AttemptCount" <= 1 AND job."AvailableAt" <= attempt."StartedAt"
                    THEN EXTRACT(EPOCH FROM attempt."StartedAt" - job."AvailableAt")::double precision END AS seconds) wait
            -- Why the lane's jobs waited: the same first attempts the average and p95 measure, those whose causes were recorded
            -- (the lock figure is null only when none is known). A reason no attempt carries a value for stays null (unknown,
            -- e.g. the slot figure with legacy lanes); a reason one attempt left null leaves that attempt's time unknown, never
            -- in another reason.
            CROSS JOIN LATERAL (SELECT attempt."AttemptNumber" = 1 AND attempt."EligibleAt" IS NOT NULL AND attempt."WaitLockMs" IS NOT NULL AS known,
                greatest(0, EXTRACT(EPOCH FROM attempt."StartedAt" - attempt."EligibleAt") * 1000)::double precision AS wait_ms) causes
            WHERE attempt."TenantId" = @tenant AND attempt."StartedAt" >= @since
            GROUP BY 1
            """;
        var attempts = await db.Database.SqlQueryRaw<LaneAttemptRow>(sql, [.. parameters]).ToListAsync(cancellationToken);
        var queue = await RunnableDepthAsync(db, tenantId, now, cancellationToken);
        // Worker lanes step 1: each lane against its wait target now, and its open alert. The same snapshot, window and targets
        // as the worker's alert (LaneWaitMonitor, LaneWaitTargets), so the panel and the alert agree.
        var monitor = new LaneWaitMonitor(db, timeProvider, configuration);
        var waits = await monitor.SnapshotAsync(tenantId, LaneWaitTargets.Window(configuration), cancellationToken);
        var alerts = await monitor.OpenAlertsAsync(tenantId, cancellationToken);
        // Worker lanes step 3: borrowed slots exist only with the flexible lane model (Worker:LaneModel, which the api reads
        // like the worker does: set it on both). Legacy: null, never a count. Flexible: the lane's running jobs over every
        // tenant beyond its own slots, each job counted in the lane that leases it first (WorkerLanes.LaneFor); a job another
        // lane leased (overlapping lanes, a lease from before a restart) can move one count between lanes, never add one.
        var flexibleLanes = WorkerLaneModel.IsFlexible(configuration);
        var running = flexibleLanes
            ? await db.IntegrationJobs.AsNoTracking()
                .Where(job => job.Status == Domain.JobStatus.Leased && job.LeaseExpiresAt > now)
                .GroupBy(job => new { job.JobType, job.Priority })
                .Select(group => new RunningRow(group.Key.JobType, group.Key.Priority, group.Count()))
                .ToListAsync(cancellationToken)
            : null;
        // Each lane's queued and running jobs per marketplace (the tenant's connections; a job without one counts under "").
        var platforms = await db.PlatformConnections.AsNoTracking().Where(connection => connection.TenantId == tenantId)
            .Select(connection => new { connection.Id, connection.PlatformCode }).ToDictionaryAsync(connection => connection.Id, connection => connection.PlatformCode, cancellationToken);
        var model = dispatcherModel ? WorkerLaneModel.Dispatcher : flexibleLanes ? WorkerLaneModel.Flexible : WorkerLaneModel.Legacy;
        return lanes.Select(definition =>
        {
            var code = LaneCode(definition.Name);
            var attempt = attempts.FirstOrDefault(row => row.Lane == definition.Name);
            var depth = queue.Where(row => LaneOf(row.JobType, row.Priority) == definition.Name).ToArray();
            var wait = waits.FirstOrDefault(row => row.Lane == definition.Name);
            var alertSince = alerts.TryGetValue(definition.Name, out var since) ? since : (DateTimeOffset?)null;
            var status = alertSince is not null ? LaneWaitStatus.Alert : wait?.Breached == true ? LaneWaitStatus.OverTarget : LaneWaitStatus.Ok;
            var slots = dispatcherModel ? dispatcherSettings.SlotsOf(definition.Name) : LaneSlots(definition);
            var byPlatform = depth.GroupBy(row => row.ConnectionId is { } connection && platforms.TryGetValue(connection, out var platform) ? platform : "", StringComparer.Ordinal)
                .Select(group => new LanePlatformView(group.Key, group.Where(row => !row.Running).Sum(row => row.Count), group.Where(row => row.Running).Sum(row => row.Count)))
                .OrderBy(view => PlatformRank(view.PlatformCode)).ThenBy(view => view.PlatformCode, StringComparer.Ordinal).ToList();
            // Borrowed: the lane's running jobs (every tenant) beyond its own slots; under the dispatcher beyond its reserved
            // slots and share (outside its share, the same thing job_attempts.Borrowed records).
            return new LaneMetricView(code, attempt?.Attempts ?? 0, Round(attempt?.AverageWaitSeconds), Round(attempt?.P95WaitSeconds), depth.Where(row => !row.Running).Sum(row => row.Count), depth.Where(row => row.Running).Sum(row => row.Count), slots,
                LaneWaitTargets.For(configuration, definition.Name).TotalSeconds, wait?.RecentP95WaitSeconds, wait?.OldestWaitSeconds, status, alertSince,
                running is null ? null : Math.Max(0, running.Where(row => LaneOf(row.JobType, row.Priority) == definition.Name).Sum(row => row.Count) - slots),
                Causes(attempt),
                dispatcherModel ? dispatcherSettings.ReservedOf(definition.Name) : null,
                dispatcherModel ? dispatcherSettings.ShareOf(definition.Name) : null,
                byPlatform,
                model);
        }).ToList();
    }

    // The lane's recorded wait causes (null while no attempt recorded them). What the known causes do not cover is unknown.
    private static LaneWaitCausesView? Causes(LaneAttemptRow? row)
    {
        if (row is not { CauseAttempts: > 0 }) return null;
        var known = (row.LockSeconds ?? 0) + (row.BackoffSeconds ?? 0) + (row.QuotaHoldSeconds ?? 0) + (row.SlotSeconds ?? 0);
        return new LaneWaitCausesView(row.CauseAttempts, Math.Round(row.CauseWaitSeconds, 1), Round(row.LockSeconds), Round(row.BackoffSeconds),
            Round(row.QuotaHoldSeconds), Round(row.SlotSeconds), Math.Round(Math.Max(0, row.CauseWaitSeconds - known), 1));
    }

    // Running jobs and the queued jobs a slot could take now, per job type, priority and connection. A runnable
    // job whose connection lock group is busy or in a quota backoff waits for that, not for a slot, so it is not
    // queue depth. The per-type metrics use the same rows.
    internal static Task<List<RunnableDepthRow>> RunnableDepthAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<RunnableDepthRow>($"""
            WITH busy AS (
                SELECT DISTINCT running."ConnectionId", coalesce(running."LockGroup", running_group.lock_group) AS lock_group
                FROM integration.jobs running
                LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS running_group(job_type, lock_group)
                  ON running_group.job_type = running."JobType"
                WHERE running."Status" = 'LEASED' AND running."LeaseExpiresAt" > {now} AND running."ConnectionId" IS NOT NULL)
            SELECT job."JobType" AS "JobType", job."Priority" AS "Priority", job."ConnectionId" AS "ConnectionId", (job."Status" = 'LEASED') AS "Running", count(*)::int AS "Count"
            FROM integration.jobs job
            LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS job_group(job_type, lock_group)
              ON job_group.job_type = job."JobType"
            WHERE job."TenantId" = {tenantId}
              AND (job."Status" = 'LEASED'
                OR (job."Status" IN ('PENDING', 'RETRY_SCHEDULED') AND job."AvailableAt" <= {now} AND job."AttemptCount" < job."MaxAttempts"
                  AND NOT EXISTS (SELECT 1 FROM busy WHERE busy."ConnectionId" = job."ConnectionId" AND busy.lock_group = coalesce(job."LockGroup", job_group.lock_group))
                  AND NOT EXISTS (SELECT 1 FROM integration.connection_backoffs backoff
                      WHERE backoff."ConnectionId" = job."ConnectionId" AND backoff."LockGroup" = coalesce(job."LockGroup", job_group.lock_group) AND backoff."Until" > {now})))
            GROUP BY 1, 2, 3, 4
            """).ToListAsync(cancellationToken);

    internal sealed record RunnableDepthRow(string JobType, int Priority, Guid? ConnectionId, bool Running, int Count);

    // Queue phase 2, the dispatcher's census (LaneDispatcher): the type and priority of every queued job, across every tenant,
    // that a lease could take by time. A connection in a quota backoff waits for its end, which the due-time wake announces; a
    // busy lock group, a quota hold or the per-store cap still count (the lease tells those apart, and a job's end retries them).
    // Review of 27 Nov: each kind is listed per connection with HeldByStore when only its own store holds it now, by the same
    // rules the lease refuses for (a running job of the connection in its lock group, the per-store cap when cap names its
    // type, an order read of the connection queued ahead of a status scan when deferral is passed), so a lane held by a store
    // waits for that store's job end instead of retrying on every job end anywhere.
    public static async Task<IReadOnlyList<JobKindRow>> RunnableJobKindsAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken,
        JobConnectionCap? cap = null, IReadOnlyList<JobConnectionCap.Rule>? deferredRules = null, JobDeferral? deferral = null, JobBulkhead? bulkhead = null)
    {
        // A bulkhead class of cap 1 (the default) held by the store running the class's job for the tenant and marketplace; a
        // class of a larger cap is left to the lease (counted as free here).
        var singleClasses = bulkhead is null ? [] : bulkhead.ClassNames.Where((_, index) => bulkhead.ClassCaps[index] == 1).ToHashSet(StringComparer.Ordinal);
        var bulkPairs = bulkhead is null ? [] : bulkhead.JobTypes.Zip(bulkhead.Classes).Where(pair => singleClasses.Contains(pair.Second)).ToList();
        var bulkOn = bulkPairs.Count > 0;
        var bulkJobTypes = bulkPairs.Select(pair => pair.First).ToArray();
        var bulkClasses = bulkPairs.Select(pair => pair.Second).ToArray();
        var deferMinimums = (deferredRules ?? []).Select(rule => rule.MinimumPriority ?? int.MinValue).ToArray();
        var deferMaximums = (deferredRules ?? []).Select(rule => rule.MaximumPriority ?? int.MaxValue).ToArray();
        var deferIncluded = (deferredRules ?? []).Select(rule => rule.IncludedJobTypes.Count == 0 ? "" : "|" + string.Join('|', rule.IncludedJobTypes) + "|").ToArray();
        var deferExcluded = (deferredRules ?? []).Select(rule => "|" + string.Join('|', rule.ExcludedJobTypes) + "|").ToArray();
        var bulkMinimum = bulkhead?.ClassMinimumPriority ?? 0;
        var bulkMaximum = bulkhead?.ClassMaximumPriority ?? 0;
        now = now.ToUniversalTime();
        var capOn = cap is { Cap: > 0 };
        var capCount = cap?.Cap ?? 0;
        var rules = cap?.Rules ?? [];
        var capMinimums = rules.Select(rule => rule.MinimumPriority ?? int.MinValue).ToArray();
        var capMaximums = rules.Select(rule => rule.MaximumPriority ?? int.MaxValue).ToArray();
        var capIncluded = rules.Select(rule => rule.IncludedJobTypes.Count == 0 ? "" : "|" + string.Join('|', rule.IncludedJobTypes) + "|").ToArray();
        var capExcluded = rules.Select(rule => "|" + string.Join('|', rule.ExcludedJobTypes) + "|").ToArray();
        var ahead = deferral?.AheadJobTypes.ToArray() ?? [];
        var aheadMinimum = deferral?.MinimumAheadPriority ?? 0;
        var aheadMaximum = deferral?.MaximumAheadPriority ?? 0;
        return await db.Database.SqlQuery<JobKindRow>($"""
            WITH busy AS (
                SELECT DISTINCT running."ConnectionId", coalesce(running."LockGroup", running_group.lock_group) AS lock_group
                FROM integration.jobs running
                LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS running_group(job_type, lock_group)
                  ON running_group.job_type = running."JobType"
                WHERE running."Status" = 'LEASED' AND running."LeaseExpiresAt" > {now} AND running."ConnectionId" IS NOT NULL),
            capped_load AS (
                SELECT running."ConnectionId", count(*) AS running_count
                FROM integration.jobs running
                WHERE {capOn} AND running."Status" = 'LEASED' AND running."LeaseExpiresAt" > {now} AND running."ConnectionId" IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM unnest({capMinimums}::int[], {capMaximums}::int[], {capIncluded}::text[], {capExcluded}::text[]) AS rule(minimum, maximum, included, excluded)
                      WHERE running."Priority" BETWEEN rule.minimum AND rule.maximum
                        AND (rule.included = '' OR strpos(rule.included, '|' || running."JobType" || '|') > 0)
                        AND strpos(rule.excluded, '|' || running."JobType" || '|') = 0)
                GROUP BY running."ConnectionId"),
            runnable AS MATERIALIZED (
                SELECT job."Id", job."JobType", job."Priority", job."ConnectionId", coalesce(job."LockGroup", job_group.lock_group, '') AS lock_group,
                       job."TenantId", job_connection."PlatformCode" AS platform
                FROM integration.jobs job
                LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS job_group(job_type, lock_group)
                  ON job_group.job_type = job."JobType"
                LEFT JOIN integration.platform_connections job_connection ON job_connection."Id" = job."ConnectionId"
                WHERE job."Status" IN ('PENDING', 'RETRY_SCHEDULED')
                  AND job."AvailableAt" <= {now}
                  AND job."AttemptCount" < job."MaxAttempts"
                  AND NOT EXISTS (
                      SELECT 1 FROM integration.connection_backoffs backoff
                      WHERE backoff."ConnectionId" = job."ConnectionId"
                        AND backoff."LockGroup" = coalesce(job."LockGroup", job_group.lock_group)
                        AND backoff."Until" > {now})),
            -- Each connection and lock group with a job queued ahead that the lease could take now (JobDeferral: runnable and
            -- not backed off, an ahead type in the priority range, not held by the per-store cap), read once. MATERIALIZED and
            -- an uncorrelated IN below: a CTE referenced once is otherwise inlined and recomputed for every runnable row.
            deferring_pairs AS MATERIALIZED (
                SELECT DISTINCT ahead_job."ConnectionId", ahead_job.lock_group
                FROM runnable ahead_job
                WHERE ahead_job."ConnectionId" IS NOT NULL
                  AND ahead_job."JobType" = ANY({ahead}::text[])
                  AND ahead_job."Priority" BETWEEN {aheadMinimum} AND {aheadMaximum}
                  AND NOT ({capOn}
                      AND EXISTS (
                          SELECT 1 FROM unnest({capMinimums}::int[], {capMaximums}::int[], {capIncluded}::text[], {capExcluded}::text[]) AS ahead_rule(minimum, maximum, included, excluded)
                          WHERE ahead_job."Priority" BETWEEN ahead_rule.minimum AND ahead_rule.maximum
                            AND (ahead_rule.included = '' OR strpos(ahead_rule.included, '|' || ahead_job."JobType" || '|') > 0)
                            AND strpos(ahead_rule.excluded, '|' || ahead_job."JobType" || '|') = 0)
                      AND {capCount} <= coalesce((SELECT capped_load.running_count FROM capped_load WHERE capped_load."ConnectionId" = ahead_job."ConnectionId"), 0))),
            -- The bulkhead classes of cap 1 held now, per tenant and marketplace, with the store running the class's job: read
            -- once (a per-row join of the running jobs and the connections took most of a census of 40 000 rows, 28 Nov 2026).
            class_holders AS MATERIALIZED (
                SELECT class_running."TenantId", class_connection."PlatformCode" AS platform, running_class.class_name,
                       min(class_running."ConnectionId"::text)::uuid AS holder
                FROM integration.jobs class_running
                JOIN unnest({bulkJobTypes}::text[], {bulkClasses}::text[]) AS running_class(job_type, class_name) ON running_class.job_type = class_running."JobType"
                JOIN integration.platform_connections class_connection ON class_connection."Id" = class_running."ConnectionId"
                WHERE {bulkOn}
                  AND class_running."Status" = 'LEASED' AND class_running."LeaseExpiresAt" > {now}
                  AND class_running."Priority" BETWEEN {bulkMinimum} AND {bulkMaximum}
                GROUP BY class_running."TenantId", class_connection."PlatformCode", running_class.class_name)
            SELECT held."JobType" AS "JobType", held."Priority" AS "Priority", held."ConnectionId" AS "ConnectionId", held.held_by AS "HeldBy"
            FROM runnable
            CROSS JOIN LATERAL (
                SELECT runnable."JobType", runnable."Priority", runnable."ConnectionId",
                    CASE
                        WHEN runnable."ConnectionId" IS NULL THEN NULL
                        WHEN EXISTS (SELECT 1 FROM busy WHERE busy."ConnectionId" = runnable."ConnectionId" AND busy.lock_group = runnable.lock_group)
                          OR ({capOn}
                              AND EXISTS (
                                  SELECT 1 FROM unnest({capMinimums}::int[], {capMaximums}::int[], {capIncluded}::text[], {capExcluded}::text[]) AS rule(minimum, maximum, included, excluded)
                                  WHERE runnable."Priority" BETWEEN rule.minimum AND rule.maximum
                                    AND (rule.included = '' OR strpos(rule.included, '|' || runnable."JobType" || '|') > 0)
                                    AND strpos(rule.excluded, '|' || runnable."JobType" || '|') = 0)
                              AND {capCount} <= coalesce((SELECT capped_load.running_count FROM capped_load WHERE capped_load."ConnectionId" = runnable."ConnectionId"), 0))
                          OR (EXISTS (
                                  SELECT 1 FROM unnest({deferMinimums}::int[], {deferMaximums}::int[], {deferIncluded}::text[], {deferExcluded}::text[]) AS rule(minimum, maximum, included, excluded)
                                  WHERE runnable."Priority" BETWEEN rule.minimum AND rule.maximum
                                    AND (rule.included = '' OR strpos(rule.included, '|' || runnable."JobType" || '|') > 0)
                                    AND strpos(rule.excluded, '|' || runnable."JobType" || '|') = 0)
                              -- the same predicate as the lease's deferral (JobDeferral), read once (deferring_pairs)
                              AND (runnable."ConnectionId", runnable.lock_group) IN (SELECT deferring_pairs."ConnectionId", deferring_pairs.lock_group FROM deferring_pairs))
                          THEN runnable."ConnectionId"
                        -- A bulkhead class at its cap for the tenant and marketplace: held by the store running the class's job.
                        ELSE (SELECT min(class_holders.holder::text)::uuid
                              FROM unnest({bulkJobTypes}::text[], {bulkClasses}::text[]) AS candidate_class(job_type, class_name)
                              JOIN class_holders ON class_holders.class_name = candidate_class.class_name
                              WHERE {bulkOn} AND runnable."Priority" BETWEEN {bulkMinimum} AND {bulkMaximum}
                                AND candidate_class.job_type = runnable."JobType"
                                AND class_holders."TenantId" = runnable."TenantId"
                                AND class_holders.platform = runnable.platform)
                    END AS held_by) held
            GROUP BY held."JobType", held."Priority", held."ConnectionId", held.held_by
            """).ToListAsync(cancellationToken);
    }

    // HeldBy: the store whose running job alone keeps these runnable jobs of the kind and connection waiting (its lock group, the
    // store cap, its queued order read ahead, or a bulkhead class at its cap); null: free for a lease now.
    public sealed record JobKindRow(string JobType, int Priority, Guid? ConnectionId, Guid? HeldBy);

    // The oldest AvailableAt among queued jobs a slot could take now, across every tenant, for /health/queues. Same
    // rule as RunnableDepthAsync (keep the two in step): a job waiting for its connection's busy lock group or a quota
    // backoff is waiting by design, so it does not make the queue look stuck.
    public static async Task<DateTimeOffset?> OldestRunnableAvailableAtAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<OldestRunnableRow>($"""
            WITH busy AS (
                SELECT DISTINCT running."ConnectionId", coalesce(running."LockGroup", running_group.lock_group) AS lock_group
                FROM integration.jobs running
                LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS running_group(job_type, lock_group)
                  ON running_group.job_type = running."JobType"
                WHERE running."Status" = 'LEASED' AND running."LeaseExpiresAt" > {now} AND running."ConnectionId" IS NOT NULL)
            SELECT min(job."AvailableAt") AS "OldestAvailableAt"
            FROM integration.jobs job
            LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS job_group(job_type, lock_group)
              ON job_group.job_type = job."JobType"
            WHERE job."Status" IN ('PENDING', 'RETRY_SCHEDULED') AND job."AvailableAt" <= {now} AND job."AttemptCount" < job."MaxAttempts"
              AND NOT EXISTS (SELECT 1 FROM busy WHERE busy."ConnectionId" = job."ConnectionId" AND busy.lock_group = coalesce(job."LockGroup", job_group.lock_group))
              AND NOT EXISTS (SELECT 1 FROM integration.connection_backoffs backoff
                  WHERE backoff."ConnectionId" = job."ConnectionId" AND backoff."LockGroup" = coalesce(job."LockGroup", job_group.lock_group) AND backoff."Until" > {now})
            """).ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0].OldestAvailableAt;
    }

    public sealed record OldestRunnableRow(DateTimeOffset? OldestAvailableAt);

    // The panel's lane codes; the four lanes it knew before keep their codes.
    internal static string LaneCode(string lane) => lane switch
    {
        "initial-data-sync" => "INITIAL",
        _ => lane.ToUpperInvariant().Replace('-', '_')
    };

    // A CASE expression giving each job's lane name, in the lanes' precedence order (WorkerLanes.LaneFor).
    internal static string LaneCaseSql(IReadOnlyList<WorkerLane> lanes, out List<object> parameters)
    {
        parameters = [];
        var builder = new System.Text.StringBuilder("CASE");
        for (var index = 0; index < lanes.Count; index++)
        {
            var lane = lanes[index];
            parameters.Add(new Npgsql.NpgsqlParameter($"lane{index}", lane.Name));
            parameters.Add(new Npgsql.NpgsqlParameter($"lane{index}_included", lane.IncludedJobTypes.ToArray()));
            parameters.Add(new Npgsql.NpgsqlParameter($"lane{index}_excluded", lane.ExcludedJobTypes.ToArray()));
            builder.Append(" WHEN ");
            if (lane.MaximumPriority is { } maximum) builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"job.\"Priority\" <= {maximum} AND ");
            if (lane.MinimumPriority is { } minimum) builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"job.\"Priority\" >= {minimum} AND ");
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"(cardinality(@lane{index}_included::text[]) = 0 OR job.\"JobType\" = ANY(@lane{index}_included::text[])) AND NOT (job.\"JobType\" = ANY(@lane{index}_excluded::text[])) THEN @lane{index}::text");
        }
        builder.Append(" ELSE 'unassigned' END");
        return builder.ToString();
    }

    // Queue phase 0: every quota bucket a connection of the tenant took tokens from (QuotaBucketOwners, seen in the last 30
    // days), with its last hour from the per-minute counts, in one grouped query (it used to be one LIKE query per Trendyol
    // connection and lane). The tenant scope comes from the connection: rate_limit_usage has no tenant column. A bucket every
    // account shares (Hepsiburada's catalogue, per server IP) counts every account's requests, as the quota does.
    public async Task<IReadOnlyList<QuotaUsageView>> QuotaAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var since = QuotaWindowStart(timeProvider.GetUtcNow());
        // Trendyol's seller buckets name the seller id (the connection's ExternalStoreId) and the API host, so they are known
        // without a recorded owner (review of 27 Nov 2026: right after the deploy the owner table is empty): those with
        // requests in the hour join the recorded ones. Keys are built and compared whole, never with LIKE.
        var options = trendyolOptions.Value;
        string[] hosts = [.. new[] { options.ProductionBaseAddress.Host, options.StageBaseAddress.Host }.Distinct(StringComparer.OrdinalIgnoreCase)];
        var rows = await db.Database.SqlQuery<QuotaRow>($"""
            WITH derived AS (
                SELECT 'trendyol:' || kind.bucket || ':' || host.name || ':' || connection."ExternalStoreId" || kind.suffix AS "Key", connection."Id" AS "ConnectionId"
                FROM integration.platform_connections connection
                CROSS JOIN unnest({hosts}::text[]) AS host(name)
                CROSS JOIN (VALUES ('orders', ':fg'), ('orders', ':bg'), ('orders', ':spacing'), ('orders', ':spacing:start'), ('finance', ''), ('claim-actions', '')) AS kind(bucket, suffix)
                WHERE connection."TenantId" = {tenantId} AND connection."PlatformCode" = 'TRENDYOL' AND connection."ExternalStoreId" <> ''),
            owners AS (
                SELECT recorded."Key", recorded."ConnectionId" FROM integration.rate_limit_bucket_connections recorded
                UNION
                SELECT derived."Key", derived."ConnectionId" FROM derived
                WHERE EXISTS (SELECT 1 FROM integration.rate_limit_usage used WHERE used."Key" = derived."Key" AND used."Minute" >= {since}))
            SELECT owner."ConnectionId" AS "ConnectionId", connection."DisplayName" AS "StoreName", owner."Key" AS "Key",
                   coalesce(sum(usage."Requests"), 0)::int AS "Requests", coalesce(max(usage."Requests"), 0)::int AS "Peak"
            FROM owners owner
            JOIN integration.platform_connections connection ON connection."Id" = owner."ConnectionId"
            LEFT JOIN integration.rate_limit_usage usage ON usage."Key" = owner."Key" AND usage."Minute" >= {since}
            WHERE connection."TenantId" = {tenantId}
            GROUP BY owner."ConnectionId", connection."DisplayName", owner."Key"
            """).ToListAsync(cancellationToken);
        // One row per bucket: a bucket two connections of the tenant share (one seller behind both) is listed once.
        return [.. rows.GroupBy(row => row.Key, StringComparer.Ordinal)
            .Select(group => (Bucket: buckets.Describe(group.Key), Rows: group.ToList()))
            .Where(item => item.Bucket is not null)
            .Select(item => new QuotaUsageView(item.Bucket!.PlatformCode, string.Join(", ", item.Rows.Select(row => row.StoreName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                item.Bucket.Code, item.Bucket.Label, item.Bucket.LimitPerMinute, item.Rows[0].Requests, item.Rows[0].Peak, Math.Round(item.Rows[0].Requests / 60.0, 1), item.Bucket.Shared, item.Bucket.BaseLimit))
            .OrderBy(view => PlatformRank(view.PlatformCode)).ThenBy(view => view.StoreName, StringComparer.Ordinal).ThenBy(view => view.Bucket, StringComparer.Ordinal)];
    }

    // The quota card's hour: minutes from one hour before now (the minute that holds now - 1 h counts as a whole).
    internal static DateTimeOffset QuotaWindowStart(DateTimeOffset now) => now.AddHours(-1).ToUniversalTime();

    internal static readonly string[] MarketplaceOrder = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY"];
    private static int PlatformRank(string platform) => Array.IndexOf(MarketplaceOrder, platform) is var index and >= 0 ? index : MarketplaceOrder.Length;

    // The targets the order block is judged by (plan of 27 Nov 2026): a delivered webhook is in the panel within 5 s (p95),
    // a new order without one within 3 minutes (p95). Amber up to twice the target.
    public const double WebhookProcessingTargetSeconds = 5;
    public const double PollingTargetSeconds = 180;

    // Queue phase 0: from the marketplace's order time (OrderedAt) to the order's first save (CreatedAt), for orders first
    // stored in the last 24 hours. Left out: orders the marketplace dated more than 7 days before their save (a history or
    // recovery read, not a new order) and orders a first import stored (its job runs at the initial-data priority). The source
    // is the job that stored it (Order.CreatedByJobId): a webhook delivery's job (whose creation is the delivery's receipt)
    // or any other job (a read); without one, the order counts in ALL only. Times below zero (clock skew) count as zero.
    public async Task<IReadOnlyList<OrderLatencyView>> OrderLatencyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var since = now.AddHours(-24);
        var rows = await db.Database.SqlQuery<LatencyRow>($"""
            WITH stored AS (
                SELECT connection."PlatformCode" AS platform,
                       CASE WHEN job."Id" IS NULL THEN NULL WHEN job."TriggerType" = 'webhook' THEN 'WEBHOOK' ELSE 'POLLING' END AS source,
                       greatest(0, EXTRACT(EPOCH FROM o."CreatedAt" - o."OrderedAt"))::double precision AS total,
                       CASE WHEN job."TriggerType" = 'webhook' THEN greatest(0, EXTRACT(EPOCH FROM job."CreatedAt" - o."OrderedAt"))::double precision END AS notice,
                       CASE WHEN job."TriggerType" = 'webhook' THEN greatest(0, EXTRACT(EPOCH FROM o."CreatedAt" - job."CreatedAt"))::double precision END AS processing
                FROM sales.orders o
                JOIN integration.platform_connections connection ON connection."Id" = o."ConnectionId" AND connection."TenantId" = o."TenantId"
                LEFT JOIN integration.jobs job ON job."Id" = o."CreatedByJobId" AND job."TenantId" = o."TenantId"
                WHERE o."TenantId" = {tenantId} AND o."CreatedAt" >= {since}
                  AND o."OrderedAt" >= o."CreatedAt" - interval '7 days'
                  AND (job."Id" IS NULL OR job."Priority" <> {JobPriorityPolicy.InitialDataSync}))
            SELECT platform AS "PlatformCode", CASE WHEN GROUPING(source) = 1 THEN 'ALL' ELSE source END AS "Source", count(*)::int AS "Orders",
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY total) AS "P50", percentile_cont(0.95) WITHIN GROUP (ORDER BY total) AS "P95",
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY notice) AS "NoticeP50", percentile_cont(0.95) WITHIN GROUP (ORDER BY notice) AS "NoticeP95",
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY processing) AS "ProcessingP50", percentile_cont(0.95) WITHIN GROUP (ORDER BY processing) AS "ProcessingP95"
            FROM stored
            GROUP BY GROUPING SETS ((platform, source), (platform))
            """).ToListAsync(cancellationToken);
        var platforms = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && MarketplaceOrder.Contains(x.PlatformCode))
            .Select(x => x.PlatformCode).Distinct().ToListAsync(cancellationToken);
        var result = new List<OrderLatencyView>();
        foreach (var platform in platforms.Concat(rows.Select(row => row.PlatformCode)).Distinct(StringComparer.Ordinal).OrderBy(PlatformRank))
        {
            var all = rows.FirstOrDefault(row => row.PlatformCode == platform && row.Source == "ALL");
            result.Add(all is null ? new OrderLatencyView(platform, "ALL", 0, null, null) : new OrderLatencyView(platform, "ALL", all.Orders, Round(all.P50), Round(all.P95)));
            foreach (var row in rows.Where(row => row.PlatformCode == platform && row.Source is "WEBHOOK" or "POLLING").OrderBy(row => row.Source == "WEBHOOK" ? 0 : 1))
            {
                var webhook = row.Source == "WEBHOOK";
                var target = webhook ? WebhookProcessingTargetSeconds : PollingTargetSeconds;
                var judged = webhook ? row.ProcessingP95 : row.P95;
                result.Add(new OrderLatencyView(platform, row.Source!, row.Orders, Round(row.P50), Round(row.P95),
                    webhook ? Round(row.NoticeP50) : null, webhook ? Round(row.NoticeP95) : null, webhook ? Round(row.ProcessingP50) : null, webhook ? Round(row.ProcessingP95) : null,
                    target, LatencyStatus(judged, target)));
            }
        }
        return result;
    }

    internal static string? LatencyStatus(double? p95, double target) =>
        p95 is not { } value ? null : value <= target ? "OK" : value <= target * 2 ? "WARN" : "BAD";

    // The same setting and bounds the worker starts its loops with (Worker.LaneSlots).
    private int LaneSlots(WorkerLane lane) => Math.Clamp(configuration?.GetValue($"Worker:LaneSlots:{lane.Name}", lane.DefaultSlots) ?? lane.DefaultSlots, 1, 8);

    private static double? Round(double? value) => value is { } number ? Math.Round(number, 1) : null;
    private sealed record LaneAttemptRow(string Lane, int Attempts, double? AverageWaitSeconds, double? P95WaitSeconds,
        int CauseAttempts, double CauseWaitSeconds, double? LockSeconds, double? BackoffSeconds, double? QuotaHoldSeconds, double? SlotSeconds);
    private sealed record QuotaRow(Guid ConnectionId, string StoreName, string Key, int Requests, int Peak);
    private sealed record LatencyRow(string PlatformCode, string? Source, int Orders, double? P50, double? P95, double? NoticeP50, double? NoticeP95, double? ProcessingP50, double? ProcessingP95);
    private sealed record RunningRow(string JobType, int Priority, int Count);
}
