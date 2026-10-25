using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Sipariş Kârlılık Analizi: an order's profit item by item, as Melontik's analysis of the same order shows it.
public sealed class ReportOrderProfitTests
{
    private static ReportInput Input(Guid store, Guid order, Guid line, decimal? cost, ReportFinance? finance, string storeFront = "TR") =>
        new(new ReportPeriod(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25), new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.Zero)),
            ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "11750129638", DerivedStatus = "DELIVERED", Amount = 465.92m, ListAmount = 470.63m, LineValue = 465.92m,
                RealAt = new DateTimeOffset(2026, 10, 25, 17, 46, 0, TimeSpan.Zero), OrderedAt = new DateTimeOffset(2026, 10, 25, 20, 46, 0, TimeSpan.Zero), StoreFrontCode = storeFront }],
            new Dictionary<Guid, ReportOrderExtra>(),
            [new() { Id = line, OrderId = order, Title = "Anne Penye Bluz", Sku = "RYP00101", Barcode = "RYP00101", UnitPrice = 465.92m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = cost }],
            new Dictionary<Guid, ReportLineExtra> { [line] = new() { Id = line, Commission = "20.6" } }, [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false, Finance: finance);

    private static ReportFinance Unsettled(Guid store) => new([], [], new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = null },
        new Dictionary<Guid, ReportCargoSample> { [store] = new(98.34m, 1) });

    [Fact]
    public void An_unsettled_trendyol_order_is_costed_as_melontik_costs_it()
    {
        var (store, order, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var input = Input(store, order, line, 160m, Unsettled(store));

        var view = Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));

        // 25 Oct 2026, #11750129638: 465.92 − 160 cost − 95.98 commission (%20.6) − 98.34 cargo − 13.19 service fee − 4.24
        // withholding (1 % of 423.56) + 6.78 net VAT (42.36 sales VAT − 14.55 cost − 16.00 commission − 16.39 cargo − 2.20 fee).
        decimal Cost(string key) => view.Costs.Single(cost => cost.Key == key).Amount;
        Assert.Equal((95.98m, 98.34m, 13.19m, 4.24m, -6.78m), (Cost("commission"), Cost("cargo"), Cost("serviceFee"), Cost("stoppage"), Cost("netVat")));
        Assert.True(view.Costs.Single(cost => cost.Key == "cargo").Estimated);
        Assert.Equal((465.92m, 160m, 100.95m), (view.Amount, view.Cost!.Value, view.Profit!.Value));
        Assert.Equal((63.09m, 21.67m), (view.ProfitOnCost!.Value, view.Margin!.Value));
        Assert.False(view.MicroExport);
        Assert.Equal("SOLD", view.State);
    }

    [Fact]
    public void An_order_without_a_cost_has_no_profit_and_a_micro_export_order_pays_the_international_fee()
    {
        var (store, order, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var noCost = Input(store, order, line, null, Unsettled(store));
        var unknown = Assert.Single(ReportOrderProfit.Build(noCost, ReportBuilder.Facts(noCost).Orders));
        Assert.Null(unknown.Cost);
        Assert.Null(unknown.Profit);

        var abroad = Input(store, order, line, 160m, Unsettled(store), "GR");
        var micro = Assert.Single(ReportOrderProfit.Build(abroad, ReportBuilder.Facts(abroad).Orders));
        Assert.True(micro.MicroExport);
        // 10.99 + VAT and 6 % of the sale (27.96); no Turkish withholding tax or sales VAT.
        Assert.Equal(41.15m, micro.Costs.Single(cost => cost.Key == "serviceFee").Amount);
        Assert.Equal(0m, micro.Costs.Single(cost => cost.Key == "stoppage").Amount);
    }
}
