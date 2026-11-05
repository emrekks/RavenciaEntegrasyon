using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The period's cargo (ReportCosts) costs the unbilled cargo as the order profit does (ReportOrderProfit), says when a
// store's cargo could not be estimated, and a fully cancelled line adds no desi to the parcel.
public sealed class ReportCargoAndDesiTests
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

    private static ReportCargoItemRow Cargo(Guid store, string order, string type) =>
        new() { ConnectionId = store, InvoiceSerialNumber = $"INV-{type}", OrderNumber = order, ShipmentPackageType = type, Amount = 98.34m };

    // A fully returned Trendyol order at a 98.34 TL tariff: the shipment and the return are each billed or estimated,
    // and the period's cargo equals the order's in every case.
    [Theory]
    [InlineData(true, false, 196.68, 98.34, 1)]
    [InlineData(false, false, 196.68, 196.68, 1)]
    [InlineData(true, true, 196.68, 0, 0)]
    public void A_returned_orders_unbilled_return_cargo_is_estimated_in_the_period_as_in_the_order(bool shipmentBilled, bool returnBilled, decimal expected, decimal estimated, int unbilledReturns)
    {
        var store = Guid.NewGuid();
        var (order, line) = (Guid.NewGuid(), Guid.NewGuid());
        var at = Start.AddHours(12);
        List<ReportCargoItemRow> items = [];
        if (shipmentBilled) items.Add(Cargo(store, "R-1", "Gönderi Kargo Bedeli"));
        if (returnBilled) items.Add(Cargo(store, "R-1", "İade Kargo Bedeli"));
        var finance = new ReportFinance([], items, new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = ReadState(store) },
            new Dictionary<Guid, ReportCargoSample> { [store] = new(98.34m, 1) });
        var input = Input(new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "R-1", DerivedStatus = "DELIVERED", Amount = 100m, LineValue = 100m, LineBack = 100m, RealAt = at, OrderedAt = at, StoreFrontCode = "TR" }],
            [new() { Id = line, OrderId = order, Title = "X", Sku = "X", Barcode = "X", UnitPrice = 100m, OrderedQuantity = 1m, ReturnedQuantity = 1m, VatRate = 10m, CostPrice = 40m }],
            finance);

        var facts = ReportBuilder.Facts(input).Orders;
        Assert.True(Assert.Single(facts).Back > 0m);
        var cargo = ReportBuilder.Build(input, Previous(input), new Dictionary<Guid, decimal>()).Costs.Cargo!;
        var orderCargo = Assert.Single(ReportOrderProfit.Build(input, facts)).Costs.Single(cost => cost.Key == "cargo").Amount;

        Assert.Equal(expected, orderCargo);
        Assert.Equal(expected, cargo.Combined);
        Assert.Equal(estimated, cargo.Estimated);
        Assert.Equal(unbilledReturns, cargo.UnbilledReturns);
        Assert.Equal(0, cargo.UnestimatedOrders);
    }

    // Two Trendyol stores, A with a cargo tariff and B without, one unbilled order each: A's is estimated, B's is
    // counted as not estimated, and the note says the cargo total is short of it.
    [Fact]
    public void An_unestimable_stores_orders_are_counted_and_the_cargo_total_is_marked_partial()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var (orderA, orderB, lineA, lineB) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var at = Start.AddHours(12);
        var finance = new ReportFinance([], [], new HashSet<(Guid, string)>(),
            new Dictionary<Guid, MarketplaceFinanceSyncState?> { [a] = ReadState(a), [b] = ReadState(b) },
            new Dictionary<Guid, ReportCargoSample> { [a] = new(98.34m, 1) });
        var input = Input(new Dictionary<Guid, ReportChannelView> { [a] = new(a, "TRENDYOL", "Trendyol A", false), [b] = new(b, "TRENDYOL", "Trendyol B", false) },
            [
                new() { Id = orderA, ConnectionId = a, OrderNumber = "A-1", DerivedStatus = "DELIVERED", Amount = 100m, LineValue = 100m, RealAt = at, OrderedAt = at, StoreFrontCode = "TR" },
                new() { Id = orderB, ConnectionId = b, OrderNumber = "B-1", DerivedStatus = "DELIVERED", Amount = 100m, LineValue = 100m, RealAt = at, OrderedAt = at, StoreFrontCode = "TR" }
            ],
            [
                new() { Id = lineA, OrderId = orderA, Title = "X", Sku = "X", UnitPrice = 100m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 40m },
                new() { Id = lineB, OrderId = orderB, Title = "Y", Sku = "Y", UnitPrice = 100m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 40m }
            ], finance);

        var report = ReportBuilder.Build(input, Previous(input), new Dictionary<Guid, decimal>());
        var cargo = report.Costs.Cargo!;
        var orders = ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders);

        Assert.Equal(2, cargo.UnbilledOrders);
        Assert.Null(orders.Single(order => order.OrderId == orderB).Costs.Single(cost => cost.Key == "cargo").Amount);
        // The known part stays (store A at its tariff); store B's order is counted, so the total is not shown as complete.
        Assert.Equal(98.34m, cargo.Estimated);
        Assert.Equal(1, cargo.UnestimatedOrders);
        Assert.Contains(report.DataNotes, note => note.StartsWith("Kargo, Trendyol", StringComparison.Ordinal)
            && note.Contains("1 siparişin mağazasında son 60 günde faturalanmış kargo olmadığı için kargosu tahmin edilemedi", StringComparison.Ordinal));
    }

    [Fact]
    public void A_store_with_a_tariff_for_every_unbilled_order_leaves_nothing_unestimated()
    {
        var store = Guid.NewGuid();
        var (order, line) = (Guid.NewGuid(), Guid.NewGuid());
        var at = Start.AddHours(12);
        var finance = new ReportFinance([], [], new HashSet<(Guid, string)>(), new Dictionary<Guid, MarketplaceFinanceSyncState?> { [store] = ReadState(store) },
            new Dictionary<Guid, ReportCargoSample> { [store] = new(98.34m, 1) });
        var input = Input(new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [new() { Id = order, ConnectionId = store, OrderNumber = "A-1", DerivedStatus = "DELIVERED", Amount = 100m, LineValue = 100m, RealAt = at, OrderedAt = at, StoreFrontCode = "TR" }],
            [new() { Id = line, OrderId = order, Title = "X", Sku = "X", UnitPrice = 100m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 40m }], finance);

        var cargo = ReportBuilder.Build(input, Previous(input), new Dictionary<Guid, decimal>()).Costs.Cargo!;

        Assert.Equal((98.34m, 0, 0), (cargo.Estimated!.Value, cargo.UnbilledReturns, cargo.UnestimatedOrders));
    }

    [Fact]
    public void A_fully_cancelled_line_adds_no_desi_to_the_parcel()
    {
        Assert.Equal((2m, ReportParcelDesi.Manual), ReportParcelDesi.ForOrder(null, [(2m, null, null, 1m), (3m, null, null, 0m)], ReportParcelDesi.Learned.None));
        // Every line cancelled: no desi known, the tariff's one-desi parcel.
        Assert.Equal((null, ReportParcelDesi.Default), ReportParcelDesi.ForOrder(null, [(3m, null, null, 0m)], ReportParcelDesi.Learned.None));
        // The marketplace's own desi of the order still wins.
        Assert.Equal((5m, ReportParcelDesi.Marketplace), ReportParcelDesi.ForOrder(5m, [(3m, null, null, 0m)], ReportParcelDesi.Learned.None));
    }

    [Fact]
    public void A_fully_cancelled_line_does_not_raise_the_cargo_estimate()
    {
        var store = Guid.NewGuid();
        var order = Guid.NewGuid();
        var (kept, cancelled) = (Guid.NewGuid(), Guid.NewGuid());
        var at = Start.AddHours(12);
        ReportInput Build(bool withCancelledLine)
        {
            List<ReportLineRow> lines = [new() { Id = kept, OrderId = order, Title = "K", Sku = "K", UnitPrice = 500m, OrderedQuantity = 1m, VatRate = 10m, CostPrice = 100m, Desi = 2m }];
            if (withCancelledLine)
                lines.Add(new() { Id = cancelled, OrderId = order, Title = "C", Sku = "C", UnitPrice = 300m, OrderedQuantity = 1m, CancelledQuantity = 1m, VatRate = 10m, CostPrice = 100m, Desi = 15m });
            return Input(new Dictionary<Guid, ReportChannelView> { [store] = new(store, "HEPSIBURADA", "Hepsiburada", false) },
                [new() { Id = order, ConnectionId = store, OrderNumber = "H-1", DerivedStatus = "DELIVERED", Amount = 500m, LineValue = 500m, RealAt = at, OrderedAt = at, StoreFrontCode = "TR" }],
                lines, null);
        }
        decimal? Cargo(ReportInput input) => Assert.Single(ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders)).Costs.Single(cost => cost.Key == "cargo").Amount;
        decimal? PeriodCargo(ReportInput input) => ReportBuilder.Build(input, Previous(input), new Dictionary<Guid, decimal>()).Costs.Cargo?.PlatformEstimated;

        var without = Build(false);
        var with = Build(true);
        Assert.Equal(0m, Assert.Single(ReportBuilder.Facts(with).Orders).Lines.Single(line => line.Row.Id == cancelled).Active);
        Assert.Equal(Cargo(without), Cargo(with));
        Assert.Equal(PeriodCargo(without), PeriodCargo(with));
    }
}
