using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Deep audit 2026-10-20 S20: a manual Shopify status is written on the version the operator saw,
// once per request key, and holds until the marketplace status itself changes.
public sealed class ShopifyManualStatusPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Manual_status_names_the_marketplace_status_of_its_moment()
    {
        var sourceEventId = ShopifyManualStatusPolicy.SourceEventId("READY_TO_SHIP", "key-1");

        Assert.Equal("READY_TO_SHIP", ShopifyManualStatusPolicy.RemoteStatusAt(sourceEventId));
        Assert.EndsWith(ShopifyManualStatusPolicy.KeySuffix("key-1"), sourceEventId, StringComparison.Ordinal);
        Assert.Null(ShopifyManualStatusPolicy.RemoteStatusAt("manual:shopify-status:638000000000000000:abc"));
    }

    [Fact]
    public void Manual_status_holds_until_the_marketplace_status_changes()
    {
        // A newer marketplace change with the same status (a note, a tag) keeps it.
        Assert.True(ShopifyManualStatusPolicy.StillHolds(At, "NEW", At.AddHours(1), "NEW"));
        // A different marketplace status replaces it.
        Assert.False(ShopifyManualStatusPolicy.StillHolds(At, "NEW", At.AddHours(1), "SHIPPED"));
        // Older rows (without the recorded status) keep the old rule.
        Assert.False(ShopifyManualStatusPolicy.StillHolds(At, null, At.AddHours(1), "NEW"));
        Assert.True(ShopifyManualStatusPolicy.StillHolds(At, null, At.AddMinutes(-1), "SHIPPED"));
    }
}

[Collection("postgres-integration")]
public sealed class ShopifyManualStatusPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ManualStatusChecksTheVersionAndAnswersAResentRequestOnce()
    {
        var (tenantId, orderId) = await SeedAsync();
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var stale = await NewService(db).SetShopifyOrderStatusAsync(tenantId, Guid.CreateVersion7(), orderId, 7, "SHIPPED", "sh-key", "sh", CancellationToken.None);
                Assert.Equal(412, stale.Error!.Status);
            }
            await using (var db = fixture.CreateContext())
            {
                var applied = await NewService(db).SetShopifyOrderStatusAsync(tenantId, Guid.CreateVersion7(), orderId, 1, "SHIPPED", "sh-key", "sh", CancellationToken.None);
                Assert.Null(applied.Error);
                Assert.Equal(("SHIPPED", 2L), (applied.Value!.Status, applied.Value.Version));
            }
            await using (var db = fixture.CreateContext())
            {
                // The resend carries the version the first request was made on.
                var resent = await NewService(db).SetShopifyOrderStatusAsync(tenantId, Guid.CreateVersion7(), orderId, 1, "SHIPPED", "sh-key", "sh", CancellationToken.None);
                Assert.Null(resent.Error);
                Assert.Equal(2L, resent.Value!.Version);
            }
            await using var verify = fixture.CreateContext();
            var history = await verify.OrderStatusHistory.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.OrderId == orderId);
            Assert.Equal("NEW", ShopifyManualStatusPolicy.RemoteStatusAt(history.SourceEventId));
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // A second panel status on an order without package statuses records the marketplace status,
    // not the first panel status (which order.DerivedStatus holds by then).
    [PostgreSqlFact]
    public async Task SecondManualStatusRecordsTheMarketplaceStatusNotThePreviousPanelStatus()
    {
        var (tenantId, orderId) = await SeedAsync(withPackage: false);
        try
        {
            await using (var db = fixture.CreateContext())
                Assert.Null((await NewService(db).SetShopifyOrderStatusAsync(tenantId, Guid.CreateVersion7(), orderId, 1, "SHIPPED", "first-key", "sh", CancellationToken.None)).Error);
            await using (var db = fixture.CreateContext())
                Assert.Null((await NewService(db).SetShopifyOrderStatusAsync(tenantId, Guid.CreateVersion7(), orderId, 2, "DELIVERED", "second-key", "sh", CancellationToken.None)).Error);
            await using var verify = fixture.CreateContext();
            var rows = await verify.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId).ToListAsync();
            Assert.Equal(["NEW", "NEW"], rows.Select(row => ShopifyManualStatusPolicy.RemoteStatusAt(row.SourceEventId)));
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderStatusHistory.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private MarketplaceSalesService NewService(AppDbContext db) =>
        new(db, null!, new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);

    private async Task<(Guid TenantId, Guid OrderId)> SeedAsync(bool withPackage = true)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"shm-{Guid.NewGuid():N}"[..16], DisplayName = "shm", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "SHOPIFY", Environment = "PRODUCTION", DisplayName = "shm", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "2026-10" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = "shm-1", OrderNumber = "#shm-1", Currency = "TRY", OrderedAt = now.AddDays(-1), LastRemoteModifiedAt = now.AddDays(-1), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "NEW", CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Orders.Add(order);
        if (withPackage) db.ShipmentPackages.Add(new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalPackageId = "order:shm:remainder", Status = ShipmentPackageStatus.New, RawStatus = "UNFULFILLED", StatusOccurredAt = now.AddDays(-1), CreatedAt = now, UpdatedAt = now, Version = 1 });
        await db.SaveChangesAsync();
        return (tenant.Id, order.Id);
    }
}
