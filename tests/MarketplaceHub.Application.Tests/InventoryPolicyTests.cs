using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InventoryPolicyTests
{
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

    [Fact]
    public void FirstObservationOfHistoricalShipmentUsesExistingPhysicalStockAsBaseline()
    {
        var baseline = OrderInventoryStockPolicy.InitialBaselineQuantity(0m, 1m);
        var delta = OrderInventoryStockPolicy.OnHandDelta(
            baseline,
            receivedLedgerDelta: 0m,
            targetCommittedQuantity: OrderInventoryStockPolicy.TargetCommittedQuantity(1m, 0m, 1m));

        Assert.Equal(1m, baseline);
        Assert.Equal(0m, delta);
    }

    [Fact]
    public void PartiallyShippedOrderConsumesOnlyStockNotAlreadyInTheBaseline()
    {
        var target = OrderInventoryStockPolicy.TargetCommittedQuantity(2m, 0m, 1m);
        var baseline = OrderInventoryStockPolicy.InitialBaselineQuantity(1m, 1m);

        Assert.Equal(2m, target);
        Assert.Equal(-1m, OrderInventoryStockPolicy.OnHandDelta(baseline, 0m, target));
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
