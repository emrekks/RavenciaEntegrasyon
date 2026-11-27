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
        var lanes = WorkerLanes.All(hotPriorityCeiling);
        // The lanes are the worker's own (WorkerLanes): a job belongs to the first lane that would lease it.
        var lane = LaneCaseSql(lanes, out var laneParameters);
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
        return lanes.Select(definition =>
        {
            var code = LaneCode(definition.Name);
            var attempt = attempts.FirstOrDefault(row => row.Lane == definition.Name);
            var depth = queue.Where(row => WorkerLanes.LaneFor(row.JobType, row.Priority, hotPriorityCeiling) == definition.Name).ToArray();
            var wait = waits.FirstOrDefault(row => row.Lane == definition.Name);
            var alertSince = alerts.TryGetValue(definition.Name, out var since) ? since : (DateTimeOffset?)null;
            var status = alertSince is not null ? LaneWaitStatus.Alert : wait?.Breached == true ? LaneWaitStatus.OverTarget : LaneWaitStatus.Ok;
            return new LaneMetricView(code, attempt?.Attempts ?? 0, Round(attempt?.AverageWaitSeconds), Round(attempt?.P95WaitSeconds), depth.Where(row => !row.Running).Sum(row => row.Count), depth.Where(row => row.Running).Sum(row => row.Count), LaneSlots(definition),
                LaneWaitTargets.For(configuration, definition.Name).TotalSeconds, wait?.RecentP95WaitSeconds, wait?.OldestWaitSeconds, status, alertSince,
                running is null ? null : Math.Max(0, running.Where(row => WorkerLanes.LaneFor(row.JobType, row.Priority, hotPriorityCeiling) == definition.Name).Sum(row => row.Count) - LaneSlots(definition)),
                Causes(attempt));
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
