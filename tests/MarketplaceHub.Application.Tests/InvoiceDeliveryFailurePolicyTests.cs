using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceDeliveryFailurePolicyTests
{
    [Theory]
    [InlineData(AdapterErrorClass.TransientNetwork)]
    [InlineData(AdapterErrorClass.Remote5xx)]
    [InlineData(AdapterErrorClass.BusinessConflict)]
    public void Ambiguous_external_delivery_failures_require_manual_reconciliation(AdapterErrorClass errorClass)
    {
        Assert.Equal(InvoiceDeliveryFailureDisposition.Unknown, InvoiceDeliveryFailurePolicy.Classify(errorClass));
    }

    [Fact]
    public void Rate_limit_is_the_only_retryable_delivery_failure()
    {
        Assert.Equal(InvoiceDeliveryFailureDisposition.Retry, InvoiceDeliveryFailurePolicy.Classify(AdapterErrorClass.RateLimit));
        Assert.Equal(InvoiceDeliveryFailureDisposition.Failed, InvoiceDeliveryFailurePolicy.Classify(AdapterErrorClass.Validation));
    }
}
