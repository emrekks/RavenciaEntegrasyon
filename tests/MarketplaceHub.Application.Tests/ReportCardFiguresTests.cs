using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The figures the overview cards add (report cards step 3): the return cargo billed for the period's Trendyol orders
// and Trendyol's return operation fees dated in it, the distinct products sold and the net revenue without its VAT.
public sealed class ReportCardFiguresTests
{
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);

    private static ReportInput Input(Dictionary<Guid, ReportChannelView> channels, List<ReportOrderRow> orders, List<ReportLineRow> lines, ReportFinance? finance) =>
        new(new ReportPeriod(Day, Day, Start, Start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            channels, orders, new Dictionary<Guid, ReportOrderExtra>(), lines,
            lines.ToDictionary(line => line.Id, line => new ReportLineExtra { Id = line.Id, CommissionType = "number", Commission = "20" }), [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false, Finance: finance);

    private static ReportInput Previous(ReportInput input) =>
        input with { Period = new ReportPeriod(Day.AddDays(-1), Day.AddDays(-1), Start.AddDays(-1), Start), Orders = [], Lines = [], LineExtras = new Dictionary<Guid, ReportLineExtra>(), Finance = null };

    private static MarketplaceFinanceSyncState ReadState(Guid store) =>
        new() { TenantId = Guid.NewGuid(), ConnectionId = store, CoveredFrom = Start.AddDays(-90), CoveredThrough = Start.AddDays(2), LastSyncedAt = Start.AddDays(2) };

    // A returned Trendyol order whose shipment and return cargo are both billed, and a return operation fee of a
    // cross-border order that names no order: the return cargo is the return line alone, the fee is apart by its date.
    [Fact]
    public void The_return_cargo_is_the_billed_return_lines_and_the_operation_fee_is_counted_by_its_date()
    {
        var store = Guid.NewGuid();
        var (order, line) = (Guid.NewGuid(), Guid.NewGuid());
        var at = Start.AddHours(12);
        List<ReportCargoItemRow> items =
        [
            new() { ConnectionId = store, InvoiceSerialNumber = "INV-1", OrderNumber = "R-1", ShipmentPackageType = "Gönderi Kargo Bedeli", Amount = 98.34m },
            new() { ConnectionId = store, InvoiceSerialNumber = "INV-2", OrderNumber = "R-1", ShipmentPackageType = "İade Kargo Bedeli", Amount = 87.12m }
        ];
        List<ReportFinanceRow> rows =
        [
            new() { ConnectionId = store, Source = "OTHER_FINANCIALS", ExternalId = "F-1", RequestedType = "DeductionInvoices", TransactionType = "Yurtdışı Operasyon Iade Bedeli",
                Category = MarketplaceFinanceCategories.OtherDeduction, TransactionDate = at, Debt = 350.4m }
        ];
        var finance = new ReportFinance(rows, items, new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = ReadState(store) },
            new Dictionary<Guid, ReportCargoSample> { [store] = new(98.34m, 1) });
        var input = Input(new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "R-1", DerivedStatus = "DELIVERED", Amount = 100m, LineValue = 100m, LineBack = 100m, RealAt = at, OrderedAt = at, StoreFrontCode = "TR" }],
            [new() { Id = line, OrderId = order, Title = "X", Sku = "X", Barcode = "X", UnitPrice = 100m, OrderedQuantity = 1m, ReturnedQuantity = 1m, VatRate = 10m, CostPrice = 40m }],
            finance);

        var costs = ReportBuilder.Build(input, Previous(input), new Dictionary<Guid, decimal>()).Costs;
        var cargo = costs.Cargo!;

        Assert.Equal((87.12m, 1), (cargo.ReturnBilled, cargo.ReturnBilledOrders));
        Assert.Equal((350.4m, 1), (cargo.ReturnOperationFees, cargo.ReturnOperationRecords));
        // Both ways are in the cargo the account takes off; the fee stays among the deductions tied to no order.
        Assert.Equal(98.34m + 87.12m, cargo.Real);
        Assert.Equal(350.4m, Assert.Single(costs.Unlinked!, x => x.Category == MarketplaceFinanceCategories.OtherDeduction).Amount);
    }

    // Two variants and a line without one sold, a fully cancelled line of a third variant not: three products; the net
    // revenue without VAT takes each line's VAT at its own rate off what was kept (a returned line keeps nothing).
    [Fact]
    public void The_summary_counts_the_products_sold_and_the_net_revenue_without_vat()
    {
        var store = Guid.NewGuid();
        var order = Guid.NewGuid();
        var (v1, v2, v3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var at = Start.AddHours(12);
        var input = Input(new Dictionary<Guid, ReportChannelView> { [store] = new(store, "HEPSIBURADA", "HB", false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "A-1", DerivedStatus = "DELIVERED", Amount = 420m, LineValue = 420m, RealAt = at, OrderedAt = at, StoreFrontCode = "TR" }],
            [
                new() { Id = Guid.NewGuid(), OrderId = order, VariantId = v1, Title = "A", Sku = "A", UnitPrice = 120m, OrderedQuantity = 2m, VatRate = 20m },
                new() { Id = Guid.NewGuid(), OrderId = order, VariantId = v2, Title = "B", Sku = "B", UnitPrice = 110m, OrderedQuantity = 1m, VatRate = 10m },
                new() { Id = Guid.NewGuid(), OrderId = order, VariantId = null, Title = "C", Sku = "C", UnitPrice = 70m, OrderedQuantity = 1m, ReturnedQuantity = 1m, VatRate = 20m },
                new() { Id = Guid.NewGuid(), OrderId = order, VariantId = v3, Title = "D", Sku = "D", UnitPrice = 50m, OrderedQuantity = 1m, CancelledQuantity = 1m, VatRate = 20m }
            ],
            null);

        var summary = ReportBuilder.Build(input, Previous(input), new Dictionary<Guid, decimal>()).Summary;

        Assert.Equal(3, summary.SoldVariants);
        // 240 at 20 % and 110 at 10 % kept: 200 + 100; the returned line keeps nothing.
        Assert.Equal(300m, summary.VatFreeNetRevenue);
    }
}
