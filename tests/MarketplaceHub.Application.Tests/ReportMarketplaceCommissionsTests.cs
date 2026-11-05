using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// One rule on every marketplace: an order whose marketplace's own finance records charge its commission is costed and
// labelled with that real commission (net of refunds and corrections); every other order keeps the estimate, labelled
// as one.
public sealed class ReportMarketplaceCommissionsTests
{
    private static readonly Guid Store = Guid.NewGuid();

    private static ReportMarketplaceCommissions.Record Hb(string id, string type, string? order, string? package, decimal debt, decimal credit) =>
        new(Store, HepsiburadaFinanceSyncJobHandler.Source, id, type, HepsiburadaFinanceSyncJobHandler.Category(type), order, package, debt, credit, null);

    // n11's settlement "commission" is a rate; paid: the line's paymentAmount (stored as Credit).
    private static ReportMarketplaceCommissions.Record N11(string item, string day, string order, decimal rate, decimal paid, bool returned = false) =>
        new(Store, N11SettlementSyncJobHandler.Source, $"{item}:{day}", "Ödendi", returned ? MarketplaceFinanceCategories.Return : MarketplaceFinanceCategories.Sale, order, null,
            0m, paid, N11SettlementSyncJobHandler.Commission(rate, null, paid, returned ? MarketplaceFinanceCategories.Return : MarketplaceFinanceCategories.Sale), rate);

    private static ReportMarketplaceCommissions.Record Pz(string code, string order, decimal? commission) =>
        new(Store, PazaramaFinanceSyncJobHandler.Source, code, "Ödeme Yapıldı", MarketplaceFinanceCategories.Sale, order, null, 0m, 0m, commission);

    [Fact]
    public void Hepsiburada_nets_the_commission_records_of_the_order_and_its_packages()
    {
        var (whole, refunded, byPackage, partly, none) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var orders = new List<ReportMarketplaceCommissions.Order>
        {
            new(whole, Store, "HEPSIBURADA", "4001", 1, ["P1"]),
            new(refunded, Store, "HEPSIBURADA", "4002", 1, ["P2"]),
            new(byPackage, Store, "HEPSIBURADA", "4003", 1, ["P3"]),
            new(partly, Store, "HEPSIBURADA", "4004", 2, ["P4", "P5"]),
            new(none, Store, "HEPSIBURADA", "4005", 1, ["P6"])
        };
        var records = new List<ReportMarketplaceCommissions.Record>
        {
            Hb("a", "Commission", "4001", "P1", 108m, 0m),
            // A VAT-difference correction paid back to the seller.
            Hb("b", "CommissionCorrection", "4001", "P1", 0m, 1.5m),
            Hb("c", "Commission", "4002", "P2", 90m, 0m),
            Hb("d", "CommissionRefund", "4002", "P2", 0m, 90m),
            // A record naming only its package.
            Hb("e", "Commission", null, "P3", 54.25m, 0m),
            // Only one of the order's two packages billed: the sum would be short, so the estimate stays.
            Hb("f", "Commission", "4004", "P4", 40m, 0m),
            // Not a commission record.
            Hb("g", "ShipmentCostSharingExpense", "4005", "P6", 75m, 0m)
        };

        var real = ReportMarketplaceCommissions.ByOrder(orders, records);

        Assert.Equal(106.5m, real[whole]);
        Assert.Equal(0m, real[refunded]);
        Assert.Equal(54.25m, real[byPackage]);
        Assert.False(real.ContainsKey(partly));
        Assert.False(real.ContainsKey(none));
    }

    [Fact]
    public void N11_charges_a_sale_lines_paid_amount_at_its_rate_and_gives_a_return_lines_back()
    {
        var (sold, returned, waiting, silent, legacy) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var orders = new List<ReportMarketplaceCommissions.Order>
        {
            new(sold, Store, "N11", "N-1", 2, []),
            new(returned, Store, "N11", "N-2", 1, []),
            new(waiting, Store, "N11", "N-3", 2, []),
            new(silent, Store, "N11", "N-4", 1, []),
            new(legacy, Store, "N11", "N-5", 1, [])
        };
        var records = new List<ReportMarketplaceCommissions.Record>
        {
            // Live, 5 Nov 2026: "commission" 20.34 is the rate; 799 paid → 162.52 (deductionAmount 185.76 adds the fees).
            N11("11", "20261101", "N-1", 20.34m, 799m),
            N11("12", "20261102", "N-1", 20.34m, 800.01m),
            N11("21", "20261101", "N-2", 20.34m, 875m),
            // The return line gives the commission back, whatever sign n11 writes it with.
            N11("21", "20261104", "N-2", 20.34m, -875m, returned: true),
            // One of two items settled so far.
            N11("31", "20261101", "N-3", 20.34m, 899m),
            // n11 left the rate out (read as 0): unknown, not 0.
            N11("41", "20261101", "N-4", 0m, 500m),
            // A row stored before 5 Nov 2026: the rate in CommissionAmount, no CommissionRate.
            new(Store, N11SettlementSyncJobHandler.Source, "51:20261101", "Ödendi", MarketplaceFinanceCategories.Sale, "N-5", null, 0m, 799m, 20.34m)
        };

        var real = ReportMarketplaceCommissions.ByOrder(orders, records);

        Assert.Equal(162.52m, N11SettlementSyncJobHandler.Commission(20.34m, null, 799m, MarketplaceFinanceCategories.Sale));
        Assert.Equal(162.52m + 162.72m, real[sold]);
        Assert.Equal(0m, real[returned]);
        Assert.False(real.ContainsKey(waiting));
        Assert.False(real.ContainsKey(silent));
        Assert.Equal(162.52m, real[legacy]);
    }

    [Fact]
    public void The_payouts_show_an_n11_lines_commission_in_lira_not_its_rate()
    {
        var row = new PayoutRow { Source = N11SettlementSyncJobHandler.Source, PlatformCode = "N11", Category = MarketplaceFinanceCategories.Sale, Credit = 799m, Debt = 185.76m, CommissionRate = 20.34m, CommissionAmount = 162.52m };
        Assert.Equal(162.52m, PayoutBuilder.Line(row, new DateOnly(2026, 11, 5)).Commission);
        // Stored before 5 Nov 2026: the rate sat in CommissionAmount.
        Assert.Equal(162.52m, PayoutBuilder.Line(row with { CommissionRate = null, CommissionAmount = 20.34m }, new DateOnly(2026, 11, 5)).Commission);
        // The payout itself is untouched: n11's settlement amount.
        Assert.Equal(613.24m, PayoutBuilder.Net(row with { SellerRevenue = 613.24m }));
    }

    [Fact]
    public void Pazarama_takes_the_money_movements_commission_unless_one_left_it_out()
    {
        var (read, unknown) = (Guid.NewGuid(), Guid.NewGuid());
        var orders = new List<ReportMarketplaceCommissions.Order> { new(read, Store, "PAZARAMA", "1", 1, []), new(unknown, Store, "PAZARAMA", "2", 1, []) };
        var records = new List<ReportMarketplaceCommissions.Record> { Pz("t1", "1", 75m), Pz("t2", "1", -15m), Pz("t3", "2", 50m), Pz("t4", "2", null) };

        var real = ReportMarketplaceCommissions.ByOrder(orders, records);

        Assert.Equal(60m, real[read]);
        Assert.False(real.ContainsKey(unknown));
    }

    [Theory]
    [InlineData("HEPSIBURADA", "Hepsiburada muhasebe")]
    [InlineData("N11", "n11 hakediş")]
    [InlineData("PAZARAMA", "Pazarama finans")]
    public void An_orders_profit_labels_the_real_commission_real_and_the_estimate_estimated(string platform, string source)
    {
        var (real, estimated) = (Guid.NewGuid(), Guid.NewGuid());
        var (lineA, lineB) = (Guid.NewGuid(), Guid.NewGuid());
        var start = new DateTimeOffset(2026, 11, 3, 21, 0, 0, TimeSpan.Zero);
        var at = start.AddHours(12);
        var input = new ReportInput(new ReportPeriod(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4), start, start.AddDays(1)), ReportBuckets.Day,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [Store] = new(Store, platform, platform, false) },
            [
                new() { Id = real, ConnectionId = Store, OrderNumber = "R", DerivedStatus = "DELIVERED", Amount = 600m, LineValue = 600m, RealAt = at, OrderedAt = at },
                new() { Id = estimated, ConnectionId = Store, OrderNumber = "E", DerivedStatus = "DELIVERED", Amount = 600m, LineValue = 600m, RealAt = at, OrderedAt = at }
            ],
            new Dictionary<Guid, ReportOrderExtra>(),
            [
                new() { Id = lineA, OrderId = real, Title = "Ceket", Sku = "A", UnitPrice = 600m, OrderedQuantity = 1m, VatRate = 20m, CostPrice = 200m },
                new() { Id = lineB, OrderId = estimated, Title = "Ceket", Sku = "A", UnitPrice = 600m, OrderedQuantity = 1m, VatRate = 20m, CostPrice = 200m }
            ],
            new Dictionary<Guid, ReportLineExtra>
            {
                [lineA] = new() { Id = lineA, CommissionRate = "18" },
                [lineB] = new() { Id = lineB, CommissionRate = "18" }
            },
            [], [], new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false,
            OrderCommissions: new Dictionary<Guid, decimal> { [real] = 120m });

        var views = ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders).ToDictionary(view => view.OrderId);

        var gercek = views[real].Costs.Single(cost => cost.Key == "commission");
        Assert.Equal((120m, false, "Gerçek Komisyon"), (gercek.Amount, gercek.Estimated, gercek.Label));
        Assert.StartsWith(source, gercek.Source);
        var tahmini = views[estimated].Costs.Single(cost => cost.Key == "commission");
        // Pazarama sends no rate on the order: its estimate is the store's rate over the read orders.
        Assert.Equal((platform == "PAZARAMA" ? 120m : 108m, true, "Tahmini Komisyon"), (tahmini.Amount, tahmini.Estimated, tahmini.Label));

        var previous = input with { Orders = [], Lines = [], LineExtras = new Dictionary<Guid, ReportLineExtra>(), OrderCommissions = null };
        var commission = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>()).Costs.Commission!;
        Assert.Equal((120m, 1), (commission.Real, commission.RealOrders));
        // What the estimate said for the same order (Pazarama has no rate on the order to estimate from).
        Assert.Equal(platform == "PAZARAMA" ? 0m : 108m, commission.EstimatedForSettled);
        Assert.Equal(commission.Real + commission.EstimatedForUnsettled, commission.Combined);
    }
}
