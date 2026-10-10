using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OneTimeInvoiceDeliveryPolicyTests
{
    [Fact]
    public void TargetOrderIsExactAndCaseSensitive()
    {
        Assert.True(OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget("4486229624"));
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
