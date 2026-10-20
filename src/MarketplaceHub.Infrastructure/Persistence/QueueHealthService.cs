using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Persistence;

// Read-only queue health: waits per worker lane, and how full the Trendyol order quota runs.
public sealed class QueueHealthService(AppDbContext db, IOptions<TrendyolOptions> trendyolOptions, TimeProvider timeProvider, IConfiguration? configuration = null) : IQueueHealthService
{
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
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY wait.seconds) AS "P95WaitSeconds"
            FROM integration.job_attempts attempt
            JOIN integration.jobs job ON job."Id" = attempt."JobId"
            CROSS JOIN LATERAL (SELECT CASE WHEN attempt."AttemptNumber" = 1 AND job."AttemptCount" <= 1 AND job."AvailableAt" <= attempt."StartedAt"
                THEN EXTRACT(EPOCH FROM attempt."StartedAt" - job."AvailableAt")::double precision END AS seconds) wait
            WHERE attempt."TenantId" = @tenant AND attempt."StartedAt" >= @since
            GROUP BY 1
            """;
        var attempts = await db.Database.SqlQueryRaw<LaneAttemptRow>(sql, [.. parameters]).ToListAsync(cancellationToken);
        var queue = await RunnableDepthAsync(db, tenantId, now, cancellationToken);
        return lanes.Select(definition =>
        {
            var code = LaneCode(definition.Name);
            var attempt = attempts.FirstOrDefault(row => row.Lane == definition.Name);
            var depth = queue.Where(row => WorkerLanes.LaneFor(row.JobType, row.Priority, hotPriorityCeiling) == definition.Name).ToArray();
            return new LaneMetricView(code, attempt?.Attempts ?? 0, Round(attempt?.AverageWaitSeconds), Round(attempt?.P95WaitSeconds), depth.Where(row => !row.Running).Sum(row => row.Count), depth.Where(row => row.Running).Sum(row => row.Count));
        }).ToList();
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

    public async Task<IReadOnlyList<QuotaUsageView>> QuotaAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PlatformCode == "TRENDYOL" && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.ExternalStoreId != "")
            .Select(x => new { x.DisplayName, x.ExternalStoreId })
            .ToListAsync(cancellationToken);
        var options = trendyolOptions.Value;
        var (foreground, background) = TrendyolResilienceHandler.OrderQuotaSplit(options);
        var perMinute = 60 / Math.Max(1, options.OrderRequestInterval.TotalSeconds);
        var since = timeProvider.GetUtcNow().AddHours(-1);
        var result = new List<QuotaUsageView>();
        foreach (var connection in connections)
        {
            foreach (var (lane, suffix, limit) in new[] { ("FOREGROUND", "fg", foreground), ("BACKGROUND", "bg", background) })
            {
                // The shared bucket key is trendyol:orders:{host}:{sellerId}:{fg|bg}.
                var pattern = $"trendyol:orders:%:{connection.ExternalStoreId}:{suffix}";
                var usage = await db.Database.SqlQuery<QuotaRow>($"""
                    SELECT coalesce(sum("Requests"), 0)::int AS "Requests", coalesce(max("Requests"), 0)::int AS "Peak"
                    FROM integration.rate_limit_usage
                    WHERE "Key" LIKE {pattern} AND "Minute" >= {since}
                    """).SingleAsync(cancellationToken);
                result.Add(new("TRENDYOL", connection.DisplayName, lane, (int)Math.Round(limit * perMinute), usage.Requests, usage.Peak, Math.Round(usage.Requests / 60.0, 1)));
            }
        }
        return result;
    }

    private static double? Round(double? value) => value is { } number ? Math.Round(number, 1) : null;
    private sealed record LaneAttemptRow(string Lane, int Attempts, double? AverageWaitSeconds, double? P95WaitSeconds);
    private sealed record QuotaRow(int Requests, int Peak);
}
