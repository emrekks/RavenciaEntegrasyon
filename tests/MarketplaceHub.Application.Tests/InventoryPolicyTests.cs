using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InventoryPolicyTests
{
    // K-2: the marketplace number (read earlier) plus the order units held as reservations at the read, plus the
    // on-hand movements recorded after it.
    [Theory]
    [InlineData(10, 0, 0, 0, 10)]   // nothing happened in the panel
    [InlineData(10, 0, 0, -2, 8)]   // an order after the read took 2 from stock
    [InlineData(8, 2, 0, 0, 10)]    // 2 reserved before the read: off the number already, taken from stock later
    [InlineData(10, 2, -2, 0, 10)]  // reserved after the read: the number still holds them
    [InlineData(8, 0, 2, -2, 8)]    // reserved before, taken from stock after the read
    [InlineData(10, 0, 0, 3, 13)]   // a cancellation (or restock) after the read
    [InlineData(1, 0, 0, -5, 0)]    // never below zero
    public void MarketplaceStockTargetCountsPanelMovementsAroundTheRead(decimal remote, decimal reservedNow, decimal reservationLedgerAfter, decimal onHandLedgerAfter, decimal expected) =>
        Assert.Equal(expected, MarketplaceStockImportPolicy.TargetOnHand(remote, reservedNow, reservationLedgerAfter, onHandLedgerAfter));

    [Fact]
    public void ShippedQuantityConsumesReservationWithoutReleasingMoreThanOrdered()
    {
        Assert.Equal(0m, OrderInventoryReservationPolicy.DesiredQuantity(1m, 0m, 1m, true));
        Assert.Equal(1m, OrderInventoryReservationPolicy.DesiredQuantity(2m, 0m, 1m, true));
        Assert.Equal(0m, OrderInventoryReservationPolicy.DesiredQuantity(2m, 2m, 1m, true));
    }

    [Fact]
    public void ReservationStatusListsControlReserveAndReleaseDecisions()
    {
        var policy = new ConnectionInventoryPolicy
        {
            AuthorityMode = InventoryAuthorityPolicy.Central,
            ReservationMode = "ORDER_LINE",
            ReserveOnStatuses = "Created, Processing",
            ReleaseOnStatuses = "Cancelled, Delivered"
        };

        Assert.True(OrderInventoryReservationPolicy.IsReservationEnabled(policy, "Created"));
        Assert.False(OrderInventoryReservationPolicy.IsReservationEnabled(policy, "Delivered"));
        Assert.False(OrderInventoryReservationPolicy.IsReservationEnabled(policy, "ReadyToShip"));
    }

    [Fact]
    public void MissingReservationPolicyDefaultsToNoReservation()
    {
        Assert.False(OrderInventoryReservationPolicy.IsReservationEnabled(null, "Created"));
    }

    [Fact]
    public void NewOrderConsumesPhysicalStockOnceAndShipmentDoesNotConsumeItAgain()
    {
        var receivedDelta = OrderInventoryStockPolicy.OnHandDelta(
            baselineQuantity: 0m,
            receivedLedgerDelta: 0m,
            targetCommittedQuantity: OrderInventoryStockPolicy.TargetCommittedQuantity(1m, 0m, 0m));
        Assert.Equal(-1m, receivedDelta);
        Assert.Equal(34m, 35m + receivedDelta);

        var afterReceiptLedgerDelta = -1m;
        var shippedDelta = OrderInventoryStockPolicy.OnHandDelta(
            baselineQuantity: 0m,
            receivedLedgerDelta: afterReceiptLedgerDelta,
            targetCommittedQuantity: OrderInventoryStockPolicy.TargetCommittedQuantity(1m, 0m, 1m));
        Assert.Equal(0m, shippedDelta);
    }

    // The order reset builds the same key in SQL from the connection id as uuid text (lowercase, hyphenated).
    [Fact]
    public void ResetCarryKeyNamesTheConnectionAndTheMarketplaceOrderAndLine()
    {
        var connectionId = Guid.Parse("0198A3C4-5E6F-7A8B-9C0D-1E2F3A4B5C6D");
        Assert.Equal(
            "order-line-reset:0198a3c4-5e6f-7a8b-9c0d-1e2f3a4b5c6d:10928374651:4455667788",
            OrderInventoryStockPolicy.ResetCarryKey(connectionId, "10928374651", "4455667788"));
    }

    [Fact]
    public void CancellationRestoresOnlyUnshippedOrderQuantity()
    {
        var cancelledBeforeShipment = OrderInventoryStockPolicy.OnHandDelta(
            baselineQuantity: 0m,
            receivedLedgerDelta: -1m,
            targetCommittedQuantity: OrderInventoryStockPolicy.TargetCommittedQuantity(1m, 1m, 0m));
        Assert.Equal(1m, cancelledBeforeShipment);

        var cancelledAfterShipment = OrderInventoryStockPolicy.OnHandDelta(
            baselineQuantity: 0m,
            receivedLedgerDelta: -1m,
            targetCommittedQuantity: OrderInventoryStockPolicy.TargetCommittedQuantity(1m, 1m, 1m));
        Assert.Equal(0m, cancelledAfterShipment);
    }

    // Owner decision (30 Oct 2026): a line first read already shipped or delivered still takes its units from stock;
    // before, its shipped units became the baseline and stock never moved (11 Trendyol lines on 28 and 30 Oct).
    [Fact]
    public void FirstReadOfAShippedLineTakesItsUnitsFromStock()
    {
        var baseline = OrderInventoryStockPolicy.InitialBaselineQuantity(0m);
        var target = OrderInventoryStockPolicy.TargetCommittedQuantity(1m, 0m, 1m);

        Assert.Equal(0m, baseline);
        Assert.Equal(new OrderInventoryStockPolicy.StockMovement(-1m, 0m), OrderInventoryStockPolicy.Plan(baseline, 0m, 0m, target, onHand: 3m, negativeStockAllowed: false));
    }

    [Fact]
    public void PartiallyShippedLegacyLineConsumesOnlyStockNotAlreadyInTheBaseline()
    {
        var target = OrderInventoryStockPolicy.TargetCommittedQuantity(2m, 0m, 1m);
        var baseline = OrderInventoryStockPolicy.InitialBaselineQuantity(1m);

        Assert.Equal(2m, target);
        Assert.Equal(new OrderInventoryStockPolicy.StockMovement(-1m, 0m), OrderInventoryStockPolicy.Plan(baseline, 0m, 0m, target, 5m, false));
    }

    // Owner decision (30 Oct 2026): no reservations. Stock short of the order gives what it has (never below zero), the
    // rest is the line's shortfall; a cancellation releases the shortfall first and gives back only what was taken.
    [Fact]
    public void ShortStockTakesWhatItHasAndKeepsTheRestAsShortfall()
    {
        Assert.Equal(new OrderInventoryStockPolicy.StockMovement(-1m, 2m), OrderInventoryStockPolicy.Plan(0m, 0m, 0m, 3m, onHand: 1m, negativeStockAllowed: false));
        Assert.Equal(new OrderInventoryStockPolicy.StockMovement(0m, 1m), OrderInventoryStockPolicy.Plan(0m, 0m, 0m, 1m, onHand: 0m, negativeStockAllowed: false));
        Assert.Equal(new OrderInventoryStockPolicy.StockMovement(-1m, 0m), OrderInventoryStockPolicy.Plan(0m, 0m, 0m, 1m, onHand: 0m, negativeStockAllowed: true));
        // Read again: nothing more is taken, even after stock was refilled.
        Assert.Equal(new OrderInventoryStockPolicy.StockMovement(0m, 0m), OrderInventoryStockPolicy.Plan(0m, 2m, -1m, 3m, onHand: 10m, negativeStockAllowed: false));
        // One unit cancelled: it comes off the shortfall; all cancelled: the one taken unit comes back.
        Assert.Equal(new OrderInventoryStockPolicy.StockMovement(0m, -1m), OrderInventoryStockPolicy.Plan(0m, 2m, -1m, 2m, onHand: 10m, negativeStockAllowed: false));
        Assert.Equal(new OrderInventoryStockPolicy.StockMovement(1m, -2m), OrderInventoryStockPolicy.Plan(0m, 2m, -1m, 0m, onHand: 10m, negativeStockAllowed: false));
    }

    [Fact]
    public void OrderConsumptionDoesNotCreateNegativeStockAndCanUseItsOwnReservation()
    {
        Assert.Equal(-1m, OrderInventoryStockPolicy.LimitToAvailableStock(1m, 0m, 2m, -2m, false));
        Assert.Equal(0m, OrderInventoryStockPolicy.LimitToAvailableStock(0m, 0m, 0m, -1m, false));
        Assert.Equal(-1m, OrderInventoryStockPolicy.LimitToAvailableStock(0m, 0m, 0m, -1m, true));
    }

    [Fact]
    public void MissingOrUnknownAuthorityNeverOverwritesLocalPhysicalStock()
    {
        Assert.False(InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(null));
        Assert.False(InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(InventoryAuthorityPolicy.Central));
        Assert.False(InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(InventoryAuthorityPolicy.RemoteObservation));
        Assert.True(InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(InventoryAuthorityPolicy.RemoteAuthoritative));
    }

    [Fact]
    public void UntouchedInventoryCanBeSeededByTheFirstCatalogSnapshotOnly()
    {
        Assert.True(InventoryAuthorityPolicy.ShouldSeedInitialOnHand(new InventoryItem
        {
            LocationCode = "MAIN",
            OnHand = 0m,
            Reserved = 0m
        }));

        Assert.False(InventoryAuthorityPolicy.ShouldSeedInitialOnHand(new InventoryItem
        {
            LocationCode = "MAIN",
            OnHand = 0m,
            Reserved = 0m,
            Version = 2
        }));
        Assert.True(InventoryAuthorityPolicy.ShouldSeedInitialOnHand(new InventoryItem
        {
            LocationCode = "MAIN",
            OnHand = 0m,
            Reserved = 0m,
            ObservedRemoteQuantity = 4m
        }));
    }
}
