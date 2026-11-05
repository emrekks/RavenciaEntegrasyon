using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-11-05 LEAD-JOB-01: a reconcile read is folded into a running one of the same type only when the running
// one's scope covers it; a running 1-day return scan does not stand for a queued 90-day one.
public sealed class ReconcileCoalescingScopeTests
{
    private static readonly Guid ConnectionId = Guid.Parse("0199a7a2-0000-7000-8000-000000000001");

    private static string Payload(object value) => JsonSerializer.Serialize(value);

    [Theory]
    [InlineData(MarketplaceJobTypes.ReturnReconciliation)]
    [InlineData(MarketplaceJobTypes.OrderReconciliation)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderReconciliation)]
    public void ANarrowerRunningWindow_DoesNotCoverAWiderRequest(string jobType)
    {
        Assert.False(MarketplaceSyncExecutionLock.RunningScopeCovers(jobType, Payload(new { connectionId = ConnectionId, lookbackDays = 1 }), Payload(new { connectionId = ConnectionId, lookbackDays = 90 })));
        Assert.True(MarketplaceSyncExecutionLock.RunningScopeCovers(jobType, Payload(new { connectionId = ConnectionId, lookbackDays = 90 }), Payload(new { connectionId = ConnectionId, lookbackDays = 1 })));
        Assert.True(MarketplaceSyncExecutionLock.RunningScopeCovers(jobType, Payload(new { connectionId = ConnectionId, lookbackDays = 3 }), Payload(new { connectionId = ConnectionId, lookbackDays = 3 })));
    }

    [Fact]
    public void BatchAndHoursCountAsWidths_OtherSettingsMustMatch()
    {
        Assert.False(MarketplaceSyncExecutionLock.RunningScopeCovers(MarketplaceJobTypes.OrderReconciliation,
            Payload(new { connectionId = ConnectionId, lookbackDays = 90, batchSize = 25 }), Payload(new { connectionId = ConnectionId, lookbackDays = 3, batchSize = 50 })));
        Assert.True(MarketplaceSyncExecutionLock.RunningScopeCovers(MarketplaceJobTypes.OrderReconciliation,
            Payload(new { connectionId = ConnectionId, lookbackDays = 90, batchSize = 50 }), Payload(new { connectionId = ConnectionId, lookbackDays = 3, batchSize = 50 })));
        Assert.False(MarketplaceSyncExecutionLock.RunningScopeCovers(MarketplaceJobTypes.StockReconciliation,
            Payload(new { connectionId = ConnectionId, lookbackHours = 1 }), Payload(new { connectionId = ConnectionId, lookbackHours = 720 })));
        Assert.False(MarketplaceSyncExecutionLock.RunningScopeCovers(MarketplaceJobTypes.ReturnReconciliation,
            Payload(new { connectionId = ConnectionId, lookbackDays = 90, filter = "a" }), Payload(new { connectionId = ConnectionId, lookbackDays = 1, filter = "b" })));
        Assert.False(MarketplaceSyncExecutionLock.RunningScopeCovers(MarketplaceJobTypes.ReturnReconciliation, "not json", Payload(new { connectionId = ConnectionId, lookbackDays = 1 })));
        Assert.False(MarketplaceSyncExecutionLock.RunningScopeCovers(MarketplaceJobTypes.ReturnReconciliation, Payload(new { connectionId = ConnectionId }), Payload(new { connectionId = ConnectionId, lookbackDays = 1 })));
    }

    [Fact]
    public void ReadsWithoutAScope_StillCoalesceWhateverTheirPayload()
    {
        Assert.True(MarketplaceSyncExecutionLock.RunningScopeCovers(MarketplaceJobTypes.OrderSync, Payload(new { connectionId = ConnectionId }), Payload(new { connectionId = ConnectionId, externalOrderId = (string?)null })));
        Assert.True(MarketplaceSyncExecutionLock.RunningScopeCovers(MarketplaceJobTypes.ReturnSync, "{}", "{}"));
    }
}

[Collection("postgres-integration")]
public sealed class ReconcileCoalescingScopePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // The 1-day run holds the "returns" lock: the 90-day job waits for its turn instead of being reported as done.
    [PostgreSqlFact]
    public async Task ARunningOneDayReconcile_DoesNotSwallowAQueuedNinetyDayOne()
    {
        var result = await ProcessWhileRunningAsync("lj01a", runningLookbackDays: 1, requestedLookbackDays: 90);
        Assert.False(result.Succeeded);
        Assert.Equal("CONNECTION_OPERATION_BUSY", result.ErrorCode);
    }

    // The 90-day run covers a 1-day request: that one is still folded into it.
    [PostgreSqlFact]
    public async Task ARunningNinetyDayReconcile_StillCoalescesAQueuedOneDayOne()
    {
        var result = await ProcessWhileRunningAsync("lj01b", runningLookbackDays: 90, requestedLookbackDays: 1);
        Assert.True(result.Succeeded);
        Assert.Equal(MarketplaceSyncExecutionLock.CoalescedCode, result.ErrorCode);
    }

    // The lease query keeps the queued job back while the running lease is live, and leases it once that lease expired.
    [PostgreSqlFact]
    public async Task TheLeaseQuery_KeepsTheQueuedJobBackOnlyWhileTheRunningLeaseIsLive()
    {
        var now = fixture.Now;
        var (tenant, connection) = NewTenant("lj01l", now);
        var running = Job(tenant.Id, connection.Id, Payload(connection.Id, 1), JobStatus.Leased, "short");
        running.LeaseExpiresAt = now.AddMinutes(10);
        var daily = Job(tenant.Id, connection.Id, Payload(connection.Id, 90), JobStatus.Pending, "daily");
        daily.AvailableAt = now.AddMinutes(-1);
        daily.LeaseExpiresAt = null;
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.PlatformConnections.Add(connection);
            db.IntegrationJobs.AddRange(running, daily);
            await db.SaveChangesAsync();
        }
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Null(await new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider).TryLeaseAsync(TimeSpan.FromMinutes(5), null, null, CancellationToken.None, onlyJobId: daily.Id));
            await using (var db = fixture.CreateContext())
                await db.IntegrationJobs.Where(x => x.Id == running.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, now.AddMinutes(-1)));
            await using (var db = fixture.CreateContext())
                Assert.NotNull(await new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider).TryLeaseAsync(TimeSpan.FromMinutes(5), null, null, CancellationToken.None, onlyJobId: daily.Id));
        }
        finally { await CleanupAsync(tenant.Id); }
    }

    private async Task<JobExecutionResult> ProcessWhileRunningAsync(string code, int runningLookbackDays, int requestedLookbackDays)
    {
        var (tenant, connection) = NewTenant(code, DateTimeOffset.UtcNow);
        var runningPayload = Payload(connection.Id, runningLookbackDays);
        var requestedPayload = Payload(connection.Id, requestedLookbackDays);
        var running = Job(tenant.Id, connection.Id, runningPayload, JobStatus.Leased, $"scheduled:return-reconcile-running:{connection.Id:N}");
        var requested = Job(tenant.Id, connection.Id, requestedPayload, JobStatus.Leased, $"scheduled:return-reconcile-requested:{connection.Id:N}");
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.PlatformConnections.Add(connection);
            db.IntegrationJobs.AddRange(running, requested);
            await db.SaveChangesAsync();
        }
        await using var holderDb = fixture.CreateContext();
        var holder = await MarketplaceSyncExecutionLock.TryAcquireAsync(holderDb, connection.Id, MarketplaceJobTypes.ReturnReconciliation, CancellationToken.None, runningPayload);
        Assert.NotNull(holder);
        try
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            return await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnReconciliation, requestedPayload, code, CancellationToken.None, requested.Id);
        }
        finally
        {
            await holder!.DisposeAsync();
            await CleanupAsync(tenant.Id);
        }
    }

    private static (Tenant Tenant, PlatformConnection Connection) NewTenant(string code, DateTimeOffset now)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        return (tenant, connection);
    }

    private static string Payload(Guid connectionId, int lookbackDays) => JsonSerializer.Serialize(new { connectionId, lookbackDays });

    private static IntegrationJob Job(Guid tenantId, Guid connectionId, string payload, JobStatus status, string dedup) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        JobType = MarketplaceJobTypes.ReturnReconciliation,
        TriggerType = "scheduled",
        PayloadJson = payload,
        PayloadVersion = 1,
        PayloadHash = Guid.NewGuid().ToString("N"),
        JobDedupKey = $"{dedup}:{Guid.NewGuid():N}",
        EffectIdempotencyKey = Guid.NewGuid().ToString("N"),
        Priority = 1,
        Status = status,
        AvailableAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        MaxAttempts = 3,
        CorrelationId = "lj01",
        CreatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var cleanup = fixture.CreateContext();
        await cleanup.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
