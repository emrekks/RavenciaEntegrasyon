using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Stok's Tahmini Kâr takes the months' profit on cost (ReportProfitRate), worked out as the Raporlar page's Net Kâr. The
// deductions come off every sale, so the cost must cover every kept unit too: the profit is worked out only when at least
// 95 % of the kept units carry a cost, and the cost of the rest is estimated by their share of the sales
// (ReportProfitCost, owner 13 Nov 2026); below 95 % there is no rate, as the page shows "Maliyet Eksik".
public sealed class ReportProfitRateTests
{
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);

    // One order: `costed` units at 100 with a cost of 40 each, `uncosted` units at `uncostedPrice` without a cost.
    private static ReportSalesView Report(decimal costed, decimal uncosted, decimal uncostedPrice = 100m, decimal? uncostedCost = null)
    {
        var store = Guid.NewGuid();
        var order = Guid.NewGuid();
        var at = Start.AddHours(12);
        List<ReportLineRow> lines =
        [
            new() { Id = Guid.NewGuid(), OrderId = order, Title = "A", Sku = "A", Barcode = "A", UnitPrice = 100m, OrderedQuantity = costed, VatRate = 10m, CostPrice = 40m }
        ];
        if (uncosted > 0m)
            lines.Add(new() { Id = Guid.NewGuid(), OrderId = order, Title = "B", Sku = "B", Barcode = "B", UnitPrice = uncostedPrice, OrderedQuantity = uncosted, VatRate = 10m, CostPrice = uncostedCost });
        var amount = costed * 100m + uncosted * uncostedPrice;
        var input = new ReportInput(new ReportPeriod(Day, Day, Start, Start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "SHOPIFY", "Shopify", false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "S-1", DerivedStatus = "DELIVERED", Amount = amount, LineValue = amount, RealAt = at, OrderedAt = at }],
            new Dictionary<Guid, ReportOrderExtra>(), lines, new Dictionary<Guid, ReportLineExtra>(), [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        var previous = input with { Period = new ReportPeriod(Day.AddDays(-1), Day.AddDays(-1), Start.AddDays(-1), Start), Orders = [], Lines = [] };
        return ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());
    }

    [Fact]
    public void Below_95_percent_of_the_kept_units_with_a_cost_the_month_has_no_rate()
    {
        // 949 of 1,000 units: 94.9 %.
        var report = Report(costed: 949m, uncosted: 51m);
        Assert.Equal((1000m, 949m), (report.Cost.KeptQuantity, report.Cost.CostedQuantity));

        Assert.False(ReportProfitCost.Of(report.Cost).Usable);
        Assert.Null(ReportProfitRate.Of(report));
        Assert.Contains(report.DataNotes, note => note.Contains("en az %95", StringComparison.Ordinal));
    }

    [Fact]
    public void At_95_percent_the_missing_cost_is_estimated_by_the_sales_share()
    {
        // 950 of 1,000 units: the 38,000 known cost is 40 % of its 95,000 revenue, so the 5,000 of the 50 units without a
        // cost carry an estimated 2,000.
        var report = Report(costed: 950m, uncosted: 50m);
        Assert.Equal(100_000m, report.Cost.KeptRevenue);
        var cost = ReportProfitCost.Of(report.Cost);
        Assert.Equal((true, 38_000m, 2_000m, 50m, 40_000m), (cost.Usable, cost.Known, cost.Estimated, cost.Missing, cost.Total));

        // Not inflated: the rate is the same as with every unit costed at the same ratio, and below what the known cost
        // alone would give.
        var rate = ReportProfitRate.Of(report);
        var whole = ReportProfitRate.Of(Report(costed: 950m, uncosted: 50m, uncostedCost: 40m));
        Assert.NotNull(rate);
        Assert.NotNull(whole);
        Assert.InRange(rate!.Value - whole!.Value, -0.01m, 0.01m);
        Assert.Contains(report.DataNotes, note => note.Contains("Net Kâr tahminidir", StringComparison.Ordinal));
    }

    [Fact]
    public void The_estimate_follows_the_price_of_the_units_without_a_cost()
    {
        // 50 cheap units (20 each, 1,000 of sales) beside 950 at 100: priced at 40 % of their sales, 400, not 50 x 40.
        var cost = ReportProfitCost.Of(Report(costed: 950m, uncosted: 50m, uncostedPrice: 20m).Cost);
        Assert.Equal(400m, cost.Estimated);
    }

    [Fact]
    public void With_every_unit_costed_the_month_has_a_rate_without_an_estimate()
    {
        var report = Report(costed: 1000m, uncosted: 0m);
        Assert.Equal((1000m, 1000m, 40_000m), (report.Cost.KeptQuantity, report.Cost.CostedQuantity, report.Cost.CostTotal));
        Assert.Equal(0m, ReportProfitCost.Of(report.Cost).Estimated);

        // Shopify's cargo, card fees and net VAT come off too; the rate is the page's account over the product cost.
        var rate = ReportProfitRate.Of(report);
        Assert.NotNull(rate);
        Assert.True(rate < (report.Summary.GrossSales - 40_000m) / 40_000m);
    }
}
