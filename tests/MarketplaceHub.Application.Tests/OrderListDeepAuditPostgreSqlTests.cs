using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Deep audit 2026-10-20 S12, S13, S14, S29: every tab counter is the number of distinct orders its
// tab lists for the same filters, the new tabs count their orders, a split order shows its live
// package, and the order search ignores case the Turkish way.
[Collection("postgres-integration")]
public sealed class OrderListDeepAuditPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task TabCountersEqualTheListTotalsForTheSameFilters()
    {
        var tenantId = await SeedAsync("ol-sum");
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);
            var all = new OrderListQuery();
            var summary = await service.OrderSummaryAsync(tenantId, all, CancellationToken.None);

            foreach (var (tab, count) in new[] { ("ALL", summary.All), ("NEW", summary.New), ("PROCESSING", summary.Processing), ("SHIPPED", summary.Shipped), ("DELIVERED", summary.Delivered), ("CANCELLED", summary.Cancelled), ("MANUAL_REVIEW", summary.ManualReview), ("RETURNED", summary.Returned), ("UNDELIVERED", summary.Undelivered), ("PENDING", summary.Pending) })
            {
                var page = await service.OrdersAsync(tenantId, 50, 1, null, all with { Status = tab }, CancellationToken.None);
                Assert.True(page.TotalCount == count, $"{tab}: list {page.TotalCount}, counter {count}");
            }
            // The split order (one delivered, one new package) is one order, in Yeni and in Teslim Edildi.
            Assert.Equal(5, summary.All);
            Assert.Equal(2, summary.New);
            Assert.Equal(1, summary.Delivered);
            Assert.Equal(1, summary.ManualReview);
            Assert.Equal(1, summary.Undelivered);

            // The menu counter asks for one tab only: that counter matches, the others are not counted, and a cached
            // answer is the same.
            var menu = await service.OrderSummaryAsync(tenantId, all, CancellationToken.None, ["PENDING"], allowCached: true);
            Assert.Equal(summary.Pending, menu.Pending);
            Assert.Equal(0, menu.All);
            Assert.Equal(menu, await service.OrderSummaryAsync(tenantId, all, CancellationToken.None, ["PENDING"], allowCached: true));

            // The counters follow the list's other filters too.
            var searched = await service.OrderSummaryAsync(tenantId, all with { Search = "ışık" }, CancellationToken.None);
            Assert.Equal(1, searched.All);
            Assert.Equal(1, searched.New);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    [PostgreSqlFact]
    public async Task SearchIgnoresCaseTheTurkishWayAndSplitOrderShowsItsLivePackage()
    {
        var tenantId = await SeedAsync("ol-srch");
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);

            foreach (var term in new[] { "ışık", "IŞIK", "Işık", "lamba", "ol-srch-a" })
            {
                var page = await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Search: term), CancellationToken.None);
                Assert.True(page.TotalCount == 1, $"{term}: {page.TotalCount}");
            }
            var percent = await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Search: "%"), CancellationToken.None);
            Assert.Equal(0, percent.TotalCount);

            var split = Assert.Single((await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Search: "ol-srch-split"), CancellationToken.None)).Items);
            Assert.Equal("NEW", split.DerivedStatus);
            // The cancelled package is the newest, yet the live ones come first.
            Assert.Equal(["ol-srch-split-done", "ol-srch-split-new", "ol-srch-split-cancel"], split.Packages!.Select(package => package.ExternalPackageId));
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    private MarketplaceSalesService NewService(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, fixture.TimeProvider);

    // Five orders: a new one whose product is "IŞIK LAMBA", a split order (delivered + new package),
    // one in manual review, one undelivered, and one cancelled.
    private async Task<Guid> SeedAsync(string code)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..20], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        var minute = 0;
        void AddOrder(string suffix, string derived, string title, params (string Id, ShipmentPackageStatus Status, string Raw)[] packages)
        {
            minute++;
            var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = $"{code}-{suffix}", OrderNumber = $"{code}-{suffix}", Currency = "TRY", GrossAmount = 10m, NetAmount = 10m, OrderedAt = now.AddMinutes(-minute), LastRemoteModifiedAt = now, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = derived, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.Orders.Add(order);
            db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = "1", Sku = $"SKU-{suffix}", TitleSnapshot = title, OrderedQuantity = packages.Length, UnitPrice = 10m, VatRate = 20m, RawStatus = "Created" });
            var at = 0;
            foreach (var (id, status, raw) in packages)
                db.ShipmentPackages.Add(new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalPackageId = $"{code}-{id}", Status = status, RawStatus = raw, StatusOccurredAt = now.AddMinutes(-60 + at++), MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown, CreatedAt = now, UpdatedAt = now, Version = 1 });
        }
        AddOrder("a", "NEW", "IŞIK LAMBA", ("a", ShipmentPackageStatus.New, "Created"));
        // The newer delivered package is listed first by time; the live new one must still be shown.
        AddOrder("split", "NEW", "Kitap", ("split-new", ShipmentPackageStatus.New, "Created"), ("split-done", ShipmentPackageStatus.Delivered, "Delivered"), ("split-cancel", ShipmentPackageStatus.Cancelled, "Cancelled"));
        AddOrder("review", "MANUAL_REVIEW", "Kalem", ("review", ShipmentPackageStatus.ManualReview, "Weird"));
        AddOrder("und", "UNDELIVERED", "Defter", ("und", ShipmentPackageStatus.Undelivered, "UnDelivered"));
        AddOrder("cnl", "CANCELLED", "Silgi", ("cnl", ShipmentPackageStatus.Cancelled, "Cancelled"));
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderStatusHistory.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PackageLineAllocations.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
