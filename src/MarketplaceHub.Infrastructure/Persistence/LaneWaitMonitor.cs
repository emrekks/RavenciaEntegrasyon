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
    // marketplace quota (quota-aware leasing) counts as waiting: its delay must show against the target and in the alert. A
    // job waiting for its connection's busy lock group, a quota backoff or the tenant's Paneli Tamamen Sıfırla waits for that,
    // not for a slot, and does not count (the lease skips it the same way). In the user lane only UserAction jobs count
    // (LaneWaitTargets.CountsTowardTarget): bulk user work is queued on purpose.
    public async Task<IReadOnlyList<LaneWait>> SnapshotAsync(Guid? tenantId, TimeSpan window, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var lanes = WorkerLaneModel.Lanes(configuration, hotPriorityCeiling);
        var lane = QueueHealthService.LaneCaseSql(lanes, out var laneParameters);
        var parameters = new List<object>
        {
            new Npgsql.NpgsqlParameter("since", now - window),
            new Npgsql.NpgsqlParameter("tenant", (object?)tenantId ?? DBNull.Value) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid },
            new Npgsql.NpgsqlParameter("userAction", JobPriorityPolicy.UserAction),
            new Npgsql.NpgsqlParameter("firstNonUser", JobPriorityPolicy.InitialDataSync)
        };
        parameters.AddRange(laneParameters);
        // The same wait İşlem Takibi shows: first attempts of jobs that were never retried, from AvailableAt to the lease. Bulk
        // user work (between UserAction and the first non-user priority) stays out of the target.
        var sql = "SELECT attempt.\"TenantId\" AS \"TenantId\", " + lane + """
             AS "Lane",
                   count(wait.seconds)::int AS "Attempts",
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY wait.seconds) AS "P95WaitSeconds"
            FROM integration.job_attempts attempt
            JOIN integration.jobs job ON job."Id" = attempt."JobId"
            CROSS JOIN LATERAL (SELECT CASE WHEN attempt."AttemptNumber" = 1 AND job."AttemptCount" <= 1 AND job."AvailableAt" <= attempt."StartedAt"
                AND NOT (job."Priority" > @userAction AND job."Priority" < @firstNonUser)
                THEN EXTRACT(EPOCH FROM attempt."StartedAt" - job."AvailableAt")::double precision END AS seconds) wait
            WHERE attempt."StartedAt" >= @since AND (@tenant IS NULL OR attempt."TenantId" = @tenant)
            GROUP BY 1, 2
            """;
        var recent = await db.Database.SqlQueryRaw<RecentRow>(sql, [.. parameters]).ToListAsync(cancellationToken);
        var waiting = await WaitingAsync(tenantId, now, cancellationToken);

        var oldest = new Dictionary<(Guid, string), double>();
        foreach (var row in waiting)
        {
            var laneName = WorkerLaneModel.LaneFor(configuration, row.JobType, row.Priority, hotPriorityCeiling);
            if (!LaneWaitTargets.CountsTowardTarget(laneName, row.Priority)) continue;
            var key = (row.TenantId, laneName);
            var seconds = Math.Max(0, (now - row.RunnableSince).TotalSeconds);
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
    public async Task<int> EvaluateAsync(LaneWaitAlarm alarm, TimeSpan window, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var snapshot = await SnapshotAsync(null, window, cancellationToken);
        var openIssues = await OpenIssuesAsync(cancellationToken);
        var open = openIssues.Select(issue => (issue.TenantId, issue.DedupeKey)).ToHashSet();
        // A tenant with an open alert but no work any more is healthy: its lanes are looked at too.
        var lanes = WorkerLaneModel.Lanes(configuration, hotPriorityCeiling).Select(lane => lane.Name).ToArray();
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
        // Queue phase 2: an alert of a lane the current lane model does not have (opened before a switch between the earlier
        // lanes and the dispatcher's) is never looked at again, so it is resolved now.
        // With the earlier lanes only the dispatcher's lanes' alerts are such (after a rollback); nothing else changes for them.
        var dispatcherModel = WorkerLaneModel.IsDispatcher(configuration);
        foreach (var (tenant, key) in openIssues.Where(issue => !lanes.Any(lane => LaneWaitTargets.IssueKey(lane) == issue.DedupeKey)
            && (dispatcherModel || DispatcherLanes.Names.Any(lane => LaneWaitTargets.IssueKey(lane) == issue.DedupeKey))))
        {
            await recorder.ResolveAsync(tenant, key, now, cancellationToken, IssueResolveStamps.ResolvedAt | IssueResolveStamps.LastSeenAt);
            changed++;
        }
        // Lanes of tenants that dropped out of the snapshot start their count again when they come back.
        alarm.ForgetStale(now);
        if (changed > 0) await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    // Lane alerts switched off (Worker:LaneAlerts:Enabled=false): every open lane alert is resolved, nothing watches it any more.
    public async Task<int> ResolveAllAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var openIssues = await OpenIssuesAsync(cancellationToken);
        if (openIssues.Count == 0) return 0;
        var recorder = new IssueRecorder(db);
        foreach (var issue in openIssues)
            await recorder.ResolveAsync(issue.TenantId, issue.DedupeKey, now, cancellationToken, IssueResolveStamps.ResolvedAt | IssueResolveStamps.LastSeenAt);
        await db.SaveChangesAsync(cancellationToken);
        return openIssues.Count;
    }

    private async Task<List<(Guid TenantId, string DedupeKey)>> OpenIssuesAsync(CancellationToken cancellationToken) =>
        [.. (await db.OperationalIssues.AsNoTracking()
            .Where(issue => issue.TenantId != null && issue.Code == LaneWaitTargets.IssueCode && issue.Status == IssueStatus.Open)
            .Select(issue => new { TenantId = issue.TenantId!.Value, issue.DedupeKey })
            .ToListAsync(cancellationToken)).Select(issue => (issue.TenantId, issue.DedupeKey))];

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

    // Queued jobs a slot could take now (not waiting for a busy lock group, a quota backoff or the tenant's Paneli Tamamen
    // Sıfırla), with the moment the oldest of each group became runnable. Same rules as the lease (JobLeaseService).
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
              AND NOT EXISTS (
                  SELECT 1 FROM integration.jobs panel_reset
                  WHERE panel_reset."TenantId" = job."TenantId"
                    AND panel_reset."JobType" = {PanelJobTypes.FactoryReset}
                    AND panel_reset."Status" IN ('PENDING', 'LEASED', 'RETRY_SCHEDULED')
                    AND panel_reset."Id" <> job."Id"
                    AND (job."JobType" <> {PanelJobTypes.FactoryReset}
                      OR (panel_reset."CreatedAt", panel_reset."Id") < (job."CreatedAt", job."Id")))
            GROUP BY 1, 2, 3, 4
            """).ToListAsync(cancellationToken);

    private sealed record RecentRow(Guid TenantId, string Lane, int Attempts, double? P95WaitSeconds);
    private sealed record WaitingRow(Guid TenantId, string JobType, int Priority, Guid? ConnectionId, DateTimeOffset RunnableSince);
}
