using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The costs of the platforms whose statement the panel does not read, from their 2026 tariffs
// (docs/KARGO-TARIFELERI-2026.md).
public sealed class ReportPlatformCostsTests
{
    [Fact]
    public void Hepsiburada_takes_the_barem_for_hepsijet_below_400_tl_and_the_desi_table_otherwise()
    {
        var barem = ReportPlatformCosts.For("HEPSIBURADA", 350m, 291.67m, false, null, "hepsiJET");
        Assert.Equal(75.99m * 1.2m, barem.Cargo);
        Assert.Equal(10.99m * 1.2m, barem.ServiceFee);
        Assert.Equal(2.9167m, barem.Stoppage);
        Assert.Equal(0m, barem.PaymentFee);

        Assert.Equal(43.99m * 1.2m, ReportPlatformCosts.For("HEPSIBURADA", 150m, 0m, false, null, null).Cargo);
        // Aras has no barem; a 3 desi parcel above 400 TL takes hepsiJET's 3 desi price.
        Assert.Equal(89.92m * 1.2m, ReportPlatformCosts.For("HEPSIBURADA", 350m, 0m, false, null, "Aras Kargo").Cargo);
        Assert.Equal(99.99m * 1.2m, ReportPlatformCosts.For("HEPSIBURADA", 900m, 0m, false, 3m, "hepsiJET").Cargo);
    }

    [Fact]
    public void N11_and_pazarama_add_the_post_fee_and_charge_their_service_fees()
    {
        var n11 = ReportPlatformCosts.For("N11", 500m, 0m, false, null, "Aras Kargo");
        Assert.Equal(90.50m * 1.0235m * 1.2m, n11.Cargo);
        Assert.Equal(500m * 0.0167m * 1.2m, n11.ServiceFee);
        Assert.Equal(70.41m * 1.0235m * 1.2m, ReportPlatformCosts.For("N11", 200m, 0m, false, null, "PTT Kargo").Cargo);

        var pazarama = ReportPlatformCosts.For("PAZARAMA", 100m, 0m, false, null, "Aras Kargo");
        Assert.Equal(80.83m * 1.0235m * 1.2m, pazarama.Cargo);
        Assert.Equal(8.99m * 1.2m, pazarama.ServiceFee);
    }

    [Fact]
    public void Ptt_avm_takes_the_contract_table_with_vat_and_a_one_and_a_half_percent_fee()
    {
        Assert.Equal(77.50m, ReportPlatformCosts.For("PTTAVM", 400m, 0m, false, 0.5m, null).Cargo);
        Assert.Equal(78.75m, ReportPlatformCosts.For("PTTAVM", 400m, 0m, false, null, null).Cargo);
        Assert.Equal(81.25m, ReportPlatformCosts.For("PTTAVM", 400m, 0m, false, 2m, null).Cargo);
        Assert.Equal(138.75m, ReportPlatformCosts.For("PTTAVM", 400m, 0m, false, 10m, null).Cargo);
        Assert.Equal(6m, ReportPlatformCosts.For("PTTAVM", 400m, 0m, false, null, null).ServiceFee);
    }

    [Fact]
    public void Shopify_pays_120_tl_of_cargo_and_the_card_fees_but_no_withholding_tax()
    {
        var shopify = ReportPlatformCosts.For("SHOPIFY", 1000m, 833.33m, false, null, null);
        Assert.Equal(120m, shopify.Cargo);
        Assert.Equal(42.9m + 20m + 0.25m, shopify.PaymentFee);
        Assert.Equal(0m, shopify.Stoppage);
        Assert.Equal(0m, shopify.ServiceFee);
        // The customer pays a Shopify return; a marketplace return's cargo comes back at the seller's cost.
        Assert.Equal(120m, ReportPlatformCosts.For("SHOPIFY", 1000m, 0m, true, null, null).Cargo);
        Assert.Equal(2m * 78.75m, ReportPlatformCosts.For("PTTAVM", 400m, 0m, true, null, null).Cargo);
    }

    [Fact]
    public void A_pazarama_order_takes_the_commission_its_money_movements_net_out_to_plus_vat()
    {
        var store = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 23);
        var start = new DateTimeOffset(2026, 10, 22, 21, 0, 0, TimeSpan.Zero);
        var at = start.AddHours(12);
        var (read, unread) = (Guid.NewGuid(), Guid.NewGuid());
        var (lineA, lineB, lineC) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "PAZARAMA", "Pazarama", false) },
            [
                new() { Id = read, ConnectionId = store, OrderNumber = "1", DerivedStatus = "DELIVERED", Amount = 800m, LineValue = 800m, RealAt = at, OrderedAt = at },
                new() { Id = unread, ConnectionId = store, OrderNumber = "2", DerivedStatus = "DELIVERED", Amount = 500m, LineValue = 500m, RealAt = at, OrderedAt = at }
            ],
            new Dictionary<Guid, ReportOrderExtra>(),
            [
                new() { Id = lineA, OrderId = read, Title = "Ceket", Sku = "A", UnitPrice = 600m, OrderedQuantity = 1m, VatRate = 20m },
                new() { Id = lineB, OrderId = read, Title = "Bluz", Sku = "B", UnitPrice = 200m, OrderedQuantity = 1m, VatRate = 20m },
                new() { Id = lineC, OrderId = unread, Title = "Etek", Sku = "C", UnitPrice = 500m, OrderedQuantity = 1m, VatRate = 20m }
            ],
            new Dictionary<Guid, ReportLineExtra>(), [], [], new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false,
            OrderCommissions: new Dictionary<Guid, decimal> { [read] = 96m });
        var previous = new ReportInput(input.Period, input.Granularity, input.Timezone, input.Channels, [], new Dictionary<Guid, ReportOrderExtra>(), [],
            new Dictionary<Guid, ReportLineExtra>(), [], [], input.Names, false);

        var report = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());

        // 96 TL without VAT, 115.20 with it, shared 3 : 1 over the lines; the order not read yet has no commission.
        Assert.Equal(115.2m, report.Commission.EstimatedCommission);
        Assert.Equal(800m, report.Commission.CoveredRevenue);
        Assert.Equal(115.2m, report.Costs.Commission!.Combined);
    }

    [Fact]
    public void A_report_without_trendyol_is_costed_from_the_platforms_tariffs()
    {
        var (hb, shop) = (Guid.NewGuid(), Guid.NewGuid());
        var day = new DateOnly(2026, 10, 23);
        var start = new DateTimeOffset(2026, 10, 22, 21, 0, 0, TimeSpan.Zero);
        var at = start.AddHours(12);
        var (a, b, free) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (lineA, lineB) = (Guid.NewGuid(), Guid.NewGuid());
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView>
            {
                [hb] = new(hb, "HEPSIBURADA", "Hepsiburada", false),
                [shop] = new(shop, "SHOPIFY", "Shopify", false)
            },
            [
                new() { Id = a, ConnectionId = hb, OrderNumber = "A", DerivedStatus = "SHIPPED", Amount = 600m, LineValue = 600m, RealAt = at, OrderedAt = at },
                new() { Id = b, ConnectionId = shop, OrderNumber = "B", DerivedStatus = "SHIPPED", Amount = 1000m, LineValue = 1000m, RealAt = at, OrderedAt = at },
                // No amount (live Shopify orders of 0 TL): not costed, as its sale is not counted.
                new() { Id = free, ConnectionId = shop, OrderNumber = "F", DerivedStatus = "SHIPPED", Amount = 0m, LineValue = 0m, RealAt = at, OrderedAt = at }
            ],
            new Dictionary<Guid, ReportOrderExtra>(),
            [
                new() { Id = lineA, OrderId = a, Title = "Ceket", Sku = "A", UnitPrice = 600m, OrderedQuantity = 1m, VatRate = 20m, Desi = 2m },
                new() { Id = lineB, OrderId = b, Title = "Bluz", Sku = "B", UnitPrice = 1000m, OrderedQuantity = 1m, VatRate = 20m }
            ],
            new Dictionary<Guid, ReportLineExtra> { [lineA] = new() { Id = lineA, CommissionType = "object", Commission = "108", CommissionRate = "18" } },
            [], [new() { Id = Guid.NewGuid(), OrderId = a, CargoProvider = "hepsiJET" }],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        var previous = new ReportInput(input.Period, input.Granularity, input.Timezone, input.Channels, [], new Dictionary<Guid, ReportOrderExtra>(), [],
            new Dictionary<Guid, ReportLineExtra>(), [], [], input.Names, false);

        var costs = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>()).Costs;

        Assert.Equal(ReportCosts.EstimateSource, costs.Source);
        var cargo = Assert.IsType<ReportCargoCostView>(costs.Cargo);
        // Hepsiburada: 600 TL, 2 desi, hepsiJET at 81.99 + VAT; Shopify: 120 TL.
        Assert.Equal(2, cargo.PlatformOrders);
        Assert.Equal(98.39m + 120m, cargo.PlatformEstimated);
        Assert.Equal(98.39m + 120m, cargo.Combined);
        Assert.Equal(108m, costs.Commission!.Combined);
        Assert.Equal(13.19m, costs.Lines.Single(line => line.Category == MarketplaceFinanceCategories.ServiceFee).Amount);
        Assert.Equal(63.15m, costs.Lines.Single(line => line.Category == MarketplaceFinanceCategories.PaymentFee).Amount);
        // 1 % of Hepsiburada's VAT-free sale (500); none on Shopify.
        Assert.Equal(5m, costs.Lines.Single(line => line.Category == MarketplaceFinanceCategories.Stoppage).Amount);
        Assert.NotNull(costs.NetVat);
        Assert.Contains(ReportCosts.Notes(costs, input), note => note.Contains("Shopify", StringComparison.Ordinal));
    }
}
