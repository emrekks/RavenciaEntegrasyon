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
    public void MissingOrUnknownAuthorityNeverOverwritesLocalPhysicalStock()
    {
        Assert.False(InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(null));
        Assert.False(InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(InventoryAuthorityPolicy.Central));
        Assert.False(InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(InventoryAuthorityPolicy.RemoteObservation));
        Assert.True(InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(InventoryAuthorityPolicy.RemoteAuthoritative));
    }
}
