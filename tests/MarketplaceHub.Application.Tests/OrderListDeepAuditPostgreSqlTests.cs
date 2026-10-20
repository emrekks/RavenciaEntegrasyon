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
            Assert.Equal(6, summary.All);
            Assert.Equal(3, summary.New);
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
            Assert.Equal(2, searched.All);
            Assert.Equal(2, searched.New);
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

            // "IŞIK LAMBA" and "Işıklı Ayna" both hold "ışık", whatever the case or i letter typed.
            foreach (var (term, expected) in new[] { ("ışık", 2), ("IŞIK", 2), ("Işık", 2), ("işik", 2), ("ışıklı", 1), ("IŞIKLI", 1), ("ayna", 1), ("lamba", 1), ("ol-srch-a", 1) })
            {
                var page = await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Search: term), CancellationToken.None);
                Assert.True(page.TotalCount == expected, $"{term}: {page.TotalCount}");
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

    // O13, O14 and the unverified tab: a Shopify order with a panel status is in that status's tab
    // only, a Hepsiburada order whose package the panel unpacked is back in Yeni, an unverified one
    // has its own tab, and every counter still equals its list.
    [PostgreSqlFact]
    public async Task EveryOrderSitsInTheTabItsBadgeShows()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"ol-tab-{Guid.NewGuid():N}"[..20], DisplayName = "ol-tab", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        PlatformConnection Connection(string platform) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "STAGE", DisplayName = platform, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var shopify = Connection("SHOPIFY");
        var hepsiburada = Connection("HEPSIBURADA");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(shopify, hepsiburada);
            var minute = 0;
            Order AddOrder(PlatformConnection connection, string number, string derived, params (ShipmentPackageStatus Status, string Raw)[] packages)
            {
                minute++;
                var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = number, OrderNumber = number, Currency = "TRY", GrossAmount = 10m, NetAmount = 10m, OrderedAt = now.AddMinutes(-minute), LastRemoteModifiedAt = now.AddHours(-1), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = derived, CreatedAt = now, UpdatedAt = now, Version = 1 };
                seed.Orders.Add(order);
                seed.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = "1", Sku = $"SKU-{number}", TitleSnapshot = number, OrderedQuantity = 1, UnitPrice = 10m, VatRate = 20m, RawStatus = "Created" });
                var index = 0;
                foreach (var (status, raw) in packages)
                    seed.ShipmentPackages.Add(new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalPackageId = $"{number}-{index++}", Status = status, RawStatus = raw, StatusOccurredAt = now.AddHours(-2), MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown, CreatedAt = now, UpdatedAt = now, Version = 1 });
                return order;
            }
            void Manual(Order order, string status) => seed.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, CanonicalStatus = status, RawStatus = $"MANUAL_SHOPIFY_STATUS:{status}", SourceEventId = $"manual:shopify-status:NEW:{Guid.NewGuid():N}", OccurredAt = now, RecordedAt = now });
            // The marketplace says new; the operator marked it shipped.
            Manual(AddOrder(shopify, "ol-tab-ship", "SHIPPED", (ShipmentPackageStatus.New, "unfulfilled")), "SHIPPED");
            // The marketplace says shipped; the operator marked it processing.
            Manual(AddOrder(shopify, "ol-tab-proc", "PROCESSING", (ShipmentPackageStatus.Shipped, "fulfilled")), "PROCESSING");
            // The marketplace says new; the operator marked it undelivered.
            Manual(AddOrder(shopify, "ol-tab-und", "UNDELIVERED", (ShipmentPackageStatus.New, "unfulfilled")), "UNDELIVERED");
            // Without a panel status the packages decide.
            AddOrder(shopify, "ol-tab-plain", "NEW", (ShipmentPackageStatus.New, "unfulfilled"));
            // The panel unpacked the only package: the order is new and unpackaged again.
            AddOrder(hepsiburada, "ol-tab-unpacked", "NEW", (ShipmentPackageStatus.Cancelled, ShipmentPackageSupersession.LocallyUnpackedRawStatus));
            AddOrder(hepsiburada, "ol-tab-unverified", "UNVERIFIED");
            await seed.SaveChangesAsync();
        }
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);
            var summary = await service.OrderSummaryAsync(tenant.Id, new OrderListQuery(), CancellationToken.None);
            async Task<string[]> Tab(string tab) => (await service.OrdersAsync(tenant.Id, 50, 1, null, new OrderListQuery(Status: tab), CancellationToken.None)).Items.Select(item => item.OrderNumber).Order().ToArray();

            Assert.Equal(["ol-tab-plain", "ol-tab-unpacked"], await Tab("NEW"));
            Assert.Equal(["ol-tab-ship"], await Tab("SHIPPED"));
            Assert.Equal(["ol-tab-proc"], await Tab("PROCESSING"));
            Assert.Equal(["ol-tab-und"], await Tab("UNDELIVERED"));
            Assert.Equal(["ol-tab-unverified"], await Tab("UNVERIFIED"));
            Assert.Empty(await Tab("CANCELLED"));
            Assert.Equal(["ol-tab-plain", "ol-tab-proc", "ol-tab-unpacked"], await Tab("PENDING"));
            Assert.Equal(6, summary.All);
            Assert.Equal(1, summary.Unverified);

            foreach (var tab in MarketplaceSalesService.OrderSummaryTabs)
            {
                var page = await service.OrdersAsync(tenant.Id, 50, 1, null, new OrderListQuery(Status: tab), CancellationToken.None);
                var count = (int)typeof(OrderSummaryView).GetProperties().Single(property => string.Equals(property.Name.ToUpperInvariant(), tab.Replace("_", string.Empty), StringComparison.Ordinal)).GetValue(summary)!;
                Assert.True(page.TotalCount == count, $"{tab}: list {page.TotalCount}, counter {count}");
                // Each badge is the tab's status (or one of its statuses).
                if (tab is "NEW" or "SHIPPED" or "UNDELIVERED" or "UNVERIFIED")
                    Assert.All(page.Items, item => Assert.Equal(tab, item.DerivedStatus));
            }
        }
        finally
        {
            await CleanupAsync(tenant.Id);
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
        // A title in normal case: the database (en_US) lowers "I" to "i", never to "ı" (O3).
        AddOrder("b", "NEW", "Işıklı Ayna", ("b", ShipmentPackageStatus.New, "Created"));
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
