using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceMarketplaceRetryPolicyTests
{
    [Theory]
    [InlineData(InvoiceStatus.ManualReview, InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure, true)]
    [InlineData(InvoiceStatus.ManualReview, InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode, true)]
    [InlineData(InvoiceStatus.ManualReview, "DELIVERY_RESULT_UNKNOWN", false)]
    [InlineData(InvoiceStatus.ManualReview, "EFATURAM_FISCAL_PAYLOAD_INVALID", false)]
    [InlineData(InvoiceStatus.UnknownResult, null, false)]
    [InlineData(InvoiceStatus.Rejected, null, false)]
    [InlineData(InvoiceStatus.Completed, null, false)]
    public void OnlyKnownDeliveryReviewsCanEnterDeliveryRetry(InvoiceStatus status, string? errorCode, bool expected)
    {
        Assert.Equal(expected, InvoiceMarketplaceRetryPolicy.CanRetryDelivery(status, errorCode));
    }
}
