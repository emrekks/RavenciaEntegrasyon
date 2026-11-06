using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Fiyat Hesapla's hints (owner, 6 Nov 2026): what each store took of the clothing sales, costed as the reports cost them.
public sealed class ReportPriceAssumptionsTests
{
    private static readonly Guid Bluz = Guid.NewGuid();
    private static readonly Guid Canta = Guid.NewGuid();
    private static readonly IReadOnlySet<Guid> Clothing = new HashSet<Guid> { Bluz };

    private sealed class Seed
    {
        public Dictionary<Guid, ReportChannelView> Channels { get; } = [];
        public List<ReportOrderRow> Orders { get; } = [];
        public List<ReportLineRow> Lines { get; } = [];
        public Dictionary<Guid, ReportLineExtra> Extras { get; } = [];
        public List<ReportPackageRow> Packages { get; } = [];
        public Dictionary<Guid, decimal> RealCommissions { get; } = [];

        public Guid Store(string platform, bool test = false)
        {
            var id = Guid.NewGuid();
            Channels[id] = new(id, platform, platform, test);
            return id;
        }

        // One order of one line: its price, its category and the commission on the line as its marketplace sends it.
        public ReportOrderRow Order(Guid store, decimal price, Guid? category, ReportLineExtra extra, string? carrier = null)
        {
            var order = new ReportOrderRow
            {
                Id = Guid.NewGuid(),
                ConnectionId = store,
                OrderNumber = $"o-{Orders.Count}",
                DerivedStatus = "DELIVERED",
                Amount = price,
                ListAmount = price,
                LineValue = price,
                RealAt = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero),
                OrderedAt = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero),
                StoreFrontCode = Channels[store].PlatformCode == "TRENDYOL" ? "TR" : null
            };
            var line = Guid.NewGuid();
            Orders.Add(order);
            Lines.Add(new() { Id = line, OrderId = order.Id, CategoryId = category, Title = "Bluz", Sku = "B1", UnitPrice = price, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 200m });
            Extras[line] = new ReportLineExtra { Id = line, CommissionType = extra.CommissionType, Commission = extra.Commission, CommissionRate = extra.CommissionRate };
            if (carrier is not null) Packages.Add(new() { Id = Guid.NewGuid(), OrderId = order.Id, CargoProvider = carrier, Status = ShipmentPackageStatus.Delivered, Amount = price });
            return order;
        }

        public ReportInput Input(ReportFinance? finance = null) =>
            new(new ReportPeriod(new DateOnly(2026, 9, 8), new DateOnly(2026, 11, 6), new DateTimeOffset(2026, 9, 7, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)),
                ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"), Channels, Orders, new Dictionary<Guid, ReportOrderExtra>(), Lines, Extras, [], Packages,
                new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false, finance, RealCommissions);
    }

    private static readonly ReportLineExtra Trendyol20 = new() { Commission = "20" };

    private static PriceAssumptionsView Build(Seed seed, ReportFinance? finance = null, IReadOnlySet<Guid>? clothing = null, IReadOnlyList<ReportPriceAssumptions.FeeRecord>? fees = null)
    {
        var input = seed.Input(finance);
        return ReportPriceAssumptions.Build(input, ReportBuilder.Facts(input).Orders, clothing ?? Clothing, fees);
    }

    [Theory]
    [InlineData("Giyim / Üst Giyim / Bluz & Tunik / Bluz", true)]
    [InlineData("Kadın Giyim & Aksesuar / Tesettür Giyim / Tesettür Tunik", true)]
    [InlineData("Moda / Kadın Giyim / Kadın Bluz", true)]
    [InlineData("GİYİM > Kadın > Bluz", true)]
    [InlineData("Aksesuar / Çanta / Omuz Çantası", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_category_under_a_giyim_path_is_clothing(string? path, bool clothing) =>
        Assert.Equal(clothing, ReportPriceAssumptions.IsClothingPath(path));

    [Fact]
    public void The_panel_categories_are_told_apart_by_their_marketplace_paths_and_without_any_path_clothing_is_unknown()
    {
        var bag = Guid.NewGuid();
        var blouse = Guid.NewGuid();
        var unmapped = Guid.NewGuid();
        var found = ReportPriceAssumptions.ClothingCategories([(blouse, "Giyim / Üst Giyim / Bluz"), (blouse, "Moda / Kadın Bluz"), (bag, "Aksesuar / Çanta / Omuz Çantası"), (unmapped, null)]);
        Assert.Equal([blouse], found!);
        Assert.Null(ReportPriceAssumptions.ClothingCategories([(unmapped, null), (bag, "  ")]));
    }

    [Fact]
    public void The_rate_is_the_clothing_orders_commission_over_their_sale_and_a_store_with_too_few_has_none()
    {
        var seed = new Seed();
        var trendyol = seed.Store("TRENDYOL");
        // Five blouses at 20 % and 21.5 %, and a bag at 10 % that is not clothing and stays out.
        foreach (var (price, rate) in new[] { (500m, "20"), (500m, "20"), (500m, "20"), (1000m, "21.5"), (500m, "20") })
            seed.Order(trendyol, price, Bluz, new() { Commission = rate });
        seed.Order(trendyol, 2000m, Canta, new() { Commission = "10" });
        // An order of a line whose product is not in the catalogue is not known to be clothing either.
        seed.Order(trendyol, 2000m, null, new() { Commission = "10" });
        var pazarama = seed.Store("PAZARAMA");
        foreach (var _ in Enumerable.Range(0, 4)) seed.Order(pazarama, 625m, Bluz, new());
        seed.Store("TRENDYOL", test: true);

        var view = Build(seed);

        Assert.True(view.ClothingOnly);
        Assert.Equal((60, 5), (view.Days, view.MinimumOrders));
        var store = Assert.Single(view.Stores, item => item.PlatformCode == "TRENDYOL");
        // (4 x 100 + 215) / 3,000 = 20.50 %.
        Assert.Equal((5, 3000m, 20.50m), (store.Orders, store.Sales, store.CommissionRate!.Value));
        Assert.Null(store.CommissionRateBeforeVat);
        var few = Assert.Single(view.Stores, item => item.PlatformCode == "PAZARAMA");
        Assert.Null(few.CommissionRate);
        // Every sale counts when clothing cannot be told apart.
        var all = ReportPriceAssumptions.Build(seed.Input(), ReportBuilder.Facts(seed.Input()).Orders, null);
        Assert.False(all.ClothingOnly);
        Assert.Equal(7, all.Stores.Single(item => item.PlatformCode == "TRENDYOL").Orders);
    }

    [Fact]
    public void Hepsiburadas_rate_carries_the_vat_it_adds_and_shows_the_rate_before_it()
    {
        var seed = new Seed();
        var store = seed.Store("HEPSIBURADA");
        // Hepsiburada's line carries its commission without VAT (90 on 500, 18 %) and charges 1.20 x it.
        foreach (var _ in Enumerable.Range(0, 5))
            seed.Order(store, 500m, Bluz, new() { CommissionType = "object", Commission = "90" });

        var view = Assert.Single(Build(seed).Stores);

        Assert.Equal((21.60m, 18.00m), (view.CommissionRate!.Value, view.CommissionRateBeforeVat!.Value));
        Assert.Equal(0, view.RealCommissionOrders);
    }

    [Fact]
    public void The_own_store_takes_no_commission_and_its_card_fees_are_named()
    {
        var seed = new Seed();
        seed.Store("SHOPIFY");

        var view = Assert.Single(Build(seed).Stores);

        Assert.False(view.TakesCommission);
        Assert.Equal(0m, view.CommissionRate);
        Assert.Equal((120m, "assumption"), (view.Cargo!.Value, view.CargoSource));
        Assert.Equal((6.29m, 0.25m), (view.PaymentFeeRate!.Value, view.PaymentFeeFixed!.Value));
        Assert.Equal((0m, 0m), (view.ServiceFee, view.ServiceFeeRate));
    }

    [Fact]
    public void Trendyols_cargo_is_its_invoice_tariff_and_unknown_without_one()
    {
        var seed = new Seed();
        var store = seed.Store("TRENDYOL");
        seed.Order(store, 500m, Bluz, Trendyol20);
        var finance = new ReportFinance([], [], new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = null },
            new Dictionary<Guid, ReportCargoSample> { [store] = new(98.34m * 40, 40) });

        var priced = Assert.Single(Build(seed, finance).Stores);
        Assert.Equal((98.34m, "invoice"), (priced.Cargo!.Value, priced.CargoSource));
        // The platform fee per order (10.99 + VAT), the Azerbaijan storefront's and the micro-export one.
        Assert.Equal((13.19m, 10.99m, 6m), (priced.ServiceFee, priced.AzerbaijanServiceFee!.Value, priced.InternationalServiceFeeRate!.Value));

        var unknown = Assert.Single(Build(seed).Stores);
        Assert.Null(unknown.Cargo);
        Assert.Null(unknown.CargoSource);
    }

    [Fact]
    public void A_platforms_cargo_is_the_tariff_amount_most_of_its_clothing_orders_take_and_unknown_without_orders()
    {
        var seed = new Seed();
        var n11 = seed.Store("N11");
        // Aras, one desi, above the 300 TL barem: 90.50 + 2.35 % post fee + VAT = 111.15; one order below 150 TL: barem.
        seed.Order(n11, 799m, Bluz, new() { CommissionRate = "20.34" }, "Aras Kargo");
        seed.Order(n11, 899m, Bluz, new() { CommissionRate = "20.34" }, "Aras Kargo");
        seed.Order(n11, 120m, Bluz, new() { CommissionRate = "20.34" }, "Aras Kargo");
        var ptt = seed.Store("PTTAVM");

        var view = Build(seed);

        var priced = view.Stores.Single(item => item.ConnectionId == n11);
        Assert.Equal((111.15m, "tariff"), (priced.Cargo!.Value, priced.CargoSource));
        Assert.Equal((0m, 2.00m), (priced.ServiceFee, priced.ServiceFeeRate));
        var none = view.Stores.Single(item => item.ConnectionId == ptt);
        Assert.Null(none.Cargo);
        Assert.Equal(1.50m, none.ServiceFeeRate);
    }

    [Fact]
    public void Hepsiburadas_service_fee_is_what_its_records_charged_its_billed_clothing_orders()
    {
        var seed = new Seed();
        var store = seed.Store("HEPSIBURADA");
        var orders = Enumerable.Range(0, 6).Select(_ => seed.Order(store, 1000m, Bluz, new() { CommissionType = "object", Commission = "150" }, "hepsiJET")).ToList();
        // Five billed (a commission record of their own), one not yet.
        foreach (var order in orders.Take(5)) seed.RealCommissions[order.Id] = 216m;
        var fees = orders.Take(5).Select(order => new ReportPriceAssumptions.FeeRecord(store, order.OrderNumber, 8.62m))
            .Append(new(store, orders[0].OrderNumber, 13.19m)).ToList();

        var view = Assert.Single(Build(seed, fees: fees).Stores);

        // (5 x 8.62 + 13.19) / 5 billed orders.
        Assert.Equal((11.26m, "records", 5), (view.ServiceFee, view.ServiceFeeSource, view.ServiceFeeOrders));
        Assert.Equal(5, view.RealCommissionOrders);
        // Fewer billed orders than the minimum: the reports' tariff (10.99 + VAT).
        seed.RealCommissions.Remove(orders[0].Id);
        var tariff = Assert.Single(Build(seed, fees: fees).Stores);
        Assert.Equal((13.19m, "tariff", 0), (tariff.ServiceFee, tariff.ServiceFeeSource, tariff.ServiceFeeOrders));
    }

    [Fact]
    public void The_platform_fees_the_hint_shows_are_those_the_reports_cost()
    {
        foreach (var (platform, amount) in new[] { ("HEPSIBURADA", 500m), ("N11", 500m), ("PAZARAMA", 500m), ("PTTAVM", 500m) })
        {
            var (perOrder, ofSale) = ReportPlatformCosts.ServiceFee(platform);
            Assert.Equal(ReportPlatformCosts.For(platform, amount, 0m, false, 1m, null).ServiceFee, perOrder + amount * ofSale);
        }
        Assert.Equal((13.188m, 8.28m), (ReportPlatformCosts.ServiceFee("HEPSIBURADA").PerOrder, ReportPlatformCosts.ServiceFee("PAZARAMA").PerOrder));
    }
}
