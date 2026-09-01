using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShipmentPackageStatusPolicyTests
{
    [Theory]
    [InlineData(ShipmentPackageStatus.Cancelled, ShipmentPackageStatus.Processing, ShipmentPackageStatus.Processing)]
    [InlineData(ShipmentPackageStatus.Cancelled, ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Shipped)]
    [InlineData(ShipmentPackageStatus.Delivered, ShipmentPackageStatus.ReturnInTransit, ShipmentPackageStatus.ReturnInTransit)]
    [InlineData(ShipmentPackageStatus.Returned, ShipmentPackageStatus.Processing, ShipmentPackageStatus.Processing)]
    public void Aggregate_RespectsSplitPackageLifecycle(ShipmentPackageStatus first, ShipmentPackageStatus second, ShipmentPackageStatus expected)
    {
        var status = ShipmentPackageStatusPolicy.Aggregate([first, second]);

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("PARTIALLY_CANCELLED", ShipmentPackageStatus.PartiallyCancelled)]
    [InlineData("RETURN_IN_TRANSIT", ShipmentPackageStatus.ReturnInTransit)]
    [InlineData("CANCELED", ShipmentPackageStatus.Cancelled)]
    public void FromRemote_MapsKnownProviderStatuses(string rawStatus, ShipmentPackageStatus expected)
    {
        Assert.Equal(expected, ShipmentPackageStatusPolicy.FromRemote(rawStatus));
    }

    [Theory]
    [InlineData(ShipmentPackageStatus.New, ShipmentPackageStatus.Shipped)]
    [InlineData(ShipmentPackageStatus.Processing, ShipmentPackageStatus.Delivered)]
    [InlineData(ShipmentPackageStatus.ReadyToShip, ShipmentPackageStatus.Returned)]
    public void StateMachine_AcceptsAuthoritativeForwardSnapshots(ShipmentPackageStatus current, ShipmentPackageStatus incoming)
    {
        Assert.True(ShipmentPackageStateMachine.CanTransition(current, incoming));
    }

    [Theory]
    [InlineData(ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Processing)]
    [InlineData(ShipmentPackageStatus.Delivered, ShipmentPackageStatus.New)]
    [InlineData(ShipmentPackageStatus.Cancelled, ShipmentPackageStatus.Shipped)]
    public void StateMachine_RejectsBackwardOrTerminalReopening(ShipmentPackageStatus current, ShipmentPackageStatus incoming)
    {
        Assert.False(ShipmentPackageStateMachine.CanTransition(current, incoming));
    }
}
