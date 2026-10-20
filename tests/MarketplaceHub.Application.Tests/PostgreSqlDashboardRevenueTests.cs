using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The performance panel's revenue (O1, O2): cancelled packages and returned parts of an order are not revenue,
// and a store waiting for new credentials keeps its past sales.
[Collection("postgres-integration")]
public sealed class PostgreSqlDashboardRevenueTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // 20 Oct 2026, 10:30 in Istanbul; the report asks for 15 Oct, a whole day in the past.
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task A_split_order_counts_only_its_packages_that_were_not_cancelled()
    {
        var tenant = NewTenant("rev-split");
        var store = NewConnection(tenant.Id, "HEPSIBURADA", "ACTIVE");
        var order = NewOrder(tenant.Id, store.Id, "split", Noon, 300m, "DELIVERED");
        try
        {
            await SeedAsync(tenant, [store], db =>
            {
                db.Orders.Add(order);
                db.ShipmentPackages.AddRange(
                    NewPackage(order, "split-1", 200m, ShipmentPackageStatus.Delivered),
                    NewPackage(order, "split-2", 100m, ShipmentPackageStatus.Cancelled));
                db.OrderLines.Add(NewLine(order, "split-line", 300m, 1m));
            });

            var row = Assert.Single((await PerformanceAsync(tenant.Id)).Current);

            Assert.Equal(200m, row.Amount);
            Assert.Equal(1, row.OrderCount);
            Assert.Equal(1, row.PackageCount);
            Assert.Equal(0m, row.ReturnedAmount);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task A_partial_return_is_taken_off_the_revenue_and_shown_as_returned()
    {
        var tenant = NewTenant("rev-part");
        var store = NewConnection(tenant.Id, "HEPSIBURADA", "ACTIVE");
        var order = NewOrder(tenant.Id, store.Id, "part", Noon, 300m, "DELIVERED");
        var kept = NewLine(order, "part-kept", 100m, 1m);
        var sentBack = NewLine(order, "part-back", 200m, 1m);
        var claim = new ReturnClaim
        {
            Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = store.Id, OrderId = order.Id, ExternalClaimId = "claim-part",
            Status = ReturnClaimStatus.Completed, RawStatus = "Completed", LastRemoteModifiedAt = Now, CreatedAt = Now, UpdatedAt = Now
        };
        // A rejected claim line does not take anything off.
        var rejected = new ReturnClaim
        {
            Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = store.Id, OrderId = order.Id, ExternalClaimId = "claim-rejected",
            Status = ReturnClaimStatus.Completed, RawStatus = "Completed", LastRemoteModifiedAt = Now, CreatedAt = Now, UpdatedAt = Now
        };
        try
        {
            await SeedAsync(tenant, [store], db =>
            {
                db.Orders.Add(order);
                db.ShipmentPackages.Add(NewPackage(order, "part-1", 300m, ShipmentPackageStatus.Delivered));
                db.OrderLines.AddRange(kept, sentBack);
                db.ReturnClaims.AddRange(claim, rejected);
                db.ReturnLines.AddRange(
                    new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = sentBack.Id, ExternalLineId = "claim-part-1", Quantity = 1m },
                    new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = rejected.Id, OrderLineId = kept.Id, ExternalLineId = "claim-rejected-1", Quantity = 1m, RawStatus = "Rejected" });
            });

            var row = Assert.Single((await PerformanceAsync(tenant.Id)).Current);

            Assert.Equal(100m, row.Amount);
            Assert.Equal(1, row.OrderCount);
            Assert.Equal(1m, row.ProductQuantity);
            Assert.Equal(200m, row.ReturnedAmount);
            Assert.Equal(1, row.ReturnedOrderCount);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task A_store_waiting_for_credentials_keeps_its_sales_and_a_hidden_one_does_not_and_Trendyol_days_stay_shifted()
    {
        var tenant = NewTenant("rev-conn");
        var draft = NewConnection(tenant.Id, "TRENDYOL", "DRAFT");
        var hidden = NewConnection(tenant.Id, "HEPSIBURADA", "HIDDEN");
        // Trendyol stores Turkey wall time labelled UTC: 23:30 "UTC" is 23:30 in Istanbul, still the 15th, while
        // 01:30 "UTC" on the 16th is the 16th in Istanbul.
        var lateEvening = NewOrder(tenant.Id, draft.Id, "draft-1", new DateTimeOffset(2026, 10, 15, 23, 30, 0, TimeSpan.Zero), 150m, "DELIVERED");
        var nextDay = NewOrder(tenant.Id, draft.Id, "draft-2", new DateTimeOffset(2026, 10, 16, 1, 30, 0, TimeSpan.Zero), 70m, "DELIVERED");
        var hiddenOrder = NewOrder(tenant.Id, hidden.Id, "hidden-1", Noon, 500m, "DELIVERED");
        try
        {
            await SeedAsync(tenant, [draft, hidden], db =>
            {
                db.Orders.AddRange(lateEvening, nextDay, hiddenOrder);
                db.ShipmentPackages.AddRange(
                    NewPackage(lateEvening, "draft-1-p", 150m, ShipmentPackageStatus.Delivered),
                    NewPackage(nextDay, "draft-2-p", 70m, ShipmentPackageStatus.Delivered),
                    NewPackage(hiddenOrder, "hidden-1-p", 500m, ShipmentPackageStatus.Delivered));
            });

            var performance = await PerformanceAsync(tenant.Id);

            var row = Assert.Single(performance.Current);
            Assert.Equal(draft.Id, row.ConnectionId);
            Assert.Equal(150m, row.Amount);
            Assert.Contains(performance.Channels, channel => channel.ConnectionId == draft.Id);
            Assert.DoesNotContain(performance.Channels, channel => channel.ConnectionId == hidden.Id);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // O19: a rebuild that moves the badge counts is announced, so open panels re-read the snapshot; an unchanged
    // rebuild is not, and the announcement is no data change for the refresher.
    [PostgreSqlFact]
    public async Task A_rebuild_that_moves_the_counts_is_announced_once()
    {
        var tenant = NewTenant("dash-ann");
        var store = NewConnection(tenant.Id, "TRENDYOL", "ACTIVE");
        var order = NewOrder(tenant.Id, store.Id, "ann", Noon, 100m, "DELIVERED");
        try
        {
            await SeedAsync(tenant, [store], db =>
            {
                db.Orders.Add(order);
                db.DashboardSnapshots.Add(new DashboardSnapshot { TenantId = tenant.Id, UpdatedAt = Now.AddMinutes(-1), PendingByPlatformJson = "{}", Version = 1 });
            });
            await using (var db = fixture.CreateContext())
            {
                db.ReturnClaims.Add(new ReturnClaim
                {
                    Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = store.Id, OrderId = order.Id, ExternalClaimId = "claim-ann",
                    Status = ReturnClaimStatus.ActionRequired, RawStatus = "WaitingInAction", LastRemoteModifiedAt = Now, CreatedAt = Now, UpdatedAt = Now
                });
                await db.SaveChangesAsync();
            }

            await using (var db = fixture.CreateContext())
                await new DashboardReadService(db, new FixedClock(Now)).RebuildTenantAsync(tenant.Id, CancellationToken.None);
            await using (var db = fixture.CreateContext())
                await new DashboardReadService(db, new FixedClock(Now.AddMinutes(1))).RebuildTenantAsync(tenant.Id, CancellationToken.None);

            await using var verification = fixture.CreateContext();
            Assert.Equal(1, (await verification.DashboardSnapshots.SingleAsync(x => x.TenantId == tenant.Id)).PendingReturns);
            var announcement = Assert.Single(await verification.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id && x.AggregateType == DashboardSnapshotRefreshPolicy.SnapshotAggregateType).ToListAsync());
            Assert.Equal("dashboard", announcement.ResourceType);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    private async Task<Application.DashboardPerformanceView> PerformanceAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var result = await new DashboardReadService(db, new FixedClock(Now)).PerformanceAsync(tenantId, Day, Day, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private static Tenant NewTenant(string code) => new()
    {
        Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..20], DisplayName = code, Status = RecordStatus.Active,
        Timezone = "Europe/Istanbul", CreatedAt = Now, UpdatedAt = Now, Version = 1
    };

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string status) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION",
        DisplayName = $"{platformCode} {status}", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = status, ApiVersion = "V1"
    };

    private static Order NewOrder(Guid tenantId, Guid connectionId, string code, DateTimeOffset orderedAt, decimal amount, string status) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = code, OrderNumber = code, Currency = "TRY",
        GrossAmount = amount, NetAmount = amount, OrderedAt = orderedAt, LastRemoteModifiedAt = orderedAt, CustomerSnapshotJson = "{}",
        ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = status, CreatedAt = Now, UpdatedAt = Now, Version = 1
    };

    private static ShipmentPackage NewPackage(Order order, string externalId, decimal amount, ShipmentPackageStatus status) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = order.TenantId, ConnectionId = order.ConnectionId, OrderId = order.Id, ExternalPackageId = externalId,
        GrossAmount = amount, NetAmount = amount, Status = status, RawStatus = status.ToString(), StatusOccurredAt = Now,
        MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced, CreatedAt = Now, UpdatedAt = Now, Version = 1
    };

    private static OrderLine NewLine(Order order, string externalId, decimal unitPrice, decimal quantity) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = order.TenantId, OrderId = order.Id, ExternalLineId = externalId, Sku = externalId,
        TitleSnapshot = externalId, OrderedQuantity = quantity, UnitPrice = unitPrice, VatRate = 20m, RawStatus = "Delivered"
    };

    private async Task SeedAsync(Tenant tenant, PlatformConnection[] connections, Action<AppDbContext> extra)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(connections);
        extra(db);
        await db.SaveChangesAsync();
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.DashboardSnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
