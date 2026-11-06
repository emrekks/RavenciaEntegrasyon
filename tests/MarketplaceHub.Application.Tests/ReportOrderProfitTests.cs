using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Sipariş Kârlılık Analizi: an order's profit item by item, as Melontik's analysis of the same order shows it.
public sealed class ReportOrderProfitTests
{
    private static ReportInput Input(Guid store, Guid order, Guid line, decimal? cost, ReportFinance? finance, string storeFront = "TR", int unreadableLines = 0) =>
        new(new ReportPeriod(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25), new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.Zero)),
            ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "11750129638", DerivedStatus = "DELIVERED", Amount = 465.92m, ListAmount = 470.63m, LineValue = 465.92m,
                RealAt = new DateTimeOffset(2026, 10, 25, 17, 46, 0, TimeSpan.Zero), OrderedAt = new DateTimeOffset(2026, 10, 25, 20, 46, 0, TimeSpan.Zero), StoreFrontCode = storeFront, UnreadableLines = unreadableLines }],
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
    public void An_order_with_an_unreadable_line_is_marked_its_profit_is_unknown_and_the_report_names_it()
    {
        // 5 Nov 2026: the unread line's cost is unknown, so is the order's profit; the report counts such orders apart.
        var (store, order, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var input = Input(store, order, line, 160m, Unsettled(store), unreadableLines: 1);

        var view = Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));

        Assert.Equal(1, view.UnreadableLines);
        Assert.Null(view.Cost);
        Assert.Null(view.Profit);
        Assert.Equal(465.92m, view.Amount);
        var previous = input with { Period = new ReportPeriod(input.Period.From.AddDays(-1), input.Period.To.AddDays(-1), input.Period.Start.AddDays(-1), input.Period.Start), Orders = [], Lines = [], LineExtras = new Dictionary<Guid, ReportLineExtra>(), Finance = null };
        var notes = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>()).DataNotes;
        Assert.Contains(notes, note => note.StartsWith("1 siparişte okunamayan ürün satırı var (TRENDYOL 1)", StringComparison.Ordinal));

        var read = Input(store, order, line, 160m, Unsettled(store));
        Assert.DoesNotContain(ReportBuilder.Build(read, previous, new Dictionary<Guid, decimal>()).DataNotes, note => note.Contains("okunamayan", StringComparison.Ordinal));
        Assert.Equal(0, Assert.Single(ReportOrderProfit.Build(read, ReportBuilder.Facts(read).Orders)).UnreadableLines);
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
        // 10.99 + VAT and 6 % of the sale (27.96); no sales VAT. Trendyol withholds the e-commerce tax on its European and
        // Gulf storefronts too (live Avrupa payouts: 0.855-0.886 % of their sales): 1 % of 423.56.
        Assert.Equal(41.15m, micro.Costs.Single(cost => cost.Key == "serviceFee").Amount);
        Assert.Equal(4.24m, micro.Costs.Single(cost => cost.Key == "stoppage").Amount);
    }

    [Fact]
    public void An_azerbaijan_storefront_order_pays_the_fee_without_vat_and_no_withholding_tax()
    {
        // Trendyol's AZ company bills "AZ-Platform Hizmet Bedeli" at 10.99 TL without VAT and its payouts carry no
        // withholding tax (live, Oct-Nov 2026).
        var (store, order, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var input = Input(store, order, line, 160m, Unsettled(store), "AZ");
        var view = Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));
        decimal Cost(string key) => view.Costs.Single(cost => cost.Key == key).Amount!.Value;

        // 10.99 and 6 % of 465.92 (27.96); the net VAT takes off the cost's (14.55), commission's (16.00) and cargo's
        // (16.39) VAT but none for the fee.
        Assert.Equal((38.95m, 0m, -46.94m), (Cost("serviceFee"), Cost("stoppage"), Cost("netVat")));
    }

    [Fact]
    public void The_report_estimates_the_withholding_tax_of_every_storefront_but_azerbaijan()
    {
        var store = Guid.NewGuid();
        // A store whose statement has been read (through the period) but holds nothing of this order yet.
        var read = new ReportFinance([], [], new HashSet<(Guid, string)>(),
            new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = new() { ConnectionId = store, CoveredThrough = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero) } },
            new Dictionary<Guid, ReportCargoSample> { [store] = new(98.34m, 1) });
        decimal Stoppage(string storeFront)
        {
            var (order, line) = (Guid.NewGuid(), Guid.NewGuid());
            var input = Input(store, order, line, 160m, read, storeFront);
            return ReportCosts.Build(input, ReportBuilder.Facts(input).Orders).Lines.SingleOrDefault(cost => cost.Category == MarketplaceFinanceCategories.Stoppage)?.Amount ?? 0m;
        }
        Assert.Equal((4.24m, 4.24m, 4.24m, 0m), (Stoppage("TR"), Stoppage("BG"), Stoppage("SA"), Stoppage("AZ")));
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

    // Live, 29 Oct 2026 #11762777840: MZ032YAD4 sold at 675, paid 633.81 after the seller's discount and coupon.
    private static (ReportInput Input, Guid Order) RateOrder(string orderRate, DateTimeOffset soldAt, ReportFinance? finance = null)
    {
        var (store, order, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var day = DateOnly.FromDateTime(soldAt.ToOffset(TimeSpan.FromHours(3)).DateTime);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime();
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "11762777840", DerivedStatus = "DELIVERED", Amount = 633.81m, ListAmount = 675m, LineValue = 633.81m,
                RealAt = soldAt, OrderedAt = soldAt, StoreFrontCode = "TR" }],
            new Dictionary<Guid, ReportOrderExtra>(),
            [new() { Id = line, OrderId = order, Title = "Anne Penye Bluz", Sku = "MZ032YAD4", Barcode = "MZ032YAD4", UnitPrice = 633.81m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 200m }],
            new Dictionary<Guid, ReportLineExtra> { [line] = new() { Id = line, Commission = orderRate } }, [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false,
            finance ?? Unsettled(store));
        return (input, order);
    }

    [Fact]
    public void An_estimated_commission_comes_from_the_rate_the_order_was_sent()
    {
        var (input, _) = RateOrder("11.6", new DateTimeOffset(2026, 11, 4, 19, 15, 48, TimeSpan.Zero));

        var view = Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));

        var commission = view.Costs.Single(cost => cost.Key == "commission");
        Assert.Equal(73.52m, commission.Amount);
        Assert.True(commission.Estimated);
        Assert.StartsWith("Siparişten gelen oran", commission.Source);
        Assert.Equal(11.6m, Assert.Single(view.Lines).CommissionRate);
    }

    [Fact]
    public void Trendyols_settled_commission_still_wins_over_the_orders_rate()
    {
        var soldAt = new DateTimeOffset(2026, 10, 29, 16, 48, 35, TimeSpan.Zero);
        var (probe, _) = RateOrder("21.5", soldAt);
        var store = probe.Orders[0].ConnectionId;
        // Settled: 145.13 on the sale less 6.01 and 2.85 on the discount and the coupon.
        ReportFinanceRow Settlement(string type, decimal commission) => new()
        {
            ConnectionId = store,
            Source = TrendyolFinanceRequestContract.Settlements,
            ExternalId = type,
            RequestedType = type,
            Category = MarketplaceFinanceCategories.Sale,
            TransactionDate = soldAt.AddDays(2),
            OrderNumber = "11762777840",
            Barcode = "MZ032YAD4",
            CommissionAmount = commission
        };
        var finance = new ReportFinance([Settlement("Sale", 145.13m), Settlement("Discount", 6.01m), Settlement("Discount", 2.85m)], [], new HashSet<(Guid, string)>(),
            new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = null }, new Dictionary<Guid, ReportCargoSample> { [store] = new(98.34m, 1) });
        var input = probe with { Finance = finance };

        var view = Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));

        var commission = view.Costs.Single(cost => cost.Key == "commission");
        Assert.Equal((136.27m, false, "Trendyol cari hesabından"), (commission.Amount, commission.Estimated, commission.Source));
        // Trendyol bills the service fee and the withholding tax on payment-day invoices tied to no order, so a settled
        // order without such a record of its own still carries their estimate, as the report's costs do (never 0).
        var fee = view.Costs.Single(cost => cost.Key == "serviceFee");
        var tax = view.Costs.Single(cost => cost.Key == "stoppage");
        Assert.Equal((13.19m, true), (fee.Amount, fee.Estimated));
        Assert.Equal((5.76m, true), (tax.Amount, tax.Estimated));
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
