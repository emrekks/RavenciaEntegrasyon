using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OneTimeInvoiceDeliveryPolicyTests
{
    [Fact]
    public void StageExceptionIsBoundToExactInvoiceAndOrder()
    {
        var invoice = Guid.Parse("01a125c4-a478-74a8-8865-943228b09515");
        Assert.True(OneTimeInvoiceDeliveryPolicy.IsAuthorizedStageDocument("4030000001", invoice));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsAuthorizedStageDocument("4480000001", invoice));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsAuthorizedStageDocument("4030000001", Guid.NewGuid()));
        Assert.True(OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget("4030000001"));
        Assert.True(OneTimeInvoiceDeliveryPolicy.IsEligibleSourceFailure(InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure, false));
        Assert.True(OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure("FAILED", InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure, null));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure("UNKNOWN", InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure, null));
    }

    [Fact]
    public void TargetOrderIsExactAndCaseSensitive()
    {
        Assert.True(OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget("4480000001"));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget("4486229625"));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget(null));
    }

    [Fact]
    public void OnlyKnownPreWriteCapabilityFailureCanBeOverriddenOnce()
    {
        Assert.True(OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure(
            "FAILED",
            OneTimeInvoiceDeliveryPolicy.PriorNoWriteFailureCode,
            null));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure("STARTED", OneTimeInvoiceDeliveryPolicy.PriorNoWriteFailureCode, null));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure("UNKNOWN", OneTimeInvoiceDeliveryPolicy.PriorNoWriteFailureCode, null));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure("FAILED", "REMOTE_REJECTED", null));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure("FAILED", OneTimeInvoiceDeliveryPolicy.PriorNoWriteFailureCode, "remote-reference"));
    }

    [Fact]
    public void LatestAlreadyFailedGuardIsEligibleOnlyWhenEarlierAttemptProvesNoWrite()
    {
        Assert.True(OneTimeInvoiceDeliveryPolicy.IsEligibleSourceFailure(
            OneTimeInvoiceDeliveryPolicy.DeliveryAlreadyFailedErrorCode,
            hasPriorNoWriteAttempt: true));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsEligibleSourceFailure(
            OneTimeInvoiceDeliveryPolicy.DeliveryAlreadyFailedErrorCode,
            hasPriorNoWriteAttempt: false));
        Assert.False(OneTimeInvoiceDeliveryPolicy.IsEligibleSourceFailure("REMOTE_REJECTED", hasPriorNoWriteAttempt: true));
    }
}
