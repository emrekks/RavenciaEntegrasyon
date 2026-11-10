using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Live 10 Nov 2026: Pazarama and PTT AVM showed "Sipariş: Hata Var" with "Bazı akışlar beklenen sürenin iki katından uzun
// süredir çalışmadı" while every order read succeeded: their open-package status scans are queued only while a package
// is open, and none was. A scan with nothing to follow is no stall; one that stops while a package stays open still is.
[Collection("postgres-integration")]
public sealed class DashboardIntegrationMatrixPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task A_status_scan_silent_for_want_of_an_open_package_leaves_the_order_cell_healthy()
    {
        var tenantId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var quiet = Connection(tenantId, "PAZARAMA", "A Pazarama");
        var stalled = Connection(tenantId, "PTTAVM", "B PttAVM");
        IntegrationJob Job(PlatformConnection connection, string type, DateTimeOffset at) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connection.Id,
            JobType = type,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "h",
            JobDedupKey = $"matrix:{Guid.NewGuid():N}",
            EffectIdempotencyKey = $"matrix:{Guid.NewGuid():N}",
            Status = JobStatus.Succeeded,
            AvailableAt = at,
            CorrelationId = "test",
            CreatedAt = at,
            CompletedAt = at,
            Version = 1
        };
        ConnectionSyncPolicy Lifecycle(PlatformConnection connection) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connection.Id,
            ResourceType = "ORDER_LIFECYCLE",
            IntervalSeconds = 120,
            JitterSeconds = 10,
            Enabled = true,
            Version = 1
        };
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = stalled.Id,
            ExternalOrderId = "matrix-open",
            OrderNumber = "matrix-open",
            Currency = "TRY",
            GrossAmount = 100m,
            NetAmount = 100m,
            OrderedAt = now.AddDays(-2),
            LastRemoteModifiedAt = now.AddDays(-2),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "NEW",
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now,
            Version = 1
        };
        var package = new ShipmentPackage
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = stalled.Id,
            OrderId = order.Id,
            ExternalPackageId = "matrix-open",
            GrossAmount = 100m,
            NetAmount = 100m,
            Status = ShipmentPackageStatus.New,
            RawStatus = "kargo_yapilmasi_bekleniyor",
            StatusOccurredAt = now.AddDays(-2),
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now,
            Version = 1
        };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Code = $"matrix-{Guid.NewGuid():N}", DisplayName = "matrix", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 });
            seed.PlatformConnections.AddRange(quiet, stalled);
            seed.ConnectionSyncPolicies.AddRange(Lifecycle(quiet), Lifecycle(stalled));
            seed.IntegrationJobs.AddRange(
                Job(quiet, MarketplaceJobTypes.PazaramaOrderSync, now.AddMinutes(-1)), Job(quiet, MarketplaceJobTypes.PazaramaOrderStatusSync, now.AddHours(-6)),
                Job(stalled, MarketplaceJobTypes.PttAvmOrderSync, now.AddMinutes(-1)), Job(stalled, MarketplaceJobTypes.PttAvmOrderStatusSync, now.AddHours(-6)));
            seed.Orders.Add(order);
            seed.ShipmentPackages.Add(package);
            await seed.SaveChangesAsync();
        }
        try
        {
            await using var db = fixture.CreateContext();
            var rows = await DashboardIntegrationMatrix.QueryAsync(db, tenantId, now, CancellationToken.None);
            var pazarama = rows.Single(x => x.ConnectionId == quiet.Id);
            Assert.Equal("ok", pazarama.Cells["orders"].State);
            Assert.Null(pazarama.Issue);
            var pttAvm = rows.Single(x => x.ConnectionId == stalled.Id);
            Assert.Equal("bad", pttAvm.Cells["orders"].State);
            Assert.Equal("Bazı akışlar beklenen sürenin iki katından uzun süredir çalışmadı.", pttAvm.Issue);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        }
    }

    private static PlatformConnection Connection(Guid tenantId, string platform, string name) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platform,
        Environment = "PRODUCTION",
        DisplayName = name,
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}",
        Version = 1
    };
}
