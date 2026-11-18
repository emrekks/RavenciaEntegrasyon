using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// Worker lanes step 1 (19 Nov 2026): each lane's queue wait now, against its target (LaneWaitTargets), per tenant. The worker
// evaluates it every minute and opens or resolves the lane's operational issue (LaneWaitAlarm); the panel's lane metrics
// (QueueHealthService.LanesAsync) show the same snapshot with the target and the alert.
public sealed class LaneWaitMonitor(AppDbContext db, TimeProvider timeProvider, IConfiguration? configuration = null)
{
    private readonly int hotPriorityCeiling = Math.Clamp(configuration?.GetValue("Worker:HotPriorityCeiling", WorkerLanes.DefaultHotPriorityCeiling) ?? WorkerLanes.DefaultHotPriorityCeiling, 0, 5);

    private static readonly string[] LockGroupJobTypes = [.. MarketplaceJobTypes.ProcessorJobTypes];
    private static readonly string[] LockGroups = [.. MarketplaceJobTypes.ProcessorJobTypes.Select(MarketplaceSyncExecutionLock.GroupFor)];

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    public sealed record LaneWait(Guid TenantId, string Lane, TimeSpan Target, int RecentAttempts, double? RecentP95WaitSeconds, double? OldestWaitSeconds)
    {
        public bool Breached => (RecentAttempts > 0 && RecentP95WaitSeconds > Target.TotalSeconds) || OldestWaitSeconds > Target.TotalSeconds;
    }

    // Every lane of every tenant with work in the window or queued now (tenantId: one tenant only). A job held for a full
    // marketplace quota (hold) waits for the marketplace, not for a slot, so it does not count as waiting here, the same way
    // a job waiting for its connection's busy lock group or quota backoff does not (QueueHealthService.RunnableDepthAsync).
    public async Task<IReadOnlyList<LaneWait>> SnapshotAsync(Guid? tenantId, TimeSpan window, JobQuotaHold? hold, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var lanes = WorkerLanes.All(hotPriorityCeiling);
        var lane = QueueHealthService.LaneCaseSql(lanes, out var laneParameters);
        var parameters = new List<object>
        {
            new Npgsql.NpgsqlParameter("since", now - window),
            new Npgsql.NpgsqlParameter("tenant", (object?)tenantId ?? DBNull.Value) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid }
        };
        parameters.AddRange(laneParameters);
        // The same wait İşlem Takibi shows: first attempts of jobs that were never retried, from AvailableAt to the lease.
        var sql = "SELECT attempt.\"TenantId\" AS \"TenantId\", " + lane + """
             AS "Lane",
                   count(wait.seconds)::int AS "Attempts",
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY wait.seconds) AS "P95WaitSeconds"
            FROM integration.job_attempts attempt
            JOIN integration.jobs job ON job."Id" = attempt."JobId"
            CROSS JOIN LATERAL (SELECT CASE WHEN attempt."AttemptNumber" = 1 AND job."AttemptCount" <= 1 AND job."AvailableAt" <= attempt."StartedAt"
                THEN EXTRACT(EPOCH FROM attempt."StartedAt" - job."AvailableAt")::double precision END AS seconds) wait
            WHERE attempt."StartedAt" >= @since AND (@tenant IS NULL OR attempt."TenantId" = @tenant)
            GROUP BY 1, 2
            """;
        var recent = await db.Database.SqlQueryRaw<RecentRow>(sql, [.. parameters]).ToListAsync(cancellationToken);
        var waiting = await WaitingAsync(tenantId, now, cancellationToken);

        var heldPairs = hold is { IsEmpty: false }
            ? hold.ConnectionIds.Zip(hold.JobTypes).ToHashSet()
            : [];
        var oldest = new Dictionary<(Guid, string), double>();
        foreach (var row in waiting)
        {
            var since = row.RunnableSince;
            var held = row.ConnectionId is { } connection && row.Priority > JobPriorityPolicy.BulkUserAction
                && heldPairs.Contains((connection, row.JobType)) && hold is not null && since > now - hold.MaximumHold;
            if (held) continue;
            var key = (row.TenantId, WorkerLanes.LaneFor(row.JobType, row.Priority, hotPriorityCeiling));
            var seconds = Math.Max(0, (now - since).TotalSeconds);
            oldest[key] = Math.Max(oldest.GetValueOrDefault(key), seconds);
        }

        var tenants = recent.Select(row => row.TenantId).Concat(waiting.Select(row => row.TenantId)).Distinct().ToList();
        if (tenantId is { } only && !tenants.Contains(only)) tenants.Add(only);
        return [.. tenants.SelectMany(tenant => lanes.Select(definition =>
        {
            var row = recent.FirstOrDefault(item => item.TenantId == tenant && item.Lane == definition.Name);
            return new LaneWait(tenant, definition.Name, LaneWaitTargets.For(configuration, definition.Name), row?.Attempts ?? 0,
                row?.P95WaitSeconds is { } p95 ? Math.Round(p95, 1) : null,
                oldest.TryGetValue((tenant, definition.Name), out var seconds) ? Math.Round(seconds, 1) : null);
        }))];
    }

    // Opens an issue for each lane whose target has been missed for the alarm's sustain time and resolves the issues of lanes
    // back within target for its clear time. Returns how many issues changed.
    public async Task<int> EvaluateAsync(LaneWaitAlarm alarm, TimeSpan window, JobQuotaHold? hold, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var snapshot = await SnapshotAsync(null, window, hold, cancellationToken);
        var openIssues = await db.OperationalIssues.AsNoTracking()
            .Where(issue => issue.TenantId != null && issue.Code == LaneWaitTargets.IssueCode && issue.Status == IssueStatus.Open)
            .Select(issue => new { TenantId = issue.TenantId!.Value, issue.DedupeKey })
            .ToListAsync(cancellationToken);
        var open = openIssues.Select(issue => (issue.TenantId, issue.DedupeKey)).ToHashSet();
        // A tenant with an open alert but no work any more is healthy: its lanes are looked at too.
        var lanes = WorkerLanes.All(hotPriorityCeiling).Select(lane => lane.Name).ToArray();
        var looked = snapshot.ToList();
        foreach (var tenant in openIssues.Select(issue => issue.TenantId).Distinct().Where(tenant => snapshot.All(row => row.TenantId != tenant)))
            looked.AddRange(lanes.Select(lane => new LaneWait(tenant, lane, LaneWaitTargets.For(configuration, lane), 0, null, null)));

        var recorder = new IssueRecorder(db);
        var changed = 0;
        foreach (var lane in looked)
        {
            var key = LaneWaitTargets.IssueKey(lane.Lane);
            switch (alarm.Observe(lane.TenantId, lane.Lane, lane.Breached, open.Contains((lane.TenantId, key)), now))
            {
                case LaneAlarmTransition.Open:
                    await recorder.RecordAsync(lane.TenantId, key, LaneWaitTargets.IssueCode,
                        LaneWaitTargets.IssueSummary(lane.Lane, lane.Target, lane.RecentP95WaitSeconds, lane.OldestWaitSeconds), now, cancellationToken);
                    changed++;
                    break;
                case LaneAlarmTransition.Resolve:
                    await recorder.ResolveAsync(lane.TenantId, key, now, cancellationToken, IssueResolveStamps.ResolvedAt | IssueResolveStamps.LastSeenAt);
                    changed++;
                    break;
            }
        }
        if (changed > 0) await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    // The open lane alerts of a tenant: lane name -> since when (the issue's last opening).
    public async Task<Dictionary<string, DateTimeOffset>> OpenAlertsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var issues = await db.OperationalIssues.AsNoTracking()
            .Where(issue => issue.TenantId == tenantId && issue.Code == LaneWaitTargets.IssueCode && issue.Status == IssueStatus.Open)
            .Select(issue => new { issue.DedupeKey, issue.LastSeenAt })
            .ToListAsync(cancellationToken);
        const string prefix = "queue-wait:";
        return issues.Where(issue => issue.DedupeKey.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(issue => issue.DedupeKey[prefix.Length..], issue => issue.LastSeenAt, StringComparer.Ordinal);
    }

    // Queued jobs a slot could take now (not waiting for a busy lock group or a quota backoff), with the moment the oldest of
    // each group became runnable. Same rule as QueueHealthService.RunnableDepthAsync.
    private Task<List<WaitingRow>> WaitingAsync(Guid? tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<WaitingRow>($"""
            WITH busy AS (
                SELECT DISTINCT running."ConnectionId", coalesce(running."LockGroup", running_group.lock_group) AS lock_group
                FROM integration.jobs running
                LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS running_group(job_type, lock_group)
                  ON running_group.job_type = running."JobType"
                WHERE running."Status" = 'LEASED' AND running."LeaseExpiresAt" > {now} AND running."ConnectionId" IS NOT NULL)
            SELECT job."TenantId" AS "TenantId", job."JobType" AS "JobType", job."Priority" AS "Priority", job."ConnectionId" AS "ConnectionId",
                   min(greatest(job."AvailableAt", job."CreatedAt")) AS "RunnableSince"
            FROM integration.jobs job
            LEFT JOIN unnest({LockGroupJobTypes}::text[], {LockGroups}::text[]) AS job_group(job_type, lock_group)
              ON job_group.job_type = job."JobType"
            WHERE ({tenantId}::uuid IS NULL OR job."TenantId" = {tenantId}::uuid)
              AND job."Status" IN ('PENDING', 'RETRY_SCHEDULED') AND job."AvailableAt" <= {now} AND job."AttemptCount" < job."MaxAttempts"
              AND NOT EXISTS (SELECT 1 FROM busy WHERE busy."ConnectionId" = job."ConnectionId" AND busy.lock_group = coalesce(job."LockGroup", job_group.lock_group))
              AND NOT EXISTS (SELECT 1 FROM integration.connection_backoffs backoff
                  WHERE backoff."ConnectionId" = job."ConnectionId" AND backoff."LockGroup" = coalesce(job."LockGroup", job_group.lock_group) AND backoff."Until" > {now})
            GROUP BY 1, 2, 3, 4
            """).ToListAsync(cancellationToken);

    private sealed record RecentRow(Guid TenantId, string Lane, int Attempts, double? P95WaitSeconds);
    private sealed record WaitingRow(Guid TenantId, string JobType, int Priority, Guid? ConnectionId, DateTimeOffset RunnableSince);
}
