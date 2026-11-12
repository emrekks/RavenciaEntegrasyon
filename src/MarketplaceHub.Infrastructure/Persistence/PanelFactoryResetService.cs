using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// Ayarlar › Veri resets (PanelFactoryResetContracts): the request queues one PANEL_FACTORY_RESET job; the job cancels the
// tenant's waiting jobs (and nothing else of the tenant is leased until it ends, JobLeaseService), waits for the running
// ones, holds every sync lock of every connection and then empties the chosen categories' tables
// (PanelFactoryResetInventory), category by category in batches, children before parents. Every category chosen is
// Paneli Tamamen Sıfırla. Nothing is written to a marketplace or to the invoice provider. A rerun after a failure
// continues where it stopped.
public sealed class PanelFactoryResetService(AppDbContext db, TimeProvider timeProvider, IDashboardReadService dashboard, ILogger<PanelFactoryResetService> logger) : IPanelFactoryResetService
{
    public const string ConfirmationWord = "PANELİ SIFIRLA";
    public const string PartialConfirmationWord = "VERİLERİ SİL";
    // The files a reset leaves unused: the hourly sweep (PurgedFileRetention) deletes the file and then the row itself.
    public const string FileDropStatus = "PURGE_DROP";
    internal const int BatchSize = 20_000;
    internal static readonly TimeSpan RunningJobWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);
    private static readonly JobStatus[] WaitingJobStatuses = [JobStatus.Pending, JobStatus.RetryScheduled];
    private static readonly JobStatus[] OpenJobStatuses = [JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled];
    private static readonly System.Globalization.CultureInfo Turkish = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

    // The columns that keep a stored file in use, with the category whose rows hold them.
    internal static readonly IReadOnlyList<(string Table, string Column, string Category)> FileReferences =
    [
        ("catalog.product_media", "FileAssetId", "PRODUCTS"),
        ("catalog.product_size_charts", "FileAssetId", "PRODUCTS"),
        ("catalog.product_size_charts", "LastGeneratedAssetId", "PRODUCTS"),
        ("catalog.import_sessions", "SourceAssetId", "PRODUCTS"),
        ("integration.reference_snapshots", "AssetId", "REFERENCES"),
        ("billing.invoice_documents", "FileAssetId", "INVOICES"),
        ("billing.manual_order_invoice_documents", "FileAssetId", "INVOICES"),
        ("sales.return_evidence", "FileAssetId", "ORDERS"),
        ("sales.shipment_documents", "FileAssetId", "ORDERS"),
    ];

    // Turkish capitals, spaces at the ends and doubled ones ignored (as the panel compares): "paneli sıfırla" matches,
    // "PANELI SIFIRLA" (dotless I) does not.
    internal static string NormalizeConfirmation(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpper(Turkish);

    private sealed record Payload(Guid? KeepSessionId, Guid ActorUserId, string[]? Categories = null);

    public async Task<ServiceResult<PanelFactoryResetView>> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        ServiceResult<PanelFactoryResetView>.Ok(await ViewAsync(tenantId, cancellationToken));

    public async Task<ServiceResult<PanelFactoryResetView>> RequestAsync(Guid tenantId, Guid actorUserId, Guid? currentSessionId, PanelFactoryResetCommand command, string correlationId, CancellationToken cancellationToken)
    {
        var partial = command.Categories is { Count: > 0 };
        var chosen = partial ? PanelFactoryResetInventory.Close(command.Categories!) : PanelFactoryResetInventory.Groups;
        if (chosen is null) return Invalid("categories", "Geçerli veri türleri seçin.");
        var typed = NormalizeConfirmation(command.Confirmation);
        // Every category chosen one by one is Paneli Tamamen Sıfırla (the other sessions end, every stored file goes), so
        // it takes the full reset's word, not the partial one.
        var full = chosen.Count == PanelFactoryResetInventory.Groups.Count;
        if (!(typed == ConfirmationWord || !full && typed == PartialConfirmationWord))
            return Invalid("confirmation", full ? $"Bütün veri türleri seçili: bu Paneli Tamamen Sıfırla demektir. Onay alanına tam olarak {ConfirmationWord} yazın." : "Onay alanına tam olarak Verileri sil yazın.");

        // A second request while one is queued or running answers with that one.
        if (!await db.IntegrationJobs.AnyAsync(x => x.TenantId == tenantId && x.JobType == PanelJobTypes.FactoryReset && OpenJobStatuses.Contains(x.Status), cancellationToken))
        {
            var now = timeProvider.GetUtcNow();
            await CancelWaitingJobsAsync(tenantId, null, now, cancellationToken);
            var payload = JsonSerializer.Serialize(new Payload(currentSessionId, actorUserId, [.. chosen.Select(x => x.Key)]));
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
                ProgressTotal = PanelFactoryResetInventory.StepsFor(chosen).Count - 1,
                ProgressPercent = 0,
                ProgressLabel = "Sıfırlama sırada bekliyor",
                Version = 1
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        return ServiceResult<PanelFactoryResetView>.Ok(await ViewAsync(tenantId, cancellationToken));
    }

    private static IReadOnlyList<PanelResetGroup> ChosenOf(Payload payload) =>
        payload.Categories is { Length: > 0 } keys ? PanelFactoryResetInventory.Close(keys) ?? [] : PanelFactoryResetInventory.Groups;

    private static Payload ReadPayload(string? payloadJson) =>
        JsonSerializer.Deserialize<Payload>(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson) ?? new Payload(null, Guid.Empty);

    public async Task<JobExecutionResult> RunAsync(Guid tenantId, Guid jobId, string payloadJson, CancellationToken cancellationToken)
    {
        var payload = ReadPayload(payloadJson);
        var chosen = ChosenOf(payload);
        if (chosen.Count == 0) return JobExecutionResult.Blocked("PANEL_RESET_INVALID", "Sıfırlanacak veri türü yok.");
        var full = chosen.Count == PanelFactoryResetInventory.Groups.Count;
        var steps = PanelFactoryResetInventory.StepsFor(chosen);
        var plan = await PlanAsync(chosen, cancellationToken);
        if (plan.Problem is { } problem) return JobExecutionResult.Blocked("PANEL_RESET_UNCLASSIFIED", problem);

        // Nothing queued starts while the reset waits for the running jobs to finish.
        await CancelWaitingJobsAsync(tenantId, jobId, timeProvider.GetUtcNow(), cancellationToken);
        await ReportAsync(tenantId, jobId, steps, 0, cancellationToken);
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
            var files = new HashSet<Guid>();
            var chosenKeys = chosen.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < chosen.Count; index++)
            {
                var group = chosen[index];
                await ReportAsync(tenantId, jobId, steps, index + 1, cancellationToken);
                // The files its rows use, before they go.
                foreach (var reference in FileReferences.Where(x => x.Category == group.Key))
                {
                    var used = $"""SELECT "{reference.Column}" AS "Value" FROM {Quote(reference.Table)} WHERE "TenantId" = @tenant AND "{reference.Column}" IS NOT NULL""";
                    files.UnionWith(await db.Database.SqlQueryRaw<Guid>(used, new NpgsqlParameter("tenant", tenantId)).ToListAsync(cancellationToken));
                }
                foreach (var detach in plan.Detaches.Where(x => x.Group == group)) await ExecuteAsync(detach.Sql, tenantId, jobId, cancellationToken);
                foreach (var table in plan.Order.Where(x => x.Group == group))
                    removed += await DeleteTableAsync(table, tenantId, jobId, plan.SelfReferencing.Contains(table.Name), cancellationToken);
                if (group == PanelFactoryResetInventory.Jobs) await CleanUpConnectionsAsync(tenantId, connectionIds, cancellationToken);
                if (group == PanelFactoryResetInventory.LegacyConnections) removed += await CleanUpLegacyConnectionsAsync(tenantId, cancellationToken);
                if (group == PanelFactoryResetInventory.Logs) removed += await db.Database.ExecuteSqlRawAsync($"DELETE FROM ops.audit_logs a WHERE {TenantlessAuditFilter}", [new NpgsqlParameter("tenant", tenantId)], cancellationToken);
                if (group == PanelFactoryResetInventory.Invoices)
                    await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM iam.tenant_settings WHERE "TenantId" = {tenantId} AND "Key" = ANY({PanelFactoryResetInventory.HistorySettingKeys})""", cancellationToken);
            }

            await ReportAsync(tenantId, jobId, steps, steps.Count - 2, cancellationToken);
            await DropUnusedFilesAsync(tenantId, full ? null : files, cancellationToken);
            // A full reset leaves this session signed in and ends the others.
            if (full)
                await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM iam.user_sessions WHERE "TenantId" = {tenantId} AND "Id" IS DISTINCT FROM CAST({payload.KeepSessionId} AS uuid)""", cancellationToken);

            // When and by whom: a full reset's one trace (none of the earlier rows are left to tell it).
            var now = timeProvider.GetUtcNow();
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = payload.ActorUserId == Guid.Empty ? null : payload.ActorUserId, Action = full ? "PANEL_FACTORY_RESET" : "PANEL_DATA_RESET", TargetType = "Tenant", TargetId = tenantId.ToString("D"), Reason = $"{string.Join(',', chosenKeys.Order(StringComparer.Ordinal))};removed={removed}", CorrelationId = jobId.ToString("D"), CreatedAt = now });
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
            await ReportAsync(tenantId, jobId, steps, steps.Count - 1, cancellationToken);
            await RefreshDashboardBestEffortAsync(tenantId);
            return JobExecutionResult.SuccessWithNote(full ? "PANEL_FACTORY_RESET_DONE" : "PANEL_DATA_RESET_DONE", full ? $"Panel sıfırlandı; {removed} kayıt silindi." : $"Seçilen veriler silindi; {removed} kayıt.");
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
    // reads everything again from the start (their cursors went with the jobs).
    private async Task CleanUpConnectionsAsync(Guid tenantId, List<Guid> connectionIds, CancellationToken cancellationToken)
    {
        if (connectionIds.Count > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM integration.connection_backoffs WHERE "ConnectionId" = ANY({connectionIds.ToArray()})""", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE integration.platform_connections SET "LastErrorCode" = NULL, "LastErrorClass" = NULL, "Version" = "Version" + 1
            WHERE "TenantId" = {tenantId} AND ("LastErrorCode" IS NOT NULL OR "LastErrorClass" IS NOT NULL)
            """, cancellationToken);
    }

    // Sign-in records written before a workspace was known (TenantId empty: failed sign-ins, lockouts). They belong to this
    // tenant when their user is a member of it and of no other tenant; an unknown account's records (no user) only when this
    // is the installation's one tenant, so a reset never removes another tenant's security trail.
    internal const string TenantlessAuditFilter = """
        a."TenantId" IS NULL AND (
          a."ActorUserId" IS NOT NULL
            AND EXISTS (SELECT 1 FROM iam.tenant_memberships m WHERE m."UserId" = a."ActorUserId" AND m."TenantId" = @tenant)
            AND NOT EXISTS (SELECT 1 FROM iam.tenant_memberships m WHERE m."UserId" = a."ActorUserId" AND m."TenantId" <> @tenant)
          OR a."ActorUserId" IS NULL AND (SELECT count(*) FROM iam.tenants) = 1)
        """;

    // What the reset itself leaves (its job and attempt, its one audit row, the events it raised, the empty dashboard
    // summary rebuilt after it) is not counted as a record still to remove.
    private static string CountFilter(string table) => table switch
    {
        "integration.jobs" => $""" AND "JobType" <> '{PanelJobTypes.FactoryReset}'""",
        "integration.job_attempts" or "integration.job_read_summaries" or "integration.job_variant_results" =>
            $""" AND "JobId" NOT IN (SELECT "Id" FROM integration.jobs WHERE "TenantId" = @tenant AND "JobType" = '{PanelJobTypes.FactoryReset}')""",
        "integration.outbox_events" =>
            $""" AND NOT ("AggregateType" = 'Tenant' AND "OperationType" = 'reset') AND NOT ("AggregateType" = 'IntegrationJob' AND "AggregateId" IN (SELECT "Id" FROM integration.jobs WHERE "TenantId" = @tenant AND "JobType" = '{PanelJobTypes.FactoryReset}'))""",
        "ops.audit_logs" => """ AND "Action" NOT IN ('PANEL_FACTORY_RESET', 'PANEL_DATA_RESET')""",
        "dashboard.snapshot" => " AND false",
        _ => string.Empty
    };

    // The settings tables a connection keeps; a deleted connection's rows in them go with it.
    private static readonly string[] ConnectionSettingTables =
    [
        "integration.platform_credentials", "integration.platform_capabilities", "integration.connection_sync_policies", "integration.webhook_subscriptions",
        "integration.sync_cursors", "integration.marketplace_question_sync_states", "integration.marketplace_finance_sync_states",
        "sales.cargo_provider_mappings", "inventory.connection_inventory_policies", "inventory.connection_location_mappings",
        "catalog.connection_attribute_defaults", "catalog.attribute_mapping_templates", "catalog.attribute_value_mapping_templates", "catalog.mapping_template_dismissals",
    ];

    // Revoked credentials, and the connections deleted earlier with everything they still keep. A deleted connection whose
    // records another category still holds (its orders, say, when Siparişler is not chosen) stays until they go.
    private async Task<long> CleanUpLegacyConnectionsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var removed = (long)await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM integration.platform_credentials WHERE "TenantId" = {tenantId} AND "RevokedAt" IS NOT NULL""", cancellationToken);
        var deleted = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status == "DELETED").Select(x => x.Id).ToListAsync(cancellationToken);
        if (deleted.Count == 0) return removed;
        foreach (var table in ConnectionSettingTables)
        {
            var sql = $"""DELETE FROM {Quote(table)} WHERE "TenantId" = @tenant AND "ConnectionId" = ANY(@ids)""";
            removed += await db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("ids", deleted.ToArray())], cancellationToken);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM integration.connection_backoffs WHERE "ConnectionId" = ANY({deleted.ToArray()})""", cancellationToken);
        removed += await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM billing.invoice_policies p WHERE p."TenantId" = {tenantId} AND p."ProviderConnectionId" = ANY({deleted.ToArray()})
              AND NOT EXISTS (SELECT 1 FROM billing.invoices i WHERE i."TenantId" = p."TenantId" AND i."InvoicePolicyId" = p."Id")
            """, cancellationToken);
        foreach (var connectionId in deleted)
        {
            try
            {
                removed += await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM integration.platform_connections WHERE "TenantId" = {tenantId} AND "Id" = {connectionId} AND "Status" = 'DELETED'""", cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                logger.LogInformation("Silinmiş bağlantının kayıtları başka bir veri türünde duruyor; bağlantı satırı kaldı. ConnectionId: {ConnectionId}", connectionId);
            }
        }
        return removed;
    }

    // A full reset drops every file of the tenant; a partial one the files the removed rows used that nothing else uses.
    private async Task DropUnusedFilesAsync(Guid tenantId, HashSet<Guid>? candidates, CancellationToken cancellationToken)
    {
        if (candidates is { Count: 0 }) return;
        var unused = string.Join(" AND ", FileReferences.Select(x => $"""NOT EXISTS (SELECT 1 FROM {Quote(x.Table)} r WHERE r."TenantId" = f."TenantId" AND r."{x.Column}" = f."Id")"""));
        var candidateFilter = candidates is null ? string.Empty : """ AND f."Id" = ANY(@files)""";
        var sql = $"""UPDATE ops.file_assets f SET "Status" = @drop, "ArchivedAt" = @now WHERE f."TenantId" = @tenant AND f."Status" <> @drop{candidateFilter} AND {unused}""";
        await db.Database.ExecuteSqlRawAsync(sql,
            [new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("drop", FileDropStatus), new NpgsqlParameter("now", timeProvider.GetUtcNow()), new NpgsqlParameter("files", (candidates ?? []).ToArray())],
            cancellationToken);
    }

    internal sealed record PlannedDetach(PanelResetGroup Group, string Sql);
    internal sealed record Plan(IReadOnlyList<PanelResetTable> Order, IReadOnlySet<string> SelfReferencing, IReadOnlyList<PlannedDetach> Detaches, string? Problem);
    internal sealed record ForeignKey(string Child, string Parent, bool Cascades);

    // The database's own tables and foreign keys: an unlisted table, a table to empty without a tenant column, a kept
    // table pointing at one that goes, or a row of a category that stays pointing at one that goes with no way to
    // detach it stops the reset before anything goes.
    internal async Task<Plan> PlanAsync(IReadOnlyList<PanelResetGroup> chosen, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        var tables = new List<string>();
        var tenantColumns = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<ForeignKey>();
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
                SELECT cn.nspname || '.' || c.relname, pn.nspname || '.' || p.relname, k.confdeltype IN ('c', 'n', 'd')
                FROM pg_constraint k
                JOIN pg_class c ON c.oid = k.conrelid JOIN pg_namespace cn ON cn.oid = c.relnamespace
                JOIN pg_class p ON p.oid = k.confrelid JOIN pg_namespace pn ON pn.oid = p.relnamespace
                WHERE k.contype = 'f'
                """, connection))
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) keys.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
        return BuildPlan(tables, tenantColumns, keys, chosen);
    }

    private static Plan Stop(string problem) => new([], new HashSet<string>(), [], $"{problem} Sıfırlama hiçbir şey silmeden durdu.");

    internal static Plan BuildPlan(IReadOnlyCollection<string> tables, IReadOnlySet<string> tenantColumns, IReadOnlyCollection<ForeignKey> keys, IReadOnlyList<PanelResetGroup> chosen)
    {
        var unclassified = tables.Where(x => PanelFactoryResetInventory.Find(x) is null).Order(StringComparer.Ordinal).ToList();
        if (unclassified.Count > 0) return Stop($"Sınıflandırılmamış tablo: {string.Join(", ", unclassified)}.");
        var existing = tables.ToHashSet(StringComparer.Ordinal);
        var deletable = PanelFactoryResetInventory.Tables.Where(x => x.Action == PanelResetAction.Delete && existing.Contains(x.Name)).ToList();
        var withoutTenant = deletable.Where(x => !tenantColumns.Contains(x.Name)).Select(x => x.Name).ToList();
        if (withoutTenant.Count > 0) return Stop($"Kiracı sütunu olmayan tablo: {string.Join(", ", withoutTenant)}.");
        var deletableNames = deletable.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var keptPointing = keys.Where(x => !x.Cascades && !deletableNames.Contains(x.Child) && deletableNames.Contains(x.Parent)).Select(x => $"{x.Child} → {x.Parent}").Distinct().ToList();
        if (keptPointing.Count > 0) return Stop($"Kalan tablo silinen tabloya bağlı: {string.Join(", ", keptPointing)}.");

        var chosenSet = chosen.ToHashSet();
        var deleted = deletable.Where(x => chosenSet.Contains(x.Group!)).ToList();
        var deletedNames = deleted.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        // Rows of a category that stays pointing at one that goes: detached first, or the reset stops.
        var detaches = new List<PlannedDetach>();
        foreach (var key in keys.Where(x => !x.Cascades && deletableNames.Contains(x.Child) && !deletedNames.Contains(x.Child) && deletedNames.Contains(x.Parent)))
        {
            var detach = PanelFactoryResetInventory.Detaches.FirstOrDefault(x => x.ChildTable == key.Child && x.ParentTable == key.Parent);
            if (detach is null) return Stop($"{key.Child} → {key.Parent} bağı için önce {PanelFactoryResetInventory.Find(key.Child)!.Group!.Label} de seçilmeli.");
            var group = PanelFactoryResetInventory.Find(key.Parent)!.Group!;
            if (!detaches.Any(x => x.Sql == detach.Sql)) detaches.Add(new(group, detach.Sql));
        }
        // Links without a key (an order line's variant) are detached the same way.
        foreach (var detach in PanelFactoryResetInventory.Detaches.Where(x => !keys.Any(k => k.Child == x.ChildTable && k.Parent == x.ParentTable)))
            if (deletedNames.Contains(detach.ParentTable) && !deletedNames.Contains(detach.ChildTable) && !detaches.Any(x => x.Sql == detach.Sql))
                detaches.Add(new(PanelFactoryResetInventory.Find(detach.ParentTable)!.Group!, detach.Sql));

        var selfReferencing = keys.Where(x => x.Child == x.Parent && deletedNames.Contains(x.Child)).Select(x => x.Child).ToHashSet(StringComparer.Ordinal);
        // Children first: a table is ready when every other table pointing at it is gone; among the ready ones the lowest
        // category rank goes first, so the categories run in their order as long as their keys agree with it.
        var children = deletedNames.ToDictionary(x => x, x => keys.Where(k => k.Parent == x && k.Child != x && deletedNames.Contains(k.Child)).Select(k => k.Child).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<PanelResetTable>();
        while (order.Count < deleted.Count)
        {
            var next = deleted.Where(x => !done.Contains(x.Name) && children[x.Name].All(done.Contains))
                .OrderBy(x => x.Group!.Rank).ThenBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault();
            if (next is null) return Stop("Silinecek tabloların bağları döngü oluşturuyor.");
            if (order.Count > 0 && order[^1].Group!.Rank > next.Group!.Rank) return Stop($"{next.Name} kendi grubundan önce silinmeli.");
            done.Add(next.Name);
            order.Add(next);
        }
        return new(order, selfReferencing, detaches, null);
    }

    private Task<int> CancelWaitingJobsAsync(Guid tenantId, Guid? keep, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.IntegrationJobs
            .Where(x => x.TenantId == tenantId && WaitingJobStatuses.Contains(x.Status) && x.JobType != PanelJobTypes.FactoryReset && x.Id != keep)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, JobStatus.Cancelled)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.LastErrorCode, "PANEL_FACTORY_RESET")
                .SetProperty(x => x.LastErrorSummary, "Panel verileri sıfırlandığı için iptal edildi.")
                .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);

    private Task ReportAsync(Guid tenantId, Guid jobId, IReadOnlyList<string> steps, int step, CancellationToken cancellationToken)
    {
        var last = steps.Count - 1;
        return db.IntegrationJobs.Where(x => x.TenantId == tenantId && x.Id == jobId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.ProgressCurrent, step)
            .SetProperty(x => x.ProgressTotal, last)
            .SetProperty(x => x.ProgressPercent, step * 100 / last)
            .SetProperty(x => x.ProgressLabel, steps[step]), cancellationToken);
    }

    private async Task<PanelFactoryResetView> ViewAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // The reference lists are counted by their stored sizes: counting their rows scans millions.
        var counted = PanelFactoryResetInventory.Tables.Where(x => x.Action == PanelResetAction.Delete && x.Name != "integration.reference_items").ToList();
        var parts = counted.Select(x => $"""SELECT '{x.Group!.Key}' AS "Key", count(*) AS "Count" FROM {Quote(x.Name)} WHERE "TenantId" = @tenant{CountFilter(x.Name)}""")
            .Append($"""SELECT 'LOGS' AS "Key", count(*) AS "Count" FROM ops.audit_logs a WHERE {TenantlessAuditFilter}""")
            .Append("""SELECT 'REFERENCES' AS "Key", coalesce(sum("ItemCount"), 0)::bigint AS "Count" FROM integration.reference_snapshots WHERE "TenantId" = @tenant""")
            .Append("""SELECT 'LEGACY_CONNECTIONS' AS "Key", count(*) AS "Count" FROM integration.platform_credentials c WHERE c."TenantId" = @tenant AND (c."RevokedAt" IS NOT NULL OR EXISTS (SELECT 1 FROM integration.platform_connections p WHERE p."Id" = c."ConnectionId" AND p."Status" = 'DELETED'))""")
            .Append("""SELECT 'LEGACY_CONNECTIONS' AS "Key", count(*) AS "Count" FROM integration.platform_connections WHERE "TenantId" = @tenant AND "Status" = 'DELETED'""");
        var sql = $"""SELECT "Key", sum("Count")::bigint AS "Count" FROM ({string.Join(" UNION ALL ", parts)}) x GROUP BY "Key" """;
        var rows = await db.Database.SqlQueryRaw<CountRow>(sql, new NpgsqlParameter("tenant", tenantId)).ToListAsync(cancellationToken);
        var byKey = rows.ToDictionary(x => x.Key, x => x.Count, StringComparer.Ordinal);
        var groups = PanelFactoryResetInventory.Groups.Select(x => new PanelFactoryResetGroupView(x.Key, x.Label, x.Description, x.Section, x.Requires, byKey.GetValueOrDefault(x.Key))).ToList();
        var job = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.JobType == PanelJobTypes.FactoryReset)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.PayloadJson, x.ProgressCurrent, x.ProgressLabel, x.ProgressPercent, x.LastErrorSummary, x.CreatedAt, x.CompletedAt })
            .FirstOrDefaultAsync(cancellationToken);
        PanelFactoryResetJobView? jobView = null;
        if (job is not null)
        {
            var chosen = ChosenOf(ReadPayload(job.PayloadJson));
            var failed = job.Status is JobStatus.Dead or JobStatus.Blocked or JobStatus.ManualReview or JobStatus.Cancelled;
            jobView = new(job.Id, JobStatusName(job.Status), chosen.Count == PanelFactoryResetInventory.Groups.Count, [.. chosen.Select(x => x.Key)], PanelFactoryResetInventory.StepsFor(chosen),
                job.ProgressCurrent, job.ProgressLabel, job.ProgressPercent, failed ? job.LastErrorSummary : null, job.CreatedAt, job.CompletedAt);
        }
        return new(groups, groups.Sum(x => x.Count), PanelFactoryResetInventory.Steps, PanelFactoryResetInventory.KeptForPanel, ConfirmationWord, "Verileri sil", jobView);
    }

    private sealed record CountRow(string Key, long Count);

    private static string JobStatusName(JobStatus status) => status switch
    {
        JobStatus.RetryScheduled => "RETRY_SCHEDULED",
        JobStatus.ManualReview => "MANUAL_REVIEW",
        _ => status.ToString().ToUpperInvariant()
    };

    private static ServiceResult<PanelFactoryResetView> Invalid(string field, string message) =>
        ServiceResult<PanelFactoryResetView>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });

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
