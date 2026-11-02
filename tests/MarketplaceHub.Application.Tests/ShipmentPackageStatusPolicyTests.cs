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
    [InlineData("UnPacked", ShipmentPackageStatus.Cancelled)]
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
    [InlineData("0", ShipmentPackageStatus.PaymentPending, true, true)]
    [InlineData("0", ShipmentPackageStatus.OnHold, true, true)]
    [InlineData(" 0 ", ShipmentPackageStatus.New, true, true)]
    [InlineData("0", ShipmentPackageStatus.Cancelled, true, false)]
    [InlineData("0", ShipmentPackageStatus.PaymentPending, false, false)]
    [InlineData("4234395651", ShipmentPackageStatus.PaymentPending, true, false)]
    public void TrendyolStatusPolicy_ClosesTheZeroPlaceholderWhateverItsStatusOnceARealPackageIsPresent(
        string externalPackageId,
        ShipmentPackageStatus status,
        bool hasAuthoritativePackage,
        bool expected)
    {
        Assert.Equal(expected, TrendyolShipmentPackageStatusPolicy.ShouldClosePlaceholder(externalPackageId, status, hasAuthoritativePackage));
    }

    // Live 11763229493: a delivered real package next to the "0" placeholder left Awaiting (PaymentPending) read as
    // PAYMENT_PENDING and blocked the sale invoice. The placeholder no longer decides the order once a real package exists.
    [Fact]
    public void TheZeroPlaceholderDoesNotDecideAnOrderThatHasARealPackage()
    {
        static ShipmentPackage Package(string id, ShipmentPackageStatus status, string raw) =>
            new() { ExternalPackageId = id, Status = status, RawStatus = raw };

        var withReal = new[] { Package("0", ShipmentPackageStatus.PaymentPending, "Awaiting"), Package("4287472684", ShipmentPackageStatus.Delivered, "Delivered") };
        var sources = ShipmentPackageSupersession.StatusSources(withReal);
        Assert.Equal([ShipmentPackageStatus.Delivered], sources);
        Assert.Equal(ShipmentPackageStatus.Delivered, ShipmentPackageStatusPolicy.Aggregate(sources!));

        // While Trendyol has not created the package yet, the placeholder alone is the order's state.
        var placeholderOnly = new[] { Package("0", ShipmentPackageStatus.PaymentPending, "Awaiting") };
        Assert.Equal([ShipmentPackageStatus.PaymentPending], ShipmentPackageSupersession.StatusSources(placeholderOnly));
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
