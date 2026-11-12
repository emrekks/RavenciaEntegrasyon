using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Stok's Tahmini Kâr takes the months' profit on cost (ReportProfitRate), worked out as the Raporlar page's Net Kâr. The
// deductions come off every sale and the product cost only off the units that carry one, so with a unit kept without a
// cost the profit would be too high by that unit's cost: then there is no rate, as the page shows "Maliyet Eksik"
// (9.1.3 of the operating guide).
public sealed class ReportProfitRateTests
{
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);

    private static ReportSalesView Report(decimal? secondCost)
    {
        var store = Guid.NewGuid();
        var order = Guid.NewGuid();
        var at = Start.AddHours(12);
        List<ReportLineRow> lines =
        [
            new() { Id = Guid.NewGuid(), OrderId = order, Title = "A", Sku = "A", Barcode = "A", UnitPrice = 100m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 40m },
            new() { Id = Guid.NewGuid(), OrderId = order, Title = "B", Sku = "B", Barcode = "B", UnitPrice = 100m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = secondCost }
        ];
        var input = new ReportInput(new ReportPeriod(Day, Day, Start, Start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "SHOPIFY", "Shopify", false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "S-1", DerivedStatus = "DELIVERED", Amount = 200m, LineValue = 200m, RealAt = at, OrderedAt = at }],
            new Dictionary<Guid, ReportOrderExtra>(), lines, new Dictionary<Guid, ReportLineExtra>(), [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        var previous = input with { Period = new ReportPeriod(Day.AddDays(-1), Day.AddDays(-1), Start.AddDays(-1), Start), Orders = [], Lines = [] };
        return ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());
    }

    [Fact]
    public void A_unit_kept_without_a_cost_leaves_the_month_without_a_rate()
    {
        var report = Report(secondCost: null);
        Assert.Equal((2m, 1m), (report.Cost.KeptQuantity, report.Cost.CostedQuantity));

        Assert.Null(ReportProfitRate.Of(report));
    }

    [Fact]
    public void With_every_unit_costed_the_month_has_a_rate()
    {
        var report = Report(secondCost: 60m);
        Assert.Equal((2m, 2m, 100m), (report.Cost.KeptQuantity, report.Cost.CostedQuantity, report.Cost.CostTotal));

        // Shopify's cargo, card fees and net VAT come off too; the rate is the page's account over the product cost.
        var rate = ReportProfitRate.Of(report);
        Assert.NotNull(rate);
        Assert.True(rate < (report.Summary.GrossSales - 100m) / 100m);
    }
}
