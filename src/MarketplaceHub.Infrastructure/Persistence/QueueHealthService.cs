using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Persistence;

// Read-only queue health: waits per worker lane, and how full the Trendyol order quota runs.
public sealed class QueueHealthService(AppDbContext db, IOptions<TrendyolOptions> trendyolOptions, TimeProvider timeProvider) : IQueueHealthService
{
    private static readonly string[] Lanes = ["USER", "INITIAL", "HOT", "BACKGROUND"];

    public async Task<IReadOnlyList<LaneMetricView>> LanesAsync(Guid tenantId, TimeSpan window, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var since = now - window;
        // The lanes follow the job priorities the worker leases by.
        var attempts = await db.Database.SqlQuery<LaneAttemptRow>($"""
            SELECT CASE WHEN job."Priority" <= {JobPriorityPolicy.UserAction} THEN 'USER'
                        WHEN job."Priority" < 0 THEN 'INITIAL'
                        WHEN job."Priority" <= 2 THEN 'HOT'
                        ELSE 'BACKGROUND' END AS "Lane",
                   count(*)::int AS "Attempts",
                   avg(wait.seconds) AS "AverageWaitSeconds",
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY wait.seconds) AS "P95WaitSeconds"
            FROM integration.job_attempts attempt
            JOIN integration.jobs job ON job."Id" = attempt."JobId"
            CROSS JOIN LATERAL (SELECT CASE WHEN attempt."AttemptNumber" = 1 AND job."AttemptCount" <= 1 AND job."AvailableAt" <= attempt."StartedAt"
                THEN EXTRACT(EPOCH FROM attempt."StartedAt" - job."AvailableAt")::double precision END AS seconds) wait
            WHERE attempt."TenantId" = {tenantId} AND attempt."StartedAt" >= {since}
            GROUP BY 1
            """).ToListAsync(cancellationToken);
        var queue = await db.IntegrationJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && (job.Status == JobStatus.Leased
                || ((job.Status == JobStatus.Pending || job.Status == JobStatus.RetryScheduled) && job.AvailableAt <= now)))
            .Select(job => new { job.Priority, Running = job.Status == JobStatus.Leased })
            .ToListAsync(cancellationToken);
        return Lanes.Select(lane =>
        {
            var attempt = attempts.FirstOrDefault(row => row.Lane == lane);
            var depth = queue.Where(job => LaneFor(job.Priority) == lane).ToArray();
            return new LaneMetricView(lane, attempt?.Attempts ?? 0, Round(attempt?.AverageWaitSeconds), Round(attempt?.P95WaitSeconds), depth.Count(job => !job.Running), depth.Count(job => job.Running));
        }).ToList();
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

    internal static string LaneFor(int priority) =>
        priority <= JobPriorityPolicy.UserAction ? "USER" : priority < 0 ? "INITIAL" : priority <= 2 ? "HOT" : "BACKGROUND";

    private static double? Round(double? value) => value is { } number ? Math.Round(number, 1) : null;
    private sealed record LaneAttemptRow(string Lane, int Attempts, double? AverageWaitSeconds, double? P95WaitSeconds);
    private sealed record QuotaRow(int Requests, int Peak);
}
