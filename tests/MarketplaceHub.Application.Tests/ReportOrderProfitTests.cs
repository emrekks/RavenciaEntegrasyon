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
        decimal Cost(string key) => view.Costs.Single(cost => cost.Key == key).Amount!.Value;
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

    [Fact]
    public void A_trendyol_order_with_no_cargo_invoice_and_no_tariff_has_an_unknown_cargo_and_so_no_profit()
    {
        // 8–24 Jul 2026 on live: the store's finance read starts on 24 Jul, so no invoice and no recent tariff.
        var (store, order, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var unread = new ReportFinance([], [], new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = null });
        foreach (var finance in new[] { unread, null })
        {
            var input = Input(store, order, line, 160m, finance);
            var view = Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));

            var cargo = view.Costs.Single(cost => cost.Key == "cargo");
            Assert.NotEqual("Kargo faturasından", cargo.Source);
            Assert.Null(cargo.Amount);
            Assert.False(cargo.Estimated);
            // The net VAT takes the cargo's VAT off, so it is unknown too.
            Assert.Null(view.Costs.Single(cost => cost.Key == "netVat").Amount);
            Assert.Equal(160m, view.Cost);
            Assert.Null(view.Profit);
            Assert.Null(view.Margin);
        }
    }

    [Fact]
    public void A_billed_trendyol_cargo_is_real_without_a_tariff()
    {
        var (store, order, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var billed = new ReportFinance([], [new() { ConnectionId = store, InvoiceSerialNumber = "DDF2026000001", OrderNumber = "11750129638", ShipmentPackageType = "Gönderi Kargo Bedeli", Amount = 98.34m }],
            new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = null });
        var input = Input(store, order, line, 160m, billed);

        var view = Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));

        var cargo = view.Costs.Single(cost => cost.Key == "cargo");
        Assert.Equal((98.34m, false, "Kargo faturasından"), (cargo.Amount, cargo.Estimated, cargo.Source));
        Assert.Equal(100.95m, view.Profit);
    }

    [Fact]
    public void The_margin_is_taken_over_the_orders_whose_profit_is_known()
    {
        var (store, line) = (Guid.NewGuid(), Guid.NewGuid());
        ReportOrderProfitView Order(decimal? cost, ReportFinance? finance)
        {
            var order = Guid.NewGuid();
            var input = Input(store, order, line, cost, finance);
            return Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));
        }
        var known = Order(160m, Unsettled(store));
        var noCost = Order(null, Unsettled(store));
        var noCargo = Order(160m, null);

        var totals = ReportOrderProfit.Totals([known, noCost, noCargo]);

        // Every order's amount, but the profit and the margin of the one order whose profit is known: 100.95 / 465.92,
        // not 100.95 / 1397.76.
        Assert.Equal(1397.76m, totals.Amount);
        Assert.Equal((100.95m, 465.92m, 1), (totals.Profit, totals.ProfitAmount, totals.ProfitOrders));
        Assert.Equal(21.67m, totals.Margin);
        Assert.Equal((1, 1), (totals.MissingCost, totals.MissingCargo));

        var none = ReportOrderProfit.Totals([noCost, noCargo]);
        Assert.Equal((null, 0m, 0, null), (none.Profit, none.ProfitAmount, none.ProfitOrders, none.Margin));
    }
}
