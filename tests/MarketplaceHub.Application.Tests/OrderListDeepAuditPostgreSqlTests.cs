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

    // The column filters show, next to each platform and cargo choice, the orders that choice would list under
    // the other filters and the status tab; a column's own choice does not narrow its counts.
    [PostgreSqlFact]
    public async Task FacetCountsEqualTheListTotalOfEachChoice()
    {
        var tenantId = await SeedAsync("ol-fac");
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);
            foreach (var tab in new[] { "ALL", "NEW" })
            {
                var query = new OrderListQuery(Status: tab);
                var facets = await service.OrderFacetsAsync(tenantId, query, CancellationToken.None);
                var total = (await service.OrdersAsync(tenantId, 50, 1, null, query, CancellationToken.None)).TotalCount;
                Assert.NotEmpty(facets.Platforms);
                Assert.Equal(total, facets.Platforms.Values.Sum());
                foreach (var (platform, count) in facets.Platforms)
                    Assert.Equal(count, (await service.OrdersAsync(tenantId, 50, 1, null, query with { Platforms = [platform] }, CancellationToken.None)).TotalCount);
                foreach (var (cargo, count) in facets.Cargos)
                    Assert.Equal(count, (await service.OrdersAsync(tenantId, 50, 1, null, query with { Cargo = cargo }, CancellationToken.None)).TotalCount);
                var platformPicked = await service.OrderFacetsAsync(tenantId, query with { Platforms = [facets.Platforms.Keys.First()] }, CancellationToken.None);
                Assert.Equal(facets.Platforms, platformPicked.Platforms);
            }
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // An order with some products cancelled stays open with the rest: it is in its own tab and in Kısmi İptal (a
    // delivered one too), its cancelled products are in İptal, every line goes to the list (the panel strikes the
    // cancelled ones), and Tümü finds them with the partial-cancel filter.
    [PostgreSqlFact]
    public async Task APartlyCancelledOrderIsInItsOwnTabInKismiIptalAndItsCancelledProductsInIptal()
    {
        var tenantId = await SeedAsync("ol-part");
        Guid open, delivered;
        try
        {
            await using (var seedDb = fixture.CreateContext())
            {
                var connectionId = await seedDb.PlatformConnections.Where(x => x.TenantId == tenantId).Select(x => x.Id).SingleAsync();
                var now = DateTimeOffset.UtcNow;
                Guid Add(string suffix, string derived, ShipmentPackageStatus packageStatus, params (decimal Ordered, decimal Cancelled)[] lines)
                {
                    var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = $"part-{suffix}", OrderNumber = $"part-{suffix}", Currency = "TRY", GrossAmount = 10m, NetAmount = 10m, OrderedAt = now.AddMinutes(-30), LastRemoteModifiedAt = now, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = derived, CreatedAt = now, UpdatedAt = now, Version = 1 };
                    seedDb.Orders.Add(order);
                    var index = 0;
                    foreach (var (ordered, cancelled) in lines)
                        seedDb.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = $"{++index}", Sku = $"SKU-{suffix}-{index}", TitleSnapshot = "Ürün", OrderedQuantity = ordered, CancelledQuantity = cancelled, UnitPrice = 10m, VatRate = 20m, RawStatus = "Created" });
                    seedDb.ShipmentPackages.Add(new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalPackageId = $"part-{suffix}", Status = packageStatus, RawStatus = packageStatus.ToString(), StatusOccurredAt = now, MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown, CreatedAt = now, UpdatedAt = now, Version = 1 });
                    return order.Id;
                }
                // Two of one product with one cancelled, and a second product cancelled whole.
                open = Add("open", "NEW", ShipmentPackageStatus.New, (2, 1), (1, 1));
                delivered = Add("done", "DELIVERED", ShipmentPackageStatus.Delivered, (1, 0), (1, 1));
                await seedDb.SaveChangesAsync();
            }

            await using var db = fixture.CreateContext();
            var service = NewService(db);
            async Task<List<Guid>> Ids(OrderListQuery query) => (await service.OrdersAsync(tenantId, 50, 1, null, query, CancellationToken.None)).Items.Select(x => x.Id).ToList();

            Assert.Equal(new[] { open, delivered }.OrderBy(x => x), (await Ids(new OrderListQuery(Status: "PARTIALLY_CANCELLED"))).OrderBy(x => x));
            Assert.Contains(open, await Ids(new OrderListQuery(Status: "NEW")));
            var cancelledTab = await Ids(new OrderListQuery(Status: "CANCELLED"));
            Assert.Contains(open, cancelledTab);
            Assert.Contains(delivered, cancelledTab);
            Assert.Equal(new[] { open, delivered }.OrderBy(x => x), (await Ids(new OrderListQuery(PartialCancel: true))).OrderBy(x => x));

            var summary = await service.OrderSummaryAsync(tenantId, new OrderListQuery(), CancellationToken.None);
            Assert.Equal(2, summary.PartiallyCancelled);
            Assert.Equal(summary.Cancelled, (await Ids(new OrderListQuery(Status: "CANCELLED"))).Count);

            var row = (await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Status: "PARTIALLY_CANCELLED"), CancellationToken.None)).Items.Single(x => x.Id == open);
            Assert.Equal(2, row.Lines!.Count);
            Assert.Contains(row.Lines!, line => line.OrderedQuantity == 1 && line.CancelledQuantity == 1);
            Assert.Equal(1, row.ProductQuantity);
            Assert.Equal(1, row.LineCount);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // The micro-export card counts the orders the row's flag marks (Trendyol micro = true), under the list's filters
    // and in any tab, per country; its filter lists exactly those orders.
    [PostgreSqlFact]
    public async Task MicroExportFilterListsTheFlaggedOrdersAndTheSummaryCountsThemPerCountry()
    {
        var tenantId = await SeedAsync("ol-mic");
        try
        {
            await using (var seedDb = fixture.CreateContext())
            {
                var connectionId = await seedDb.PlatformConnections.Where(x => x.TenantId == tenantId).Select(x => x.Id).SingleAsync();
                var now = DateTimeOffset.UtcNow;
                void Add(string suffix, string derived, string customerJson, string invoiceJson, string? storefront)
                    => seedDb.Orders.Add(new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = $"mic-{suffix}", OrderNumber = $"mic-{suffix}", Currency = "TRY", GrossAmount = 10m, NetAmount = 10m, OrderedAt = now.AddMinutes(-20), LastRemoteModifiedAt = now, CustomerSnapshotJson = customerJson, ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = invoiceJson, StoreFrontCode = storefront, DerivedStatus = derived, CreatedAt = now, UpdatedAt = now, Version = 1 });
                Add("sa1", "NEW", "{\"micro\":true}", "{\"countryCode\":\"SA\"}", null);
                Add("sa2", "CANCELLED", "{\"micro\": true}", "{\"countryCode\":\"SA\"}", "SA");
                Add("bg", "DELIVERED", "{\"micro\":true}", "{}", "BG");
                // micro = false (a 3P export) and a foreign address alone are no micro export.
                Add("no1", "NEW", "{\"micro\":false}", "{\"countryCode\":\"DE\"}", "DE");
                Add("no2", "NEW", "{}", "{\"countryCode\":\"SA\"}", "SA");
                await seedDb.SaveChangesAsync();
            }

            await using var db = fixture.CreateContext();
            var service = NewService(db);
            var listed = (await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(MicroExport: true), CancellationToken.None)).Items.Select(x => x.OrderNumber).OrderBy(x => x).ToList();
            Assert.Equal(["mic-bg", "mic-sa1", "mic-sa2"], listed);

            var summary = await service.OrderSummaryAsync(tenantId, new OrderListQuery(), CancellationToken.None, ["ALL"]);
            Assert.Equal(3, summary.MicroExport);
            Assert.Equal(2, summary.MicroExportCountries!["SA"]);
            Assert.Equal(1, summary.MicroExportCountries["BG"]);
            // The count follows the list's filters but not its tab.
            Assert.Equal(1, (await service.OrderSummaryAsync(tenantId, new OrderListQuery(Search: "mic-bg"), CancellationToken.None, ["ALL"])).MicroExport);
            Assert.Equal(3, (await service.OrderSummaryAsync(tenantId, new OrderListQuery(Status: "NEW"), CancellationToken.None, ["ALL"])).MicroExport);
            // The card asks for the ALL tab alone, under a day filter: the count and countries follow the filter. The orders
            // are stored 20 minutes ago, and Trendyol stores its order times three hours ahead of the real instant, so they
            // were really placed about three hours and twenty minutes ago: a window around now misses them, one around that
            // time holds them.
            var around = (double fromHours, double toHours) => new OrderListQuery(DateFrom: DateTimeOffset.UtcNow.AddHours(fromHours), DateTo: DateTimeOffset.UtcNow.AddHours(toHours));
            Assert.Equal(0, (await service.OrderSummaryAsync(tenantId, around(-1, 1), CancellationToken.None, ["ALL"])).MicroExport);
            Assert.Empty((await service.OrdersAsync(tenantId, 50, 1, null, around(-1, 1) with { MicroExport = true }, CancellationToken.None)).Items);
            Assert.Equal(3, (await service.OrdersAsync(tenantId, 50, 1, null, around(-4, -3) with { MicroExport = true }, CancellationToken.None)).Items.Count);
            var today = await service.OrderSummaryAsync(tenantId, around(-4, -3), CancellationToken.None, ["ALL"]);
            Assert.Equal(3, today.MicroExport);
            Assert.Equal(2, today.MicroExportCountries!["SA"]);
            var otherDay = await service.OrderSummaryAsync(tenantId, new OrderListQuery(DateFrom: DateTimeOffset.UtcNow.AddDays(-3), DateTo: DateTimeOffset.UtcNow.AddDays(-2)), CancellationToken.None, ["ALL"]);
            Assert.Equal(0, otherDay.MicroExport);
            // The tab counters (every tab, no names) and a menu counter that asks for one other tab leave the micro-export count out.
            Assert.Equal(0, (await service.OrderSummaryAsync(tenantId, new OrderListQuery(), CancellationToken.None)).MicroExport);
            Assert.Equal(0, (await service.OrderSummaryAsync(tenantId, new OrderListQuery(), CancellationToken.None, ["PENDING"])).MicroExport);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // The dashboard's "Geciken Siparişler" card links to /orders?status=LATE; the list had no such view and opened
    // empty. It lists exactly the orders the card counts.
    [PostgreSqlFact]
    public async Task LateViewListsTheOrdersTheDashboardCountsAsLate()
    {
        var tenantId = await SeedAsync("ol-late");
        try
        {
            await using (var seedDb = fixture.CreateContext())
            {
                var newOrder = await seedDb.Orders.Where(x => x.TenantId == tenantId && x.DerivedStatus == "NEW").OrderBy(x => x.Id).FirstAsync();
                newOrder.ShipmentDueAt = fixture.Now.AddHours(-30);
                await seedDb.SaveChangesAsync();
            }

            await using var db = fixture.CreateContext();
            var late = await DashboardReadService.LateOrderMetricsQuery(db, tenantId, fixture.Now).Select(x => x.Id).OrderBy(x => x).ToListAsync();
            var page = await NewService(db).OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Status: "LATE"), CancellationToken.None);

            Assert.NotEmpty(late);
            Assert.Equal(late, page.Items.Select(x => x.Id).OrderBy(x => x).ToList());
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

    // Trendyol order #11768706205: the line's barcode and stock code (MZ052BOD-16) were changed on Trendyol only, so no
    // panel variant carries them; its listing id (productCode 1696621714) is linked to the variant still coded
    // MZ051BYC-16. The list and the detail find that variant by the listing and show its photo and model code.
    [PostgreSqlFact]
    public async Task ALineWhoseCodesChangedOnTheMarketplaceIsFoundByItsListingId()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"ol-img-{Guid.NewGuid():N}"[..20], DisplayName = "ol-img", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "ol-img", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Triko Bluz", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = "MZ051BYC-16", SkuNormalized = "MZ051BYC-16", Barcode = "MZ051BYC-16", BarcodeNormalized = "MZ051BYC-16", ModelCode = "MZ052BOD", OptionSignature = "Renk: Kahverengi | Beden: L", CreatedAt = now, UpdatedAt = now };
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = "https://cdn.example.com/mz052bod-kahverengi.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = "ol-img-1", OrderNumber = "ol-img-1", Currency = "TRY", GrossAmount = 10m, NetAmount = 10m, OrderedAt = now, LastRemoteModifiedAt = now, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "NEW", CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.FileAssets.Add(asset);
            seed.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, FileAssetId = asset.Id, MediaRole = "GALLERY", SortOrder = 0, Status = "ACTIVE" });
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = "1696621714", ExternalBarcode = "MZ051BYC-16", ExternalSku = "MZ051BYC-16" });
            seed.Orders.Add(order);
            seed.OrderLines.Add(new OrderLine
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                OrderId = order.Id,
                ExternalLineId = "6139565733",
                Sku = "MZ052BOD-16",
                Barcode = "MZ052BOD-16",
                TitleSnapshot = "Kışlık Saç Örgü Triko Düğmeli Yumuşak Bluz MZ052BOD, L",
                OrderedQuantity = 1,
                UnitPrice = 559.68m,
                VatRate = 10m,
                RawStatus = "ReadyToShip",
                SourceSnapshotJson = """{"sku":"MZ052BOD-16","barcode":"MZ052BOD-16","productCode":1696621714,"contentId":1696621714,"productColor":"Kahverengi","productSize":"L"}"""
            });
            await seed.SaveChangesAsync();
        }
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);
            var listed = Assert.Single(Assert.Single((await service.OrdersAsync(tenant.Id, 50, 1, null, new OrderListQuery(), CancellationToken.None)).Items).Lines!);
            Assert.Equal("https://cdn.example.com/mz052bod-kahverengi.jpg", listed.ImageUrl);
            Assert.Equal("MZ052BOD", listed.ModelCode);
            Assert.Equal(variant.Id, listed.VariantId);
            var detail = await service.OrderAsync(tenant.Id, order.Id, CancellationToken.None);
            var detailLine = Assert.Single(detail.Value!.Lines);
            Assert.Equal("https://cdn.example.com/mz052bod-kahverengi.jpg", detailLine.ImageUrl);
            Assert.Equal("MZ052BOD", detailLine.ModelCode);
            // The invoice workspace and its detail modal read the same catalog match.
            var lines = await db.OrderLines.AsNoTracking().Where(line => line.TenantId == tenant.Id).ToListAsync();
            var catalog = await OrderLineCatalog.LoadAsync(db, tenant.Id, lines, _ => connection.Id, CancellationToken.None);
            var line = Assert.Single(lines);
            Assert.Equal(variant.Id, catalog.Variant(line)?.Id);
            Assert.Equal("https://cdn.example.com/mz052bod-kahverengi.jpg", catalog.CatalogImageUrl(catalog.Variant(line)));
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.MarketplaceVariantLinks.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.ProductMedia.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.FileAssets.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.ProductVariants.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Products.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
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
