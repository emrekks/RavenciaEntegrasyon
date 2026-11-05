using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A commission nobody knows (no commission on the line, no rate of its store in the period) is not taken as 0: the
// order's profit is unknown and the order is counted, while Shopify's lack of a commission stays a known 0.
public sealed class ReportOrderCommissionTests
{
    private static readonly ReportPeriod Period = new(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25),
        new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.Zero));
    private static readonly DateTimeOffset At = new(2026, 10, 25, 17, 46, 0, TimeSpan.Zero);

    // Live 25 Oct 2026, order 11750129638: one 465.92 TL line, cost 160 TL, at a 98.34 TL cargo tariff.
    private static (ReportInput Input, Guid Order) Trendyol(bool commissionOnLine, bool otherLineWithCommission = false)
    {
        var (store, order, line, other, otherLine) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var finance = new ReportFinance([], [], new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = null },
            new Dictionary<Guid, ReportCargoSample> { [store] = new(98.34m, 1) });
        List<ReportOrderRow> orders = [new() { Id = order, ConnectionId = store, OrderNumber = "11750129638", DerivedStatus = "DELIVERED", Amount = 465.92m, ListAmount = 470.63m, LineValue = 465.92m,
            RealAt = At, OrderedAt = At.AddHours(3), StoreFrontCode = "TR" }];
        List<ReportLineRow> lines = [new() { Id = line, OrderId = order, Title = "Anne Penye Bluz", Sku = "RYP00101", Barcode = "RYP00101", UnitPrice = 465.92m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 160m }];
        var extras = new Dictionary<Guid, ReportLineExtra> { [line] = commissionOnLine ? new() { Id = line, CommissionType = "number", Commission = "20.6" } : new() { Id = line } };
        if (otherLineWithCommission)
        {
            orders.Add(new() { Id = other, ConnectionId = store, OrderNumber = "11750129639", DerivedStatus = "DELIVERED", Amount = 465.92m, LineValue = 465.92m, RealAt = At, OrderedAt = At, StoreFrontCode = "TR" });
            lines.Add(new() { Id = otherLine, OrderId = other, Title = "Anne Penye Bluz", Sku = "RYP00101", UnitPrice = 465.92m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 160m });
            extras[otherLine] = new() { Id = otherLine, CommissionType = "number", Commission = "20.6" };
        }
        return (new ReportInput(Period, ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            orders, new Dictionary<Guid, ReportOrderExtra>(), lines, extras, [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false, Finance: finance), order);
    }

    private static ReportInput Platform(string platformCode, bool commissionOnLine)
    {
        var (store, order, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        return new ReportInput(Period, ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, platformCode, platformCode, false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "P-1", DerivedStatus = "DELIVERED", Amount = 500m, LineValue = 500m, RealAt = At, OrderedAt = At, StoreFrontCode = "TR" }],
            new Dictionary<Guid, ReportOrderExtra>(),
            [new() { Id = line, OrderId = order, Title = "Bluz", Sku = "B-1", UnitPrice = 500m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 150m }],
            new Dictionary<Guid, ReportLineExtra> { [line] = commissionOnLine ? new() { Id = line, CommissionType = "number", CommissionRate = "18" } : new() { Id = line } }, [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
    }

    [Fact]
    public void A_line_without_commission_and_no_store_rate_leaves_the_commission_and_the_profit_unknown()
    {
        var (input, _) = Trendyol(commissionOnLine: false);
        var facts = ReportBuilder.Facts(input).Orders;
        Assert.Null(Assert.Single(Assert.Single(facts).Lines).Commission);
        var view = Assert.Single(ReportOrderProfit.Build(input, facts));

        var commission = view.Costs.Single(cost => cost.Key == "commission");
        Assert.Null(commission.Amount);
        Assert.Equal("Komisyon Eksik", commission.Label);
        Assert.Null(view.Costs.Single(cost => cost.Key == "netVat").Amount);
        Assert.Null(view.Profit);
        Assert.Null(view.Margin);
        var totals = ReportOrderProfit.Totals([view]);
        Assert.Equal((1, 0, (decimal?)null), (totals.MissingCommission, totals.ProfitOrders, totals.Profit));
    }

    [Fact]
    public void The_same_order_with_its_rate_keeps_its_profit()
    {
        var (input, _) = Trendyol(commissionOnLine: true);
        var view = Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));
        Assert.Equal(100.95m, view.Profit);
        Assert.Equal(0, ReportOrderProfit.Totals([view]).MissingCommission);
    }

    [Fact]
    public void A_line_without_commission_is_estimated_at_its_stores_rate_when_the_period_has_one()
    {
        var (input, order) = Trendyol(commissionOnLine: false, otherLineWithCommission: true);
        var view = ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders).Single(item => item.OrderId == order);
        var commission = view.Costs.Single(cost => cost.Key == "commission");
        Assert.Equal((95.98m, "Tahmini Komisyon"), (commission.Amount!.Value, commission.Label));
        Assert.Equal(100.95m, view.Profit);
    }

    [Fact]
    public void Shopify_takes_no_commission_so_its_profit_stays_known()
    {
        var input = Platform("SHOPIFY", commissionOnLine: false);
        var view = Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders));
        Assert.Equal(0m, view.Costs.Single(cost => cost.Key == "commission").Amount);
        Assert.NotNull(view.Profit);
        var costs = ReportBuilder.Build(input, input with { Orders = [], Lines = [], LineExtras = new Dictionary<Guid, ReportLineExtra>() }, new Dictionary<Guid, decimal>()).Costs;
        Assert.Equal(0, costs.Commission!.MissingOrders);
    }

    [Fact]
    public void The_periods_commission_counts_the_orders_whose_commission_is_unknown()
    {
        var input = Platform("HEPSIBURADA", commissionOnLine: false);
        var report = ReportBuilder.Build(input, input with { Orders = [], Lines = [], LineExtras = new Dictionary<Guid, ReportLineExtra>() }, new Dictionary<Guid, decimal>());
        Assert.Equal((1, 0m), (report.Costs.Commission!.MissingOrders, report.Costs.Commission.EstimatedForUnsettled));
        Assert.Contains(report.DataNotes, note => note.StartsWith("1 siparişin komisyonu bilinmiyor", StringComparison.Ordinal));
        Assert.Null(Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders)).Profit);

        var known = Platform("HEPSIBURADA", commissionOnLine: true);
        Assert.Equal(0, ReportBuilder.Build(known, known with { Orders = [], Lines = [], LineExtras = new Dictionary<Guid, ReportLineExtra>() }, new Dictionary<Guid, decimal>()).Costs.Commission!.MissingOrders);
    }
}
