using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Paneli Tamamen Sıfırla (12 Nov 2026): tenant A starts again from zero; its connections, credentials and settings stay,
// and tenant B is not touched. Uses the full-reset scenario's seed (two marketplace stores and an e-invoice provider
// with orders, invoices, products, stock, finance, references, jobs and logs) for both tenants.
public sealed partial class PostgreSqlConnectionFullResetTests
{
    [PostgreSqlFact]
    public async Task PanelFactoryReset_EmptiesEveryDeletedTableOfTheTenant_KeepsTheRest_AndLeavesTheOtherTenant()
    {
        var a = NewSeed("pf-a");
        var b = NewSeed("pf-b");
        try
        {
            await SeedAsync(a);
            await SeedAsync(b);
            foreach (var seed in new[] { a, b }) await SeedPanelExtrasAsync(seed.Tenant.Id);
            var tables = await TenantTablesAsync();
            var aBefore = await TenantCountsAsync(tables, a.Tenant.Id);
            var bBefore = await TenantCountsAsync(tables, b.Tenant.Id);
            // Every group the panel lists has rows to remove.
            foreach (var group in PanelFactoryResetInventory.Groups)
                Assert.True(PanelFactoryResetInventory.Tables.Where(x => x.Group == group && x.Action == PanelResetAction.Delete).Sum(x => aBefore.GetValueOrDefault(x.Name)) > 0, group.Key);

            Guid jobId;
            await using (var db = fixture.CreateContext())
            {
                var service = NewPanelService(db);
                var wrong = await service.RequestAsync(a.Tenant.Id, Guid.CreateVersion7(), null, new PanelFactoryResetCommand("PANELI SIFIRLA"), "corr", CancellationToken.None);
                Assert.Equal(("VALIDATION_FAILED", 422), (wrong.Error!.Code, wrong.Error.Status));
                var view = await service.RequestAsync(a.Tenant.Id, Guid.CreateVersion7(), null, new PanelFactoryResetCommand(" PANELİ SIFIRLA "), "corr", CancellationToken.None);
                Assert.True(view.Succeeded, view.Error?.Message);
                Assert.Equal("PENDING", view.Value!.Job!.Status);
                Assert.True(view.Value.Total > 0);
                var again = await service.RequestAsync(a.Tenant.Id, Guid.CreateVersion7(), null, new PanelFactoryResetCommand("PANELİ SIFIRLA"), "corr", CancellationToken.None);
                jobId = again.Value!.Job!.JobId;
                Assert.Equal(view.Value.Job.JobId, jobId);
            }
            // The request cancelled the tenant's waiting jobs; the worker leases the reset.
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(JobStatus.Cancelled, (await db.IntegrationJobs.SingleAsync(x => x.Id == a.OtherJob.Id)).Status);
                Assert.Equal(JobStatus.Pending, (await db.IntegrationJobs.SingleAsync(x => x.Id == b.OtherJob.Id)).Status);
                Assert.Null((await db.IntegrationJobs.SingleAsync(x => x.Id == jobId)).ConnectionId);
                // Nothing else of the tenant starts while the reset waits or runs, the scheduler's reads included.
                var scheduled = Job(a.Tenant.Id, a.Other.Id, MarketplaceJobTypes.OrderSync, JobStatus.Pending);
                db.IntegrationJobs.Add(scheduled);
                await db.SaveChangesAsync();
                var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
                Assert.Null(await leases.TryLeaseAsync(TimeSpan.FromMinutes(5), null, null, CancellationToken.None, onlyJobId: scheduled.Id));
                Assert.NotNull(await leases.TryLeaseAsync(TimeSpan.FromMinutes(5), null, null, CancellationToken.None, onlyJobId: jobId));
            }
            await using (var db = fixture.CreateContext())
            {
                var job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
                var result = await NewPanelService(db).RunAsync(a.Tenant.Id, jobId, job.PayloadJson, CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorSummary);
            }

            var aAfter = await TenantCountsAsync(tables, a.Tenant.Id);
            foreach (var table in tables)
            {
                var entry = PanelFactoryResetInventory.Find(table)!;
                long expected = entry.Action switch
                {
                    // The running reset with its attempt, its one audit row and the event that tells the panels.
                    PanelResetAction.Delete => table is "integration.jobs" or "integration.job_attempts" or "ops.audit_logs" or "integration.outbox_events" ? 1 : 0,
                    _ when table == "iam.tenant_settings" => aBefore[table] - 1,
                    _ => aBefore[table]
                };
                Assert.True(expected == aAfter[table], $"{table}: expected {expected}, found {aAfter[table]}");
            }
            Assert.Equal(bBefore, await TenantCountsAsync(tables, b.Tenant.Id));
            // Every dashboard table of the tenant is empty (the old revenue days used to outlive the connection resets).
            Assert.All(tables.Where(x => x.StartsWith("dashboard.", StringComparison.Ordinal)), table => Assert.Equal(0, aAfter[table]));

            await using var verification = fixture.CreateContext();
            Assert.Equal(PanelJobTypes.FactoryReset, (await verification.IntegrationJobs.SingleAsync(x => x.TenantId == a.Tenant.Id)).JobType);
            Assert.Equal(100, (await verification.IntegrationJobs.SingleAsync(x => x.Id == jobId)).ProgressPercent);
            Assert.Equal("PANEL_FACTORY_RESET", (await verification.AuditLogs.SingleAsync(x => x.TenantId == a.Tenant.Id)).Action);
            Assert.All(await verification.FileAssets.Where(x => x.TenantId == a.Tenant.Id).ToListAsync(), x => Assert.Equal(PanelFactoryResetService.FileDropStatus, x.Status));
            Assert.Equal("ACTIVE", (await verification.FileAssets.SingleAsync(x => x.Id == b.ManualPdf.Id)).Status);
            Assert.Equal(["appearance"], await verification.TenantSettings.Where(x => x.TenantId == a.Tenant.Id).Select(x => x.Key).ToListAsync());
            // The connections stay as they were, with their credentials; their waits went, the other tenant's stayed.
            Assert.Equal(3, await verification.PlatformConnections.CountAsync(x => x.TenantId == a.Tenant.Id && x.Status == "ACTIVE"));
            Assert.Equal(0, await BackoffsAsync(verification, a.Tenant.Id));
            Assert.Equal(2, await BackoffsAsync(verification, b.Tenant.Id));
        }
        finally
        {
            foreach (var seed in new[] { a, b })
            {
                await DeletePanelExtrasAsync(seed.Tenant.Id);
                await DeleteTenantAsync(seed.Tenant.Id);
            }
        }
    }

    private PanelFactoryResetService NewPanelService(AppDbContext db) => new(db, fixture.TimeProvider, new NoDashboard(), NullLogger<PanelFactoryResetService>.Instance);

    private static Task<int> BackoffsAsync(AppDbContext db, Guid tenantId) => db.Database.SqlQuery<int>($"""
        SELECT count(*)::int AS "Value" FROM integration.connection_backoffs b JOIN integration.platform_connections c ON c."Id" = b."ConnectionId" WHERE c."TenantId" = {tenantId}
        """).SingleAsync();

    // The logs, lists and settings the full-reset seed does not reach.
    private async Task SeedPanelExtrasAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        db.Categories.Add(new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "Elbise", NormalizedName = "elbise", Path = "Elbise", CreatedAt = fixture.Now, UpdatedAt = fixture.Now });
        db.Brands.Add(new Brand { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "Ravencia", NormalizedName = "ravencia", CreatedAt = fixture.Now, UpdatedAt = fixture.Now });
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "PRODUCT_UPDATED", TargetType = "Product", TargetId = "x", Reason = "test", CorrelationId = "corr", CreatedAt = fixture.Now });
        db.AiUsageLogs.Add(new AiUsageLog { Id = Guid.CreateVersion7(), TenantId = tenantId, Feature = "TEST", Model = "test", Status = "OK", CreatedAt = fixture.Now });
        db.ApiIdempotencyRecords.Add(new ApiIdempotencyRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, RouteTemplate = "/api/v1/test", IdempotencyKey = $"pf-{Guid.NewGuid():N}", RequestHash = "hash", State = "COMPLETED", CreatedAt = fixture.Now, ExpiresAt = fixture.Now.AddDays(1) });
        db.IntegrationOutboxEvents.Add(new IntegrationOutboxEvent { Id = Guid.CreateVersion7(), TenantId = tenantId, ResourceType = "orders", OperationType = "update", AggregateType = "Order", AggregateId = Guid.CreateVersion7(), PayloadJson = JsonSerializer.Serialize(new { }), CreatedAt = fixture.Now, NextAttemptAt = fixture.Now });
        db.Set<DashboardRevenueDaily>().Add(new DashboardRevenueDaily { TenantId = tenantId, Day = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), PlatformName = "Trendyol", Currency = "TRY", Amount = 120m, OrderCount = 1, UpdatedAt = fixture.Now });
        db.TenantSettings.AddRange(
            new TenantSetting { TenantId = tenantId, Key = "appearance", ValueJson = "{}", UpdatedAt = fixture.Now, Version = 1 },
            new TenantSetting { TenantId = tenantId, Key = "invoice-workspace-hidden-packages", ValueJson = "[]", UpdatedAt = fixture.Now, Version = 1 });
        await db.SaveChangesAsync();
    }

    private async Task DeletePanelExtrasAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DELETE FROM dashboard.revenue_daily WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.categories WHERE "TenantId"={{tenantId}};
            DELETE FROM catalog.brands WHERE "TenantId"={{tenantId}};
            DELETE FROM ops.ai_usage_logs WHERE "TenantId"={{tenantId}};
            DELETE FROM ops.api_idempotency_records WHERE "TenantId"={{tenantId}};
            DELETE FROM iam.tenant_settings WHERE "TenantId"={{tenantId}};
            """);
    }

    private async Task<List<string>> TenantTablesAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQuery<string>($"""
            SELECT t.table_schema || '.' || t.table_name AS "Value" FROM information_schema.tables t
            WHERE t.table_type = 'BASE TABLE' AND t.table_schema = ANY({PanelFactoryResetInventory.Schemas})
              AND EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = t.table_schema AND c.table_name = t.table_name AND c.column_name = 'TenantId')
            ORDER BY 1
            """).ToListAsync();
    }

    private async Task<Dictionary<string, long>> TenantCountsAsync(IEnumerable<string> tables, Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var quoted = string.Join('.', table.Split('.').Select(part => $"\"{part}\""));
            await using var command = new NpgsqlCommand($"""SELECT count(*) FROM {quoted} WHERE "TenantId"=@tenant""", connection);
            command.Parameters.AddWithValue("tenant", tenantId);
            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }
        return counts;
    }
}
