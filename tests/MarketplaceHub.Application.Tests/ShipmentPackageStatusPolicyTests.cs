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
    [InlineData("Open", ShipmentPackageStatus.ReadyToShip)]
    [InlineData("Packaged", ShipmentPackageStatus.ReadyToShip)]
    [InlineData("Intransit", ShipmentPackageStatus.Shipped)]
    [InlineData("CANCELED", ShipmentPackageStatus.Cancelled)]
    public void FromRemote_MapsKnownProviderStatuses(string rawStatus, ShipmentPackageStatus expected)
    {
        Assert.Equal(expected, ShipmentPackageStatusPolicy.FromRemote(rawStatus));
    }

    [Theory]
    [InlineData("Awaiting", ShipmentPackageStatus.PaymentPending)]
    [InlineData("Verified", ShipmentPackageStatus.PaymentPending)]
    [InlineData("UnPacked", ShipmentPackageStatus.New)]
    [InlineData("At_Collection_Point", ShipmentPackageStatus.Shipped)]
    [InlineData("Created", ShipmentPackageStatus.New)]
    public void TrendyolFromRemote_UsesShipmentMeaningInsteadOfGenericHoldMapping(string rawStatus, ShipmentPackageStatus expected)
    {
        Assert.Equal(expected, TrendyolShipmentPackageStatusPolicy.FromRemote(rawStatus));
    }

    [Theory]
    [InlineData("Awaiting")]
    [InlineData("Verified")]
    [InlineData("UnPacked")]
    [InlineData("At_Collection_Point")]
    public void TrendyolStatusPolicy_RecognizesLegacyHoldProjections(string rawStatus)
    {
        Assert.True(TrendyolShipmentPackageStatusPolicy.IsLegacyOnHoldProjection(rawStatus));
    }

    [Fact]
    public void TrendyolStatusPolicy_MatchesStatusFormattingVariantsDuringProjectionRepair()
    {
        Assert.True(TrendyolShipmentPackageStatusPolicy.HasSameNormalizedStatus("AtCollectionPoint", "AT_COLLECTION_POINT"));
    }

    [Theory]
    [InlineData("Awaiting", ShipmentPackageStatus.PaymentPending)]
    [InlineData("Verified", ShipmentPackageStatus.PaymentPending)]
    [InlineData("UnPacked", ShipmentPackageStatus.New)]
    [InlineData("At_Collection_Point", ShipmentPackageStatus.Shipped)]
    public void TrendyolStatusPolicy_RepairsLegacyZeroIdHoldWhenRealPackageIsPresent(
        string rawStatus,
        ShipmentPackageStatus expected)
    {
        Assert.True(TrendyolShipmentPackageStatusPolicy.ShouldRepairLegacyPlaceholderOnHold(
            "0",
            rawStatus,
            ShipmentPackageStatus.OnHold,
            hasAuthoritativePackage: true));
        Assert.Equal(expected, TrendyolShipmentPackageStatusPolicy.FromRemote(rawStatus));
    }

    [Theory]
    [InlineData("0", "Awaiting", false)]
    [InlineData("4234395651", "Awaiting", true)]
    [InlineData("0", "Awaiting", true)]
    [InlineData("0", "Processing", true)]
    public void TrendyolStatusPolicy_DoesNotRepairUnrelatedPackageStates(
        string externalPackageId,
        string rawStatus,
        bool hasAuthoritativePackage)
    {
        var shouldRepair = TrendyolShipmentPackageStatusPolicy.ShouldRepairLegacyPlaceholderOnHold(
            externalPackageId,
            rawStatus,
            ShipmentPackageStatus.OnHold,
            hasAuthoritativePackage);

        Assert.Equal(externalPackageId == "0" && rawStatus == "Awaiting" && hasAuthoritativePackage, shouldRepair);
    }

    [Fact]
    public void PaymentPending_IsSeparatedFromHoldsAndMayAdvanceWhenTrendyolConfirmsPayment()
    {
        Assert.Equal(ShipmentPackageStatus.PaymentPending, ShipmentPackageStatusPolicy.Aggregate([ShipmentPackageStatus.PaymentPending]));
        Assert.Equal(ShipmentPackageStatus.New, ShipmentPackageStatusPolicy.Aggregate([ShipmentPackageStatus.PaymentPending, ShipmentPackageStatus.New]));
        Assert.True(ShipmentPackageStateMachine.CanTransition(ShipmentPackageStatus.OnHold, ShipmentPackageStatus.PaymentPending));
        Assert.True(ShipmentPackageStateMachine.CanTransition(ShipmentPackageStatus.PaymentPending, ShipmentPackageStatus.New));
        Assert.False(ShipmentPackageStateMachine.CanTransition(ShipmentPackageStatus.New, ShipmentPackageStatus.PaymentPending));
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
