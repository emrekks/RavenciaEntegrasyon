using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// Paneli Tamamen Sıfırla (PanelFactoryResetContracts): the request queues one PANEL_FACTORY_RESET job; the job cancels
// the tenant's waiting jobs, waits for the running ones, holds every sync lock of every connection and then empties
// the tables PanelFactoryResetInventory marks Delete, group by group in batches, children before parents. Nothing is
// written to a marketplace or to the invoice provider. A rerun after a failure continues where it stopped.
public sealed class PanelFactoryResetService(AppDbContext db, TimeProvider timeProvider, IDashboardReadService dashboard, ILogger<PanelFactoryResetService> logger) : IPanelFactoryResetService
{
    public const string ConfirmationWord = "PANELİ SIFIRLA";
    // The files of the tenant: the hourly sweep (PurgedFileRetention) deletes the file and then the row itself.
    public const string FileDropStatus = "PURGE_DROP";
    internal const int BatchSize = 20_000;
    internal static readonly TimeSpan RunningJobWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);
    private static readonly JobStatus[] WaitingJobStatuses = [JobStatus.Pending, JobStatus.RetryScheduled];
    private static readonly JobStatus[] OpenJobStatuses = [JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled];

    private sealed record Payload(Guid? KeepSessionId, Guid ActorUserId);

    public async Task<ServiceResult<PanelFactoryResetView>> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        ServiceResult<PanelFactoryResetView>.Ok(await ViewAsync(tenantId, cancellationToken));

    public async Task<ServiceResult<PanelFactoryResetView>> RequestAsync(Guid tenantId, Guid actorUserId, Guid? currentSessionId, PanelFactoryResetCommand command, string correlationId, CancellationToken cancellationToken)
    {
        if (!string.Equals(command.Confirmation?.Trim(), ConfirmationWord, StringComparison.Ordinal))
            return ServiceResult<PanelFactoryResetView>.Fail("VALIDATION_FAILED", $"Onay alanına tam olarak {ConfirmationWord} yazın.", 422, new Dictionary<string, string[]> { ["confirmation"] = [$"Onay alanına tam olarak {ConfirmationWord} yazın."] });

        // A second request while one is queued or running answers with that one.
        if (!await db.IntegrationJobs.AnyAsync(x => x.TenantId == tenantId && x.JobType == PanelJobTypes.FactoryReset && OpenJobStatuses.Contains(x.Status), cancellationToken))
        {
            var now = timeProvider.GetUtcNow();
            await CancelWaitingJobsAsync(tenantId, null, now, cancellationToken);
            var payload = JsonSerializer.Serialize(new Payload(currentSessionId, actorUserId));
            var dedup = $"panel-factory-reset:{tenantId:D}:{Guid.CreateVersion7():N}";
            db.IntegrationJobs.Add(new IntegrationJob
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = null,
                JobType = PanelJobTypes.FactoryReset,
                PayloadJson = payload,
                PayloadVersion = 1,
                PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
                JobDedupKey = dedup,
                EffectIdempotencyKey = dedup,
                Priority = JobPriorityPolicy.UserAction,
                TriggerType = "user",
                AvailableAt = now,
                CreatedAt = now,
                CorrelationId = correlationId,
                ProgressCurrent = 0,
                ProgressTotal = PanelFactoryResetInventory.Steps.Count - 1,
                ProgressPercent = 0,
                ProgressLabel = "Sıfırlama sırada bekliyor",
                Version = 1
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        return ServiceResult<PanelFactoryResetView>.Ok(await ViewAsync(tenantId, cancellationToken));
    }

    public async Task<JobExecutionResult> RunAsync(Guid tenantId, Guid jobId, string payloadJson, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<Payload>(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson) ?? new Payload(null, Guid.Empty);
        var plan = await PlanAsync(cancellationToken);
        if (plan.Problem is { } problem) return JobExecutionResult.Blocked("PANEL_RESET_UNCLASSIFIED", problem);

        // Nothing queued starts while the reset waits for the running jobs to finish.
        await CancelWaitingJobsAsync(tenantId, jobId, timeProvider.GetUtcNow(), cancellationToken);
        await ReportAsync(tenantId, jobId, 0, null, cancellationToken);
        var deadline = timeProvider.GetUtcNow() + RunningJobWait;
        while (await db.IntegrationJobs.AnyAsync(x => x.TenantId == tenantId && x.Id != jobId && x.Status == JobStatus.Leased && x.LeaseExpiresAt > timeProvider.GetUtcNow(), cancellationToken))
        {
            if (timeProvider.GetUtcNow() >= deadline)
                return JobExecutionResult.Deferred("PANEL_RESET_BUSY", "Süren bir iş bitince sıfırlama yeniden denenecek.", TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken);
        }

        // Every lane of every connection: a marketplace job that is leased now cannot start its work until the reset ends.
        var connectionIds = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.Id).ToListAsync(cancellationToken);
        var locks = new List<IAsyncDisposable>();
        var commandTimeout = db.Database.GetCommandTimeout();
        try
        {
            var lockDeadline = timeProvider.GetUtcNow() + OperationalDataMaintenanceService.FullResetLockWait;
            foreach (var connectionId in connectionIds)
            {
                foreach (var group in OperationalDataMaintenanceService.SyncLockGroups)
                {
                    var remaining = lockDeadline - timeProvider.GetUtcNow();
                    var acquired = await MarketplaceSyncExecutionLock.AcquireGroupAsync(db, connectionId, group, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider, cancellationToken);
                    if (acquired is null)
                        return JobExecutionResult.Deferred("PANEL_RESET_BUSY", "Bir bağlantıda süren eşitleme bitince sıfırlama yeniden denenecek.", TimeSpan.FromSeconds(30));
                    locks.Add(acquired);
                }
            }

            db.Database.SetCommandTimeout(CommandTimeout);
            var removed = 0L;
            foreach (var group in PanelFactoryResetInventory.Groups)
            {
                await ReportAsync(tenantId, jobId, group.Rank, null, cancellationToken);
                foreach (var table in plan.Order.Where(x => x.Group == group))
                    removed += await DeleteTableAsync(table, tenantId, jobId, plan.SelfReferencing.Contains(table.Name), cancellationToken);
                if (group == PanelFactoryResetInventory.Logs) await CleanUpConnectionsAsync(tenantId, connectionIds, cancellationToken);
            }
            await CleanUpSpecialTablesAsync(tenantId, payload.KeepSessionId, cancellationToken);

            // The one trace: when the reset was done and by whom (none of the earlier rows are left to tell it).
            var now = timeProvider.GetUtcNow();
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = payload.ActorUserId == Guid.Empty ? null : payload.ActorUserId, Action = "PANEL_FACTORY_RESET", TargetType = "Tenant", TargetId = tenantId.ToString("D"), Reason = $"removed={removed}", CorrelationId = jobId.ToString("D"), CreatedAt = now });
            // The report cache and the open panels learn that everything changed (the deletes raise no events of their own).
            db.IntegrationOutboxEvents.Add(new IntegrationOutboxEvent
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ResourceType = "tenant",
                OperationType = "reset",
                AggregateType = "Tenant",
                AggregateId = tenantId,
                PayloadJson = JsonSerializer.Serialize(new { aggregateType = "Tenant", aggregateId = tenantId }),
                CreatedAt = now,
                NextAttemptAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await ReportAsync(tenantId, jobId, PanelFactoryResetInventory.Steps.Count - 1, null, cancellationToken);
            await RefreshDashboardBestEffortAsync(tenantId);
            return JobExecutionResult.SuccessWithNote("PANEL_FACTORY_RESET_DONE", $"Panel sıfırlandı; {removed} kayıt silindi.");
        }
        finally
        {
            db.Database.SetCommandTimeout(commandTimeout);
            // The first lock opened the database connection the others share; it closes it, so it goes last.
            for (var index = locks.Count - 1; index >= 0; index--) await locks[index].DisposeAsync();
        }
    }

    // One table of the tenant, in batches; a table that points at itself goes in one statement, so a parent never
    // goes before its child within a batch.
    private async Task<long> DeleteTableAsync(PanelResetTable table, Guid tenantId, Guid jobId, bool selfReferencing, CancellationToken cancellationToken)
    {
        var name = Quote(table.Name);
        var keep = table.Name switch
        {
            "integration.jobs" => """ AND "Id" <> @job""",
            "integration.job_attempts" or "integration.job_read_summaries" or "integration.job_variant_results" => """ AND "JobId" <> @job""",
            _ => string.Empty
        };
        var filter = $"""
            "TenantId" = @tenant{keep}
            """;
        if (selfReferencing) return await ExecuteAsync($"DELETE FROM {name} WHERE {filter}", tenantId, jobId, cancellationToken);
        var total = 0L;
        while (true)
        {
            var count = await ExecuteAsync($"DELETE FROM {name} WHERE ctid = ANY(ARRAY(SELECT ctid FROM {name} WHERE {filter} LIMIT {BatchSize}))", tenantId, jobId, cancellationToken);
            total += count;
            if (count < BatchSize) return total;
        }
    }

    private Task<int> ExecuteAsync(string sql, Guid tenantId, Guid jobId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("job", jobId)], cancellationToken);

    // The connections stay with their credentials and settings: only their waits and last error go, so the next sync
    // reads everything again from the start (their cursors went with the logs).
    private async Task CleanUpConnectionsAsync(Guid tenantId, List<Guid> connectionIds, CancellationToken cancellationToken)
    {
        if (connectionIds.Count > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM integration.connection_backoffs WHERE "ConnectionId" = ANY({connectionIds.ToArray()})""", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE integration.platform_connections SET "LastErrorCode" = NULL, "LastErrorClass" = NULL, "Version" = "Version" + 1
            WHERE "TenantId" = {tenantId} AND ("LastErrorCode" IS NOT NULL OR "LastErrorClass" IS NOT NULL)
            """, cancellationToken);
    }

    private async Task CleanUpSpecialTablesAsync(Guid tenantId, Guid? keepSessionId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.file_assets SET "Status" = {FileDropStatus}, "ArchivedAt" = {timeProvider.GetUtcNow()}
            WHERE "TenantId" = {tenantId} AND "Status" <> {FileDropStatus}
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM iam.user_sessions WHERE "TenantId" = {tenantId} AND "Id" IS DISTINCT FROM CAST({keepSessionId} AS uuid)
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM iam.tenant_settings WHERE "TenantId" = {tenantId} AND "Key" = ANY({PanelFactoryResetInventory.HistorySettingKeys})
            """, cancellationToken);
    }

    internal sealed record Plan(IReadOnlyList<PanelResetTable> Order, IReadOnlySet<string> SelfReferencing, string? Problem);

    // The database's own tables and foreign keys: an unlisted table, a Delete table without a tenant column or a kept
    // table that points at a deleted one stops the reset before anything goes.
    internal async Task<Plan> PlanAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        var tables = new List<string>();
        var tenantColumns = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<(string Child, string Parent)>();
        try
        {
            await using (var command = new NpgsqlCommand("""
                SELECT t.table_schema || '.' || t.table_name,
                       EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = t.table_schema AND c.table_name = t.table_name AND c.column_name = 'TenantId')
                FROM information_schema.tables t
                WHERE t.table_type = 'BASE TABLE' AND t.table_schema = ANY(@schemas)
                """, connection))
            {
                command.Parameters.AddWithValue("schemas", PanelFactoryResetInventory.Schemas);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    tables.Add(reader.GetString(0));
                    if (reader.GetBoolean(1)) tenantColumns.Add(reader.GetString(0));
                }
            }
            await using (var command = new NpgsqlCommand("""
                SELECT cn.nspname || '.' || c.relname, pn.nspname || '.' || p.relname
                FROM pg_constraint k
                JOIN pg_class c ON c.oid = k.conrelid JOIN pg_namespace cn ON cn.oid = c.relnamespace
                JOIN pg_class p ON p.oid = k.confrelid JOIN pg_namespace pn ON pn.oid = p.relnamespace
                WHERE k.contype = 'f'
                """, connection))
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) keys.Add((reader.GetString(0), reader.GetString(1)));
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
        return BuildPlan(tables, tenantColumns, keys);
    }

    internal static Plan BuildPlan(IReadOnlyCollection<string> tables, IReadOnlySet<string> tenantColumns, IReadOnlyCollection<(string Child, string Parent)> keys)
    {
        var unclassified = tables.Where(x => PanelFactoryResetInventory.Find(x) is null).Order(StringComparer.Ordinal).ToList();
        if (unclassified.Count > 0) return new([], new HashSet<string>(), $"Sınıflandırılmamış tablo: {string.Join(", ", unclassified)}. Sıfırlama hiçbir şey silmeden durdu.");
        var existing = tables.ToHashSet(StringComparer.Ordinal);
        var deleted = PanelFactoryResetInventory.Tables.Where(x => x.Action == PanelResetAction.Delete && existing.Contains(x.Name)).ToList();
        var withoutTenant = deleted.Where(x => !tenantColumns.Contains(x.Name)).Select(x => x.Name).ToList();
        if (withoutTenant.Count > 0) return new([], new HashSet<string>(), $"Kiracı sütunu olmayan tablo: {string.Join(", ", withoutTenant)}. Sıfırlama hiçbir şey silmeden durdu.");
        var deletedNames = deleted.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var keptPointing = keys.Where(x => !deletedNames.Contains(x.Child) && deletedNames.Contains(x.Parent)).Select(x => $"{x.Child} → {x.Parent}").Distinct().ToList();
        if (keptPointing.Count > 0) return new([], new HashSet<string>(), $"Kalan tablo silinen tabloya bağlı: {string.Join(", ", keptPointing)}. Sıfırlama hiçbir şey silmeden durdu.");

        var selfReferencing = keys.Where(x => x.Child == x.Parent && deletedNames.Contains(x.Child)).Select(x => x.Child).ToHashSet(StringComparer.Ordinal);
        // Children first: a table is ready when every other table pointing at it is gone; among the ready ones the lowest
        // group rank goes first, so the groups run in their order as long as their keys agree with it.
        var children = deletedNames.ToDictionary(x => x, x => keys.Where(k => k.Parent == x && k.Child != x && deletedNames.Contains(k.Child)).Select(k => k.Child).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<PanelResetTable>();
        while (order.Count < deleted.Count)
        {
            var next = deleted.Where(x => !done.Contains(x.Name) && children[x.Name].All(done.Contains))
                .OrderBy(x => x.Group!.Rank).ThenBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault();
            if (next is null) return new([], new HashSet<string>(), "Silinecek tabloların bağları döngü oluşturuyor. Sıfırlama hiçbir şey silmeden durdu.");
            if (order.Count > 0 && order[^1].Group!.Rank > next.Group!.Rank)
                return new([], new HashSet<string>(), $"{next.Name} kendi grubundan önce silinmeli. Sıfırlama hiçbir şey silmeden durdu.");
            done.Add(next.Name);
            order.Add(next);
        }
        return new(order, selfReferencing, null);
    }

    private Task<int> CancelWaitingJobsAsync(Guid tenantId, Guid? keep, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.IntegrationJobs
            .Where(x => x.TenantId == tenantId && WaitingJobStatuses.Contains(x.Status) && x.JobType != PanelJobTypes.FactoryReset && x.Id != keep)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, JobStatus.Cancelled)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.LastErrorCode, "PANEL_FACTORY_RESET")
                .SetProperty(x => x.LastErrorSummary, "Panel tamamen sıfırlandığı için iptal edildi.")
                .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);

    private async Task ReportAsync(Guid tenantId, Guid? jobId, int step, string? label, CancellationToken cancellationToken)
    {
        if (jobId is not { } id) return;
        var last = PanelFactoryResetInventory.Steps.Count - 1;
        await db.IntegrationJobs.Where(x => x.TenantId == tenantId && x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.ProgressCurrent, step)
            .SetProperty(x => x.ProgressTotal, last)
            .SetProperty(x => x.ProgressPercent, step * 100 / last)
            .SetProperty(x => x.ProgressLabel, label ?? PanelFactoryResetInventory.Steps[step]), cancellationToken);
    }

    private async Task<PanelFactoryResetView> ViewAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // The reference lists are counted by their stored sizes: counting their rows scans millions.
        var counted = PanelFactoryResetInventory.Tables.Where(x => x.Action == PanelResetAction.Delete && x.Name != "integration.reference_items").ToList();
        var parts = counted.Select(x => $"""SELECT '{x.Group!.Key}' AS "Key", count(*) AS "Count" FROM {Quote(x.Name)} WHERE "TenantId" = @tenant""")
            .Append("""SELECT 'REFERENCES' AS "Key", coalesce(sum("ItemCount"), 0)::bigint AS "Count" FROM integration.reference_snapshots WHERE "TenantId" = @tenant""")
            .Append("""SELECT 'CACHES' AS "Key", count(*) AS "Count" FROM ops.file_assets WHERE "TenantId" = @tenant AND "Status" NOT IN ('DELETED', 'PURGE_DROP')""");
        var sql = $"""SELECT "Key", sum("Count")::bigint AS "Count" FROM ({string.Join(" UNION ALL ", parts)}) x GROUP BY "Key" """;
        var rows = await db.Database.SqlQueryRaw<CountRow>(sql, new NpgsqlParameter("tenant", tenantId)).ToListAsync(cancellationToken);
        var byKey = rows.ToDictionary(x => x.Key, x => x.Count, StringComparer.Ordinal);
        var groups = PanelFactoryResetInventory.Groups.Select(x => new PanelFactoryResetGroupView(x.Key, x.Label, byKey.GetValueOrDefault(x.Key))).ToList();
        var job = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.JobType == PanelJobTypes.FactoryReset)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.ProgressCurrent, x.ProgressLabel, x.ProgressPercent, x.LastErrorSummary, x.CreatedAt, x.CompletedAt })
            .FirstOrDefaultAsync(cancellationToken);
        var failed = job?.Status is JobStatus.Dead or JobStatus.Blocked or JobStatus.ManualReview or JobStatus.Cancelled;
        return new(groups, groups.Sum(x => x.Count), PanelFactoryResetInventory.Steps, PanelFactoryResetInventory.KeptForPanel, ConfirmationWord,
            job is null ? null : new(job.Id, JobStatusName(job.Status), job.ProgressCurrent, job.ProgressLabel, job.ProgressPercent, failed ? job.LastErrorSummary : null, job.CreatedAt, job.CompletedAt));
    }

    private sealed record CountRow(string Key, long Count);

    private static string JobStatusName(JobStatus status) => status switch
    {
        JobStatus.RetryScheduled => "RETRY_SCHEDULED",
        JobStatus.ManualReview => "MANUAL_REVIEW",
        _ => status.ToString().ToUpperInvariant()
    };

    // Names come from the inventory only (never from a request); quoted for PostgreSQL.
    private static string Quote(string table)
    {
        var parts = table.Split('.');
        return string.Join('.', parts.Select(part => $"\"{part.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    private async Task RefreshDashboardBestEffortAsync(Guid tenantId)
    {
        db.ChangeTracker.Clear();
        using var timeout = new CancellationTokenSource(OperationalDataMaintenanceService.DashboardRefreshTimeout);
        try
        {
            await dashboard.RebuildTenantAsync(tenantId, timeout.Token);
        }
        catch (Exception exception) when (exception is DbUpdateException || exception is OperationCanceledException && timeout.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Panel sıfırlamasından sonra pano özeti yenilenemedi; worker yenileyecek. TenantId: {TenantId}", tenantId);
        }
    }
}
