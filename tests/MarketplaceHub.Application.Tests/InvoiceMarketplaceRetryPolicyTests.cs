using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceMarketplaceRetryPolicyTests
{
    [Theory]
    [InlineData(InvoiceStatus.ManualReview, InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure, true)]
    [InlineData(InvoiceStatus.ManualReview, InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode, true)]
    [InlineData(InvoiceStatus.ManualReview, InvoiceMarketplaceRetryPolicy.DeliveryIdentityChanged, true)]
    [InlineData(InvoiceStatus.ManualReview, "PLATFORM_INVOICE_ALREADY_PRESENT", false)]
    [InlineData(InvoiceStatus.ManualReview, "DELIVERY_RESULT_UNKNOWN", false)]
    [InlineData(InvoiceStatus.ManualReview, "EFATURAM_FISCAL_PAYLOAD_INVALID", false)]
    [InlineData(InvoiceStatus.UnknownResult, null, false)]
    [InlineData(InvoiceStatus.Rejected, null, false)]
    [InlineData(InvoiceStatus.Completed, null, false)]
    public void OnlyKnownDeliveryReviewsCanEnterDeliveryRetry(InvoiceStatus status, string? errorCode, bool expected)
    {
        Assert.Equal(expected, InvoiceMarketplaceRetryPolicy.CanRetryDelivery(status, errorCode));
    }

    [Theory]
    [InlineData(InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure, true)]
    [InlineData(InvoiceMarketplaceRetryPolicy.DeliveryIdentityChanged, true)]
    [InlineData(InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode, false)]
    [InlineData("PLATFORM_INVOICE_ALREADY_PRESENT", false)]
    [InlineData(null, false)]
    public void OnlyTheOwnersOwnRetryLiftsADeliveryStop(string? errorCode, bool expected)
    {
        Assert.Equal(expected, InvoiceMarketplaceRetryPolicy.IsOwnerApprovedRetry(errorCode));
    }

    // The delivery's identity is the invoice, the package and the invoice number: a newer document link, a refreshed
    // customer snapshot or the package's current lines are not in it, so a retry with them is the same delivery.
    [Fact]
    public void TheDeliveryIdentityIgnoresThePayloadAndFollowsInvoicePackageAndNumber()
    {
        var invoiceId = Guid.Parse("01a13648-4111-7c7c-ad91-01185461d3f4");
        var identity = InvoiceMarketplaceRetryPolicy.DeliveryIdentity(invoiceId, "PKG-1", "DAP2026000000437");

        Assert.Equal(identity, InvoiceMarketplaceRetryPolicy.DeliveryIdentity(invoiceId, " PKG-1 ", "DAP2026000000437 "));
        Assert.NotEqual(identity, InvoiceMarketplaceRetryPolicy.DeliveryIdentity(invoiceId, "PKG-2", "DAP2026000000437"));
        Assert.NotEqual(identity, InvoiceMarketplaceRetryPolicy.DeliveryIdentity(invoiceId, "PKG-1", "DAP2026000000438"));
        Assert.NotEqual(identity, InvoiceMarketplaceRetryPolicy.DeliveryIdentity(Guid.CreateVersion7(), "PKG-1", "DAP2026000000437"));
    }
}
