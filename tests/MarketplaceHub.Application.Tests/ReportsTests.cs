using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReportsTests
{
    // The picked days as stored instants, as text, and a file's periods with the picked days as their outer bounds.
    [Fact]
    public void Picked_validity_days_are_whole_istanbul_days_and_keep_a_files_inner_boundaries()
    {
        var (start, end) = Infrastructure.Imports.CommissionTariffValidity.Bounds(new DateOnly(2026, 10, 29), new DateOnly(2026, 11, 5));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 21, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 21, 0, 0, TimeSpan.Zero), end);
        Assert.Equal(TimeSpan.Zero, start.Offset);
        Assert.Equal((new DateOnly(2026, 10, 29), new DateOnly(2026, 11, 5)), Infrastructure.Imports.CommissionTariffValidity.Days(start, end));
        Assert.Equal("29 Ekim – 5 Kasım 2026", Infrastructure.Imports.CommissionTariffValidity.Text(new DateOnly(2026, 10, 29), new DateOnly(2026, 11, 5)));
        Assert.Equal("29 Aralık 2026 – 2 Ocak 2027", Infrastructure.Imports.CommissionTariffValidity.Text(new DateOnly(2026, 12, 29), new DateOnly(2027, 1, 2)));
        Assert.Equal("5 Kasım 2026", Infrastructure.Imports.CommissionTariffValidity.Text(new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 5)));
        Assert.Equal("COMMISSION_TARIFF_PERIOD_INVALID", Infrastructure.Imports.CommissionTariffValidity.Invalid(new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 4))?.Code);
        Assert.Null(Infrastructure.Imports.CommissionTariffValidity.Invalid(new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 5)));

        // A file of two periods, 29 Ekim 08.00 – 1 Kasım 07.59 and 1 Kasım 08.00 – 5 Kasım 07.59.
        var istanbul = TimeSpan.FromHours(3);
        var boundary = new DateTimeOffset(2026, 11, 1, 8, 0, 0, istanbul).ToUniversalTime();
        IReadOnlyList<(DateTimeOffset, DateTimeOffset)> periods = [(new DateTimeOffset(2026, 10, 29, 8, 0, 0, istanbul).ToUniversalTime(), boundary), (boundary, new DateTimeOffset(2026, 11, 5, 8, 0, 0, istanbul).ToUniversalTime())];
        var outer = Infrastructure.Imports.CommissionTariffValidity.Outer(periods, new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 6));
        Assert.True(outer.Succeeded);
        Assert.Equal([(new DateTimeOffset(2026, 10, 27, 21, 0, 0, TimeSpan.Zero), boundary), (boundary, new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.Zero))], outer.Value!);
        // Days that leave the boundary between the periods outside are refused.
        Assert.Equal("COMMISSION_TARIFF_PERIOD_INVALID", Infrastructure.Imports.CommissionTariffValidity.Outer(periods, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 6)).Error?.Code);
        Assert.Equal("COMMISSION_TARIFF_PERIOD_INVALID", Infrastructure.Imports.CommissionTariffValidity.Outer(periods, new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 31)).Error?.Code);
        // One period moved alone may not overlap the other.
        Assert.True(Infrastructure.Imports.CommissionTariffValidity.Single(new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 31), [periods[1]]).Succeeded);
        Assert.Equal("COMMISSION_TARIFF_PERIOD_INVALID", Infrastructure.Imports.CommissionTariffValidity.Single(new DateOnly(2026, 10, 27), new DateOnly(2026, 11, 1), [periods[1]]).Error?.Code);
    }

    [Theory]
    [InlineData("İSTANBUL", "İstanbul")]
    [InlineData("istanbul", "İstanbul")]
    [InlineData("Istanbul", "İstanbul")]
    [InlineData("  İstanbul Avrupa ", "İstanbul")]
    [InlineData("Kadıköy / İstanbul", "İstanbul")]
    [InlineData("Kadıköy İstanbul", "İstanbul")]
    [InlineData("Afyon", "Afyonkarahisar")]
    [InlineData("K.Maraş", "Kahramanmaraş")]
    [InlineData("KAHRAMANMARAŞ", "Kahramanmaraş")]
    [InlineData("İçel", "Mersin")]
    [InlineData("ÇANAKKALE", "Çanakkale")]
    [InlineData("şanlıurfa", "Şanlıurfa")]
    [InlineData("IĞDIR", "Iğdır")]
    [InlineData("Izmir", "İzmir")]
    public void A_city_text_reads_as_its_province(string raw, string expected) =>
        Assert.Equal(expected, TurkishProvinces.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("34")]
    [InlineData("Kadıköy")]
    [InlineData("Berlin")]
    [InlineData("Ankara İstanbul")]
    public void A_text_that_names_no_single_province_is_unknown(string? raw) =>
        Assert.Null(TurkishProvinces.Normalize(raw));

    [Fact]
    public void Every_province_is_listed_once_and_reads_as_itself()
    {
        Assert.Equal(81, TurkishProvinces.Names.Distinct().Count());
        Assert.All(TurkishProvinces.Names, name => Assert.Equal(name, TurkishProvinces.Normalize(name)));
    }

    [Theory]
    [InlineData(null, "2026-10-01", "2026-10-31", "day")]
    [InlineData(null, "2026-01-01", "2026-05-31", "week")]
    [InlineData(null, "2025-11-01", "2026-10-31", "month")]
    [InlineData("WEEK", "2026-10-01", "2026-10-01", "week")]
    public void The_buckets_follow_the_choice_or_the_period_length(string? requested, string from, string to, string expected) =>
        Assert.Equal(expected, ReportBuckets.Resolve(requested, DateOnly.Parse(from), DateOnly.Parse(to)));

    [Fact]
    public void An_unknown_bucket_name_is_refused_and_weeks_start_on_monday()
    {
        Assert.Null(ReportBuckets.Resolve("year", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)));
        Assert.Equal(new DateOnly(2026, 10, 19), ReportBuckets.Start(new DateOnly(2026, 10, 25), ReportBuckets.Week));
        Assert.Equal(new DateOnly(2026, 10, 1), ReportBuckets.Start(new DateOnly(2026, 10, 25), ReportBuckets.Month));
        Assert.Equal([new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 5)], ReportBuckets.Starts(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8), ReportBuckets.Week).ToArray());
    }

    [Theory]
    [InlineData("TRENDYOL", "number", "21.5", null, null, 43)]
    [InlineData("HEPSIBURADA", "object", "15", "15", null, 36)]
    [InlineData("HEPSIBURADA", null, null, "15", null, 36)]
    [InlineData("N11", null, null, "20,5", null, 41)]
    [InlineData("PTTAVM", null, null, null, "20", 40)]
    public void The_commission_of_kept_units_is_estimated_from_what_the_line_carries(string platform, string? type, string? commission, string? rate, string? komisyon, double expected) =>
        // 2 units kept out of 4 sold, paid 200 for the kept ones (Hepsiburada's amount is per unit: 15% of 100, plus its 20 % VAT).
        Assert.Equal((decimal)expected, ReportBuckets.EstimatedCommission(platform, type, commission, rate, komisyon, 200m, 2m));

    [Theory]
    [InlineData(2, 546, 117.94)]
    [InlineData(1, 273, 58.97)]
    [InlineData(0, 0, 0)]
    public void A_hepsiburada_commission_amount_counts_once_per_kept_unit(int keptUnits, double keptAmount, double expected) =>
        // The live line: 2 x 273 TL at rate 18 carries {"amount": 49.14}, 18% of one unit; with one unit back one stays charged.
        // Hepsiburada charges 20 % VAT on top: 49.14 x 1.20 = 58.97 a unit.
        Assert.Equal((decimal)expected, ReportBuckets.EstimatedCommission("HEPSIBURADA", "object", "49.14", "18", null, (decimal)keptAmount, keptUnits));

    // Live order 4091991027: the line carries {"amount": 232.02} and Hepsiburada's Commission record charged 278.42.
    [Fact]
    public void A_hepsiburada_estimate_matches_the_commission_record_hepsiburada_charges()
    {
        Assert.Equal(278.42m, ReportBuckets.EstimatedCommission("HEPSIBURADA", "object", "232.02", "18", null, 1094.15m, 1m));
    }

    [Theory]
    [InlineData("PAZARAMA", "number", "10")]
    [InlineData("SHOPIFY", "number", "10")]
    [InlineData("TRENDYOL", "number", "0")]
    [InlineData("TRENDYOL", "number", "250")]
    [InlineData("TRENDYOL", "string", "abc")]
    public void A_line_without_a_usable_commission_has_none(string platform, string? type, string? commission) =>
        Assert.Null(ReportBuckets.EstimatedCommission(platform, type, commission, null, null, 200m, 2m));

    [Fact]
    public void A_district_is_shown_in_turkish_title_case_and_a_code_is_no_district()
    {
        Assert.Equal("Kadıköy", ReportBuckets.District("KADIKÖY"));
        Assert.Equal("Şişli Merkez", ReportBuckets.District(null, " şişli  merkez "));
        Assert.Null(ReportBuckets.District("1234", " "));
    }

    [Theory]
    [InlineData("UPLOADED", MarketplaceInvoiceStatus.NotInvoiced, false, "INVOICED")]
    [InlineData("PENDING", MarketplaceInvoiceStatus.Invoiced, true, "NOT_INVOICED")]
    [InlineData(null, MarketplaceInvoiceStatus.Unknown, true, "INVOICED")]
    [InlineData(null, MarketplaceInvoiceStatus.Received, false, "INVOICED")]
    [InlineData(null, MarketplaceInvoiceStatus.NotInvoiced, false, "NOT_INVOICED")]
    [InlineData(null, MarketplaceInvoiceStatus.Unknown, false, "UNKNOWN")]
    public void A_package_is_put_in_an_invoice_bucket_by_its_evidence(string? manual, MarketplaceInvoiceStatus marketplace, bool panel, string expected) =>
        Assert.Equal(expected, ReportBuilder.InvoiceBucket(new ReportPackageRow { ManualInvoiceStatus = manual, MarketplaceInvoiceStatus = marketplace, PanelInvoiced = panel }));

    [Fact]
    public void Products_rise_and_fall_against_the_previous_period_and_one_sold_only_before_falls_to_nothing()
    {
        static ReportProductView Product(Guid id, string title, decimal sold) => new(id, null, title, title, "", "", sold, 0m, null, sold * 100m, 0m, 0m, NetAmount: sold * 100m);
        Guid up = Guid.NewGuid(), down = Guid.NewGuid(), gone = Guid.NewGuid(), fresh = Guid.NewGuid();
        var (products, rising, falling) = ReportBuilder.Movers(
            [Product(up, "Up", 9m), Product(down, "Down", 2m), Product(fresh, "Fresh", 3m)],
            [Product(up, "Up", 4m), Product(down, "Down", 5m), Product(gone, "Gone", 6m)],
            new Dictionary<Guid, decimal> { [up] = 12m });

        Assert.Equal(["Up", "Fresh"], rising.Select(x => x.Title));
        Assert.Equal(["Gone", "Down"], falling.Select(x => x.Title));
        var lost = falling[0];
        Assert.Equal((0m, 6m, 0m), (lost.SoldQuantity, lost.PreviousSoldQuantity!.Value, lost.SalesAmount));
        Assert.Equal((4m, 12m), (products.Single(x => x.Title == "Up").PreviousSoldQuantity!.Value, products.Single(x => x.Title == "Up").Available!.Value));
        Assert.Equal(0m, products.Single(x => x.Title == "Fresh").PreviousSoldQuantity);
        Assert.Null(products.Single(x => x.Title == "Down").Available);
    }
}

// The whole report over a seeded store: every breakdown adds up to the totals, unknowns go to "Bilinmiyor", and the
// test store stays out unless asked for.
[Collection("postgres-integration")]
public sealed class PostgreSqlReportsTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // 20 Oct 2026, 10:30 in Istanbul; the report asks for 15 Oct.
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 15);
    // 15 Oct, 12:00 in Istanbul; Trendyol order times are real instants like every platform's.
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TrendyolNoon = Noon;

    [PostgreSqlFact]
    public async Task The_report_adds_up_and_reads_cities_commission_cost_customers_and_cargo()
    {
        var tenant = NewTenant("rep-all");
        var trendyol = NewConnection(tenant.Id, "TRENDYOL", "PRODUCTION");
        var stage = NewConnection(tenant.Id, "HEPSIBURADA", "STAGE");
        var brand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", CreatedAt = Now, UpdatedAt = Now };
        var category = new Category { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Koltuk Kılıfı", NormalizedName = "KOLTUK KILIFI", Path = "Koltuk Kılıfı", CreatedAt = Now, UpdatedAt = Now };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kılıf", Description = "", Status = ProductStatus.Active, BrandId = brand.Id, CategoryId = category.Id, CreatedAt = Now, UpdatedAt = Now };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = "KLF-1", SkuNormalized = "KLF-1", OptionSignature = "Renk: Siyah", CostPrice = 40m, CreatedAt = Now, UpdatedAt = Now };

        var istanbul = NewOrder(tenant.Id, trendyol.Id, "t-ist", TrendyolNoon, 200m, "DELIVERED", """{"city":"İSTANBUL","district":"KADIKÖY"}""", """{"customerId":"c1"}""");
        var istanbulLine = NewLine(istanbul, "t-ist-1", 100m, 2m, """{"commission":21.5}""", variant.Id);
        var ankara = NewOrder(tenant.Id, trendyol.Id, "t-ank", TrendyolNoon, 100m, "DELIVERED", """{"city":"Ankara"}""", """{"customerId":"c1"}""");
        var ankaraLine = NewLine(ankara, "t-ank-1", 100m, 1m, """{"commission":21.5}""", null);
        ankaraLine.ShippedQuantity = 1m;
        ankaraLine.DeliveredQuantity = 1m;
        ankaraLine.ReturnedQuantity = 1m;
        var noCity = NewOrder(tenant.Id, trendyol.Id, "t-none", TrendyolNoon, 50m, "DELIVERED", "{}", "{}");
        var noCityLine = NewLine(noCity, "t-none-1", 50m, 1m, "{}", null);
        var cancelled = NewOrder(tenant.Id, trendyol.Id, "t-cancel", TrendyolNoon, 80m, "CANCELLED", """{"city":"İzmir"}""", "{}");
        var cancelledLine = NewLine(cancelled, "t-cancel-1", 80m, 1m, """{"cancelReason":"Stok yok"}""", null);
        var test = NewOrder(tenant.Id, stage.Id, "h-test", Noon, 999m, "DELIVERED", """{"city":"Bursa"}""", "{}");
        var testLine = NewLine(test, "h-test-1", 999m, 1m, "{}", null);
        var claim = new ReturnClaim
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = trendyol.Id,
            OrderId = ankara.Id,
            ExternalClaimId = "claim-ank",
            Status = ReturnClaimStatus.Completed,
            RawStatus = "Completed",
            ReasonText = "Beden uymadı",
            LastRemoteModifiedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        var package = NewPackage(istanbul, "pkg-ist", 200m, ShipmentPackageStatus.Delivered, "Aras Kargo");
        istanbul.ShipmentDueAt = TrendyolNoon.AddHours(20);
        try
        {
            await using (var seed = fixture.CreateContext())
            {
                seed.Tenants.Add(tenant);
                seed.PlatformConnections.AddRange(trendyol, stage);
                seed.Brands.Add(brand);
                seed.Categories.Add(category);
                seed.Products.Add(product);
                seed.ProductVariants.Add(variant);
                seed.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 6, Available = 6 });
                seed.Orders.AddRange(istanbul, ankara, noCity, cancelled, test);
                seed.OrderLines.AddRange(istanbulLine, ankaraLine, noCityLine, cancelledLine, testLine);
                seed.ReturnClaims.Add(claim);
                seed.ShipmentPackages.Add(package);
                // A billed parcel the desi is learned from: the report reads it through orders, lines and products.
                seed.MarketplaceCargoInvoiceItems.Add(new MarketplaceCargoInvoiceItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = trendyol.Id, InvoiceSerialNumber = "KRG-1", LineNo = 1, ShipmentPackageType = "Gönderi Kargo Bedeli", ParcelUniqueId = "p-1", OrderNumber = istanbul.OrderNumber, Amount = 93.05m, Desi = 1, RawJson = "{}", FetchedAt = Now });
                seed.OrderStatusHistory.AddRange(
                    new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = istanbul.Id, PackageId = package.Id, CanonicalStatus = "SHIPPED", RawStatus = "Shipped", SourceEventId = "s1", OccurredAt = TrendyolNoon.AddHours(24), RecordedAt = Now },
                    new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = istanbul.Id, PackageId = package.Id, CanonicalStatus = "DELIVERED", RawStatus = "Delivered", SourceEventId = "s2", OccurredAt = TrendyolNoon.AddHours(72), RecordedAt = Now });
                await seed.SaveChangesAsync();
            }

            var report = await ReportAsync(tenant.Id, includeTest: false);

            var summary = report.Summary;
            Assert.Equal(3, summary.Orders);
            Assert.Equal(1, summary.CancelledOrders);
            // One cancelled unit of the five ordered (Trendyol counts the rate over units, cancelled ones included).
            Assert.Equal(0.2m, summary.CancelRate);
            Assert.Equal(4m, summary.SoldQuantity);
            Assert.Equal(1m, summary.ReturnedQuantity);
            Assert.Equal(350m, summary.GrossSales);
            Assert.Equal(250m, summary.NetRevenue);
            Assert.Equal(1, summary.ReturnClaims);
            Assert.Equal(1, summary.OrdersWithoutCity);
            // Stok raporu over the same data: six units at a cost of 40, the two Istanbul units kept that day; each unit is
            // priced at its panel price or counted as missing one.
            await using (var db = fixture.CreateContext())
            {
                var stock = await new ReportsService(db, new FixedClock(Now)).StockAsync(tenant.Id, new ReportStockQuery(Day, Day, null, false), CancellationToken.None);
                Assert.True(stock.Succeeded, stock.Error?.Message);
                var stockSummary = stock.Value!.Summary;
                Assert.Equal((6m, 240m, 2m, 3m), (stockSummary.Units, stockSummary.Capital, stockSummary.SoldUnits, stockSummary.CoverDays!.Value));
                Assert.Equal("Koltuk Kılıfı", Assert.Single(stock.Value.Categories).Label);
                Assert.Equal(stockSummary.Units, stockSummary.PricedUnits + stockSummary.MissingPriceUnits);
            }

            // Every breakdown adds up to the totals; the unknown bucket comes last.
            Assert.Equal(summary.SoldQuantity, report.Cities.Sum(x => x.SoldQuantity));
            Assert.Equal(summary.Orders, report.Cities.Sum(x => x.Orders));
            Assert.Equal(["İstanbul", "Ankara", "Bilinmiyor"], report.Cities.Select(x => x.Label).ToArray());
            Assert.False(report.Cities[^1].Known);
            Assert.Equal("Kadıköy", Assert.Single(report.Cities[0].Children).Label);
            Assert.Equal(summary.NetRevenue, report.Platforms.Sum(x => x.NetRevenue));
            Assert.Equal("TRENDYOL", Assert.Single(report.Platforms).Key);
            Assert.Equal(summary.SoldQuantity, report.Brands.Sum(x => x.SoldQuantity));
            Assert.Equal(["Ravencia", "Bilinmiyor"], report.Brands.Select(x => x.Label).ToArray());
            Assert.Equal(["Koltuk Kılıfı", "Bilinmiyor"], report.Categories.Select(x => x.Label).ToArray());

            // Commission: 21.5% of the 200 paid for the two kept Istanbul units; the returned Ankara unit keeps none and
            // the line without a commission is not covered.
            Assert.Equal(43m, report.Commission.EstimatedCommission);
            Assert.Equal(200m, report.Commission.CoveredRevenue);
            Assert.Equal(0.8m, report.Commission.Coverage);
            Assert.Equal(157m, report.Commission.EstimatedPayout);

            // Cost: only the two Istanbul units carry one; net profit also takes the commission off.
            Assert.Equal(3m, report.Cost.KeptQuantity);
            Assert.Equal(2m, report.Cost.CostedQuantity);
            Assert.Equal(80m, report.Cost.CostTotal);
            Assert.Equal(120m, report.Cost.GrossProfit);
            Assert.Equal(77m, report.Cost.NetProfit);

            // One customer ordered twice; the order without an identity is left out.
            Assert.Equal(1, report.Customers.UniqueCustomers);
            Assert.Equal(1, report.Customers.RepeatCustomers);
            Assert.Equal(1, report.Customers.UnidentifiedOrders);

            var cargo = Assert.Single(report.Cargo);
            Assert.Equal("Aras Kargo", cargo.Label);
            Assert.Equal(24m, cargo.AverageHoursToShip);
            Assert.Equal(48m, cargo.AverageHoursToDeliver);
            Assert.Equal(1, cargo.LateShipments);

            Assert.Equal("Stok yok", Assert.Single(report.Cancellations.Reasons).Reason);
            Assert.Equal("Beden uymadı", Assert.Single(report.ReturnReasons).Reason);
            Assert.Equal(1, report.Invoices.Buckets.Single(x => x.Key == "NOT_INVOICED").Packages);
            var seller = Assert.Single(report.Stock.BestSellers, x => x.VariantId == variant.Id);
            Assert.Equal(6m, seller.Available);
            // The three orders came at 12:00 Istanbul time, Monday-first rows.
            Assert.Equal(3, report.Heatmap[((int)Day.DayOfWeek + 6) % 7][12]);
            Assert.Equal(summary.Orders, report.TimeSeries.Sum(x => x.Orders));
            Assert.Contains(report.DataNotes, note => note.Contains("il okunamadı", StringComparison.Ordinal));

            var withTest = await ReportAsync(tenant.Id, includeTest: true);
            Assert.Equal(4, withTest.Summary.Orders);
            Assert.Contains(withTest.Cities, x => x.Label == "Bursa");
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // 23-30 Sep 2026: a Saudi micro-export order's accepted claim had no refund on Trendyol's statement and Trendyol's
    // sales report did not count it; the panel counts such a return only once the statement records it.
    [PostgreSqlFact]
    public async Task A_micro_export_return_counts_once_trendyols_statement_records_it()
    {
        var tenant = NewTenant("rep-mx");
        var trendyol = NewConnection(tenant.Id, "TRENDYOL", "PRODUCTION");
        var saudi = NewOrder(tenant.Id, trendyol.Id, "t-sa", TrendyolNoon, 300m, "DELIVERED", "{}", "{}");
        saudi.StoreFrontCode = "SA";
        var saudiLine = NewLine(saudi, "t-sa-1", 100m, 3m, "{}", null);
        var domestic = NewOrder(tenant.Id, trendyol.Id, "t-tr", TrendyolNoon, 100m, "DELIVERED", """{"city":"Ankara"}""", "{}");
        // Listed at 110, paid 100 after the marketplace's discount.
        domestic.GrossAmount = 110m;
        var domesticLine = NewLine(domestic, "t-tr-1", 100m, 1m, "{}", null);
        // 10 Sep 2026: a Romanian order Trendyol marked returned (line, package and order) with no refund on the statement.
        var romania = NewOrder(tenant.Id, trendyol.Id, "t-ro", TrendyolNoon, 50m, "RETURNED", "{}", "{}");
        romania.StoreFrontCode = "RO";
        var romaniaLine = NewLine(romania, "t-ro-1", 50m, 1m, "{}", null);
        romaniaLine.ShippedQuantity = 1m;
        romaniaLine.DeliveredQuantity = 1m;
        romaniaLine.ReturnedQuantity = 1m;
        var romaniaPackage = NewPackage(romania, "pkg-ro", 50m, ShipmentPackageStatus.Returned, "Trendyol Express");
        ReturnClaim Claim(Order order, string id) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = trendyol.Id,
            OrderId = order.Id,
            ExternalClaimId = id,
            Status = ReturnClaimStatus.Approved,
            RawStatus = "Accepted",
            ReasonText = "Bedeni/Ebatı Büyük Geldi",
            LastRemoteModifiedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        var saudiClaim = Claim(saudi, "claim-sa");
        var domesticClaim = Claim(domestic, "claim-tr");
        try
        {
            await using (var seed = fixture.CreateContext())
            {
                seed.Tenants.Add(tenant);
                seed.PlatformConnections.Add(trendyol);
                seed.Orders.AddRange(saudi, domestic, romania);
                seed.OrderLines.AddRange(saudiLine, domesticLine, romaniaLine);
                seed.ShipmentPackages.Add(romaniaPackage);
                seed.ReturnClaims.AddRange(saudiClaim, domesticClaim);
                seed.ReturnLines.AddRange(
                    new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = saudiClaim.Id, OrderLineId = saudiLine.Id, ExternalLineId = "sa-1", Quantity = 3m, RawStatus = "Accepted" },
                    new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = domesticClaim.Id, OrderLineId = domesticLine.Id, ExternalLineId = "tr-1", Quantity = 1m, RawStatus = "Accepted" });
                await seed.SaveChangesAsync();
            }

            // No refund on the statement yet: only the domestic unit is back (the Romanian order's sale is kept throughout).
            var before = await ReportAsync(tenant.Id, includeTest: false);
            Assert.Equal(1m, before.Summary.ReturnedQuantity);
            Assert.Equal(100m, before.Summary.ReturnedAmount);
            // At list price, as Trendyol's sales report shows returns.
            Assert.Equal(110m, before.Summary.ReturnedListAmount);
            Assert.Equal(1, before.Summary.ReturnClaims);
            Assert.Equal(350m, before.Summary.NetRevenue);
            // The overview reads the same rule (owner, 29 Oct 2026: a micro-export return keeps its revenue, Trendyol only
            // takes the fee off the payout): the same revenue, returned amount and kept units on both pages.
            var overviewBefore = await OverviewAsync(tenant.Id);
            Assert.Equal(before.Summary.NetRevenue, overviewBefore.Sum(x => x.Amount));
            Assert.Equal(before.Summary.ReturnedAmount, overviewBefore.Sum(x => x.ReturnedAmount));
            Assert.Equal(before.Summary.KeptQuantity, overviewBefore.Sum(x => x.ProductQuantity));
            Assert.Equal(before.Summary.Orders, overviewBefore.Sum(x => x.OrderCount));
            Assert.Equal(1, overviewBefore.Sum(x => x.ReturnedOrderCount));
            // The Romanian order's package was sent and its sale kept, so it counts as a package.
            Assert.Equal(1, overviewBefore.Sum(x => x.PackageCount));

            await using (var refund = fixture.CreateContext())
            {
                refund.MarketplaceFinanceTransactions.Add(new MarketplaceFinanceTransaction
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    ConnectionId = trendyol.Id,
                    PlatformCode = "TRENDYOL",
                    Source = "SETTLEMENTS",
                    ExternalId = "ret-sa",
                    RequestedType = "Return",
                    TransactionType = "İade",
                    Category = MarketplaceFinanceCategories.Return,
                    TransactionDate = Now,
                    OrderNumber = "t-sa",
                    Debt = 300m,
                    RawJson = "{}",
                    FirstSeenAt = Now,
                    UpdatedAt = Now
                });
                await refund.SaveChangesAsync();
            }

            var after = await ReportAsync(tenant.Id, includeTest: false);
            Assert.Equal(4m, after.Summary.ReturnedQuantity);
            Assert.Equal(400m, after.Summary.ReturnedAmount);
            Assert.Equal(410m, after.Summary.ReturnedListAmount);
            Assert.Equal(2, after.Summary.ReturnClaims);
            Assert.Equal(50m, after.Summary.NetRevenue);
            var overviewAfter = await OverviewAsync(tenant.Id);
            Assert.Equal(after.Summary.NetRevenue, overviewAfter.Sum(x => x.Amount));
            Assert.Equal(after.Summary.ReturnedAmount, overviewAfter.Sum(x => x.ReturnedAmount));
            Assert.Equal(after.Summary.KeptQuantity, overviewAfter.Sum(x => x.ProductQuantity));
            // The panel's Ciro (returns included) is the report's Satış Tutarı, over the same orders (O5, 2 Nov 2026).
            Assert.Equal(after.Summary.GrossSales, overviewAfter.Sum(x => x.Amount + x.ReturnedAmount));
            Assert.Equal(after.Summary.OrdersWithAmount, overviewAfter.Sum(x => x.OrderCount));
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // A Hepsiburada store shipping with Aras: the previous period's order is costed at Aras's desi table as the period's
    // is, not at hepsiJET's barem for an order below 400 TL (the previous period once read no package at all).
    [PostgreSqlFact]
    public async Task The_previous_periods_platform_cargo_reads_its_packages_carrier()
    {
        var tenant = NewTenant("rep-prev-cargo");
        var hepsiburada = NewConnection(tenant.Id, "HEPSIBURADA", "PRODUCTION");
        var today = NewOrder(tenant.Id, hepsiburada.Id, "h-today", Noon, 150m, "DELIVERED", "{}", "{}");
        var todayLine = NewLine(today, "h-today-1", 150m, 1m, "{}", null);
        var yesterday = NewOrder(tenant.Id, hepsiburada.Id, "h-yesterday", Noon.AddDays(-1), 150m, "DELIVERED", "{}", "{}");
        var yesterdayLine = NewLine(yesterday, "h-yesterday-1", 150m, 1m, "{}", null);
        try
        {
            await using (var seed = fixture.CreateContext())
            {
                seed.Tenants.Add(tenant);
                seed.PlatformConnections.Add(hepsiburada);
                seed.Orders.AddRange(today, yesterday);
                seed.OrderLines.AddRange(todayLine, yesterdayLine);
                seed.ShipmentPackages.AddRange(
                    NewPackage(today, "pkg-today", 150m, ShipmentPackageStatus.Delivered, "Aras Kargo"),
                    NewPackage(yesterday, "pkg-yesterday", 150m, ShipmentPackageStatus.Delivered, "Aras Kargo"));
                await seed.SaveChangesAsync();
            }

            var report = await ReportAsync(tenant.Id, includeTest: false);

            // One desi with Aras: 89.92 + 20 % VAT (hepsiJET's barem would be 43.99 + VAT).
            var cargo = Assert.IsType<ReportCargoCostView>(report.Costs.Cargo);
            Assert.Equal(107.90m, cargo.PlatformEstimated);
            var previous = Assert.IsType<ReportPreviousAccountView>(report.PreviousAccount);
            var previousCargo = Assert.IsType<ReportCargoCostView>(previous.Costs.Cargo);
            Assert.Equal(1, previousCargo.PlatformOrders);
            Assert.Equal(107.90m, previousCargo.PlatformEstimated);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // Live, 1-2 Nov 2026, order 11772267262: list 1,127.17, seller discount 76.28, Trendyol discount 150.00 (75 on each
    // of two lines), paid 900.89. Trendyol pays its discount to the seller, so the revenue is 1,050.89 and the discount
    // shown is the seller's 76.28, on the overview and in the reports alike.
    [PostgreSqlFact]
    public async Task The_marketplaces_own_discount_is_revenue_and_not_the_sellers_discount()
    {
        var tenant = NewTenant("rep-ty-discount");
        var trendyol = NewConnection(tenant.Id, "TRENDYOL", "PRODUCTION");
        var order = NewOrder(tenant.Id, trendyol.Id, "t-tydisc", TrendyolNoon, 900.89m, "CREATED", "{}", "{}");
        order.GrossAmount = 1127.17m;
        var first = NewLine(order, "t-tydisc-1", 450.44m, 1m, """{"commission":16.1}""", null);
        first.PlatformDiscount = 75m;
        var second = NewLine(order, "t-tydisc-2", 450.45m, 1m, """{"commission":16.1}""", null);
        second.PlatformDiscount = 75m;
        var package = NewPackage(order, "pkg-tydisc", 900.89m, ShipmentPackageStatus.New, "Trendyol Express");
        package.GrossAmount = 1127.17m;
        package.DiscountAmount = 226.28m;
        try
        {
            await using (var seed = fixture.CreateContext())
            {
                seed.Tenants.Add(tenant);
                seed.PlatformConnections.Add(trendyol);
                seed.Orders.Add(order);
                seed.OrderLines.AddRange(first, second);
                seed.ShipmentPackages.Add(package);
                await seed.SaveChangesAsync();
            }

            var report = await ReportAsync(tenant.Id, includeTest: false);
            Assert.Equal(1050.89m, report.Summary.GrossSales);
            Assert.Equal(1050.89m, report.Summary.NetRevenue);
            Assert.Equal(76.28m, report.Summary.DiscountAmount);
            // Commission on the price before Trendyol's discount: 16.1 % of 525.44 and of 525.45, 84.60 each.
            Assert.Equal(169.20m, report.Commission.EstimatedCommission);

            var overview = Assert.Single(await OverviewAsync(tenant.Id));
            Assert.Equal(1050.89m, overview.Amount);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    // Trendyol's service fee and withholding tax invoices name only their payout: they are split over the orders that
    // payout paid and count with those orders (owner, 5 Nov 2026), whatever day they were billed on. A share of an order
    // of another period stays out of this one, a share of an order never imported and an invoice whose payout lines are
    // not read yet or that names no payout stay apart, unlinked.
    [PostgreSqlFact]
    public async Task Per_sale_fee_invoices_are_split_over_the_orders_of_their_payout()
    {
        var tenant = NewTenant("rep-fee-split");
        var trendyol = NewConnection(tenant.Id, "TRENDYOL", "PRODUCTION");
        var orderA = NewOrder(tenant.Id, trendyol.Id, "t-a", TrendyolNoon, 100m, "DELIVERED", """{"city":"Ankara"}""", "{}");
        var lineA = NewLine(orderA, "t-a-1", 100m, 1m, """{"commission":20}""", null);
        var orderB = NewOrder(tenant.Id, trendyol.Id, "t-b", TrendyolNoon, 50m, "DELIVERED", """{"city":"İzmir"}""", "{}");
        var lineB = NewLine(orderB, "t-b-1", 50m, 1m, """{"commission":20}""", null);
        // An order of 10 Oct, another period.
        var old = NewOrder(tenant.Id, trendyol.Id, "t-old", TrendyolNoon.AddDays(-5), 150m, "DELIVERED", """{"city":"Bursa"}""", "{}");
        var oldLine = NewLine(old, "t-old-1", 150m, 1m, """{"commission":20}""", null);
        var billed = TrendyolNoon.AddDays(3);
        MarketplaceFinanceTransaction Record(string id, string source, string requestedType, string? type, string category, DateTimeOffset at,
            string? orderNumber = null, string? package = null, string? payout = null, decimal debt = 0m, decimal credit = 0m, decimal? commission = null) => new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = trendyol.Id,
                PlatformCode = "TRENDYOL",
                Source = source,
                ExternalId = id,
                RequestedType = requestedType,
                TransactionType = type,
                Category = category,
                TransactionDate = at,
                OrderNumber = orderNumber,
                ShipmentPackageId = package,
                PaymentOrderId = payout,
                Country = source == "SETTLEMENTS" ? "Türkiye" : null,
                Debt = debt,
                Credit = credit,
                CommissionAmount = commission,
                RawJson = "{}",
                FirstSeenAt = Now,
                UpdatedAt = Now
            };
        const string Others = "OTHER_FINANCIALS";
        const string Fee = "Platform Hizmet Bedeli";
        var records = new[]
        {
            // Payout PO-1 paid both orders of the period (packages P-A and P-B).
            Record("s-a", "SETTLEMENTS", "Sale", "Satış", MarketplaceFinanceCategories.Sale, TrendyolNoon, "t-a", "P-A", "PO-1", credit: 100m, commission: 20m),
            Record("s-b", "SETTLEMENTS", "Sale", "Satış", MarketplaceFinanceCategories.Sale, TrendyolNoon, "t-b", "P-B", "PO-1", credit: 50m, commission: 10m),
            // Payout PO-0 paid the old order and one the panel never imported.
            Record("s-old", "SETTLEMENTS", "Sale", "Satış", MarketplaceFinanceCategories.Sale, TrendyolNoon.AddDays(-5), "t-old", "P-O", "PO-0", credit: 150m, commission: 30m),
            Record("s-ghost", "SETTLEMENTS", "Sale", "Satış", MarketplaceFinanceCategories.Sale, TrendyolNoon.AddDays(-5), "t-ghost", "P-G", "PO-0", credit: 50m, commission: 10m),
            // PO-1's invoices, billed three days after the period.
            Record("SF-1", Others, "DeductionInvoices", Fee, MarketplaceFinanceCategories.ServiceFee, billed, payout: "PO-1", debt: 26.38m),
            Record("ST-1", Others, "Stoppage", "E-ticaret Stopajı", MarketplaceFinanceCategories.Stoppage, billed, payout: "PO-1", debt: 1.25m),
            // Billed in the period: PO-0's fee, a fee of a payout whose lines are not read yet, and one naming no payout.
            Record("SF-0", Others, "DeductionInvoices", Fee, MarketplaceFinanceCategories.ServiceFee, TrendyolNoon, payout: "PO-0", debt: 26.38m),
            Record("SF-9", Others, "DeductionInvoices", Fee, MarketplaceFinanceCategories.ServiceFee, TrendyolNoon, payout: "PO-9", debt: 39.57m),
            Record("SF-N", Others, "DeductionInvoices", Fee, MarketplaceFinanceCategories.ServiceFee, TrendyolNoon, debt: 13.19m)
        };
        try
        {
            await using (var seed = fixture.CreateContext())
            {
                seed.Tenants.Add(tenant);
                seed.PlatformConnections.Add(trendyol);
                seed.Orders.AddRange(orderA, orderB, old);
                seed.OrderLines.AddRange(lineA, lineB, oldLine);
                seed.MarketplaceFinanceTransactions.AddRange(records);
                seed.MarketplaceFinanceSyncStates.Add(new MarketplaceFinanceSyncState
                {
                    TenantId = tenant.Id,
                    ConnectionId = trendyol.Id,
                    CoveredFrom = Now.AddDays(-90),
                    CoveredThrough = Now,
                    LastSyncedAt = Now
                });
                await seed.SaveChangesAsync();
            }

            var report = await ReportAsync(tenant.Id, includeTest: false);
            var fee = report.Costs.Lines.Single(line => line.Category == MarketplaceFinanceCategories.ServiceFee);
            var tax = report.Costs.Lines.Single(line => line.Category == MarketplaceFinanceCategories.Stoppage);
            // 13.19 a package, nothing estimated; the tax by sale, 100 : 50.
            Assert.Equal((26.38m, 26.38m, 0m, 2), (fee.Amount, fee.OrderLinked, fee.Estimated, fee.Records));
            Assert.Equal((1.25m, 1.25m, 0m, 2), (tax.Amount, tax.OrderLinked, tax.Estimated, tax.Records));
            // Apart: the never-imported order's share (13.19), the unread payout's 39.57 and the 13.19 naming no payout;
            // the old order's share is in its own period. No withholding tax is left apart.
            var apart = report.Costs.Unlinked!.Single(line => line.Category == MarketplaceFinanceCategories.ServiceFee);
            Assert.Equal((65.95m, 3), (apart.Amount, apart.Records));
            Assert.DoesNotContain(report.Costs.Unlinked!, line => line.Category == MarketplaceFinanceCategories.Stoppage);
            Assert.Contains(report.DataNotes, note => note.Contains("39,57 ₺", StringComparison.Ordinal) && note.Contains("ödeme kayıtları okununca", StringComparison.Ordinal));
            Assert.Contains(report.DataNotes, note => note.StartsWith("13,19 ₺", StringComparison.Ordinal));

            await using var db = fixture.CreateContext();
            var page = await new ReportsService(db, new FixedClock(Now)).OrderProfitabilityAsync(tenant.Id,
                new ReportOrderProfitQuery(Day, Day, null, null, null, false, 0, 50), CancellationToken.None);
            Assert.True(page.Succeeded, page.Error?.Message);
            var a = page.Value!.Items.Single(order => order.OrderNumber == "t-a").Costs;
            var b = page.Value!.Items.Single(order => order.OrderNumber == "t-b").Costs;
            Assert.Equal((13.19m, false), (a.Single(cost => cost.Key == "serviceFee").Amount, a.Single(cost => cost.Key == "serviceFee").Estimated));
            Assert.Equal((0.83m, false), (a.Single(cost => cost.Key == "stoppage").Amount, a.Single(cost => cost.Key == "stoppage").Estimated));
            Assert.Equal((13.19m, false), (b.Single(cost => cost.Key == "serviceFee").Amount, b.Single(cost => cost.Key == "serviceFee").Estimated));
            Assert.Equal((0.42m, false), (b.Single(cost => cost.Key == "stoppage").Amount, b.Single(cost => cost.Key == "stoppage").Estimated));
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    private async Task<ReportSalesView> ReportAsync(Guid tenantId, bool includeTest)
    {
        await using var db = fixture.CreateContext();
        var result = await new ReportsService(db, new FixedClock(Now)).SalesAsync(tenantId, new ReportSalesQuery(Day, Day, null, null, null, includeTest), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private async Task<IReadOnlyList<DashboardPerformanceRowView>> OverviewAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var result = await new DashboardReadService(db, new FixedClock(Now)).PerformanceAsync(tenantId, Day, Day, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!.Current;
    }

    private static Tenant NewTenant(string code) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = $"{code}-{Guid.NewGuid():N}"[..20],
        DisplayName = code,
        Status = RecordStatus.Active,
        Timezone = "Europe/Istanbul",
        CreatedAt = Now,
        UpdatedAt = Now,
        Version = 1
    };

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string environment) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = environment,
        DisplayName = $"{platformCode} {environment}",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1"
    };

    private static Order NewOrder(Guid tenantId, Guid connectionId, string code, DateTimeOffset orderedAt, decimal amount, string status, string address, string customer) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ExternalOrderId = code,
        OrderNumber = code,
        Currency = "TRY",
        GrossAmount = amount,
        NetAmount = amount,
        OrderedAt = orderedAt,
        LastRemoteModifiedAt = orderedAt,
        CustomerSnapshotJson = customer,
        ShipmentAddressSnapshotJson = address,
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = status,
        CreatedAt = Now,
        UpdatedAt = Now,
        Version = 1
    };

    private static OrderLine NewLine(Order order, string externalId, decimal unitPrice, decimal quantity, string snapshot, Guid? variantId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = order.TenantId,
        OrderId = order.Id,
        VariantId = variantId,
        ExternalLineId = externalId,
        Sku = externalId,
        TitleSnapshot = externalId,
        SourceSnapshotJson = snapshot,
        OrderedQuantity = quantity,
        UnitPrice = unitPrice,
        VatRate = 20m,
        RawStatus = "Delivered"
    };

    private static ShipmentPackage NewPackage(Order order, string externalId, decimal amount, ShipmentPackageStatus status, string cargo) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = order.TenantId,
        ConnectionId = order.ConnectionId,
        OrderId = order.Id,
        ExternalPackageId = externalId,
        CargoProviderExternalId = cargo,
        GrossAmount = amount,
        NetAmount = amount,
        Status = status,
        RawStatus = status.ToString(),
        StatusOccurredAt = Now,
        MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced,
        CreatedAt = Now,
        UpdatedAt = Now,
        Version = 1
    };

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.MarketplaceCargoInvoiceItems.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderStatusHistory.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceFinanceTransactions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceFinanceSyncStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InventoryItems.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductVariants.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Products.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Brands.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Categories.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
