using MarketplaceHub.Domain;

namespace MarketplaceHub.Domain.Tests;

public sealed class F3DomainTests
{
    [Fact]
    public void Shipment_state_machine_allows_forward_flow_and_rejects_regression()
    {
        Assert.True(ShipmentPackageStateMachine.CanTransition(ShipmentPackageStatus.New, ShipmentPackageStatus.Processing));
        Assert.True(ShipmentPackageStateMachine.CanTransition(ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Delivered));
        Assert.False(ShipmentPackageStateMachine.CanTransition(ShipmentPackageStatus.Delivered, ShipmentPackageStatus.New));
        Assert.False(ShipmentPackageStateMachine.CanTransition(ShipmentPackageStatus.Returned, ShipmentPackageStatus.Shipped));
    }

    [Fact]
    public void Order_quantity_invariant_covers_split_cancel_ship_deliver_and_return()
    {
        Assert.True(OrderQuantityInvariant.IsValid(10, 8, 2, 6, 5, 1));
        Assert.False(OrderQuantityInvariant.IsValid(10, 9, 2, 6, 5, 1));
        Assert.False(OrderQuantityInvariant.IsValid(10, 8, 2, 6, 5, 6));
    }

    [Fact]
    public void Return_state_machine_rejects_completed_regression()
    {
        Assert.True(ReturnClaimStateMachine.CanTransition(ReturnClaimStatus.Requested, ReturnClaimStatus.ActionRequired));
        Assert.True(ReturnClaimStateMachine.CanTransition(ReturnClaimStatus.Approved, ReturnClaimStatus.Completed));
        Assert.False(ReturnClaimStateMachine.CanTransition(ReturnClaimStatus.Completed, ReturnClaimStatus.Requested));
    }
}
