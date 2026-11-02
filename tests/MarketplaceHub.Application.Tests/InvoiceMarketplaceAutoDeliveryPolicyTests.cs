using Xunit;

namespace MarketplaceHub.Application.Tests;

// User decision, 2 Nov 2026: an issued invoice goes to its marketplace by itself on every platform with a delivery API,
// as on Hepsiburada. Live: an n11 invoice (DAP2026000000453) stayed silently undelivered because the n11 connection's
// hand-kept INVOICE_DELIVER record was "Unknown" after a credential rotation.
public sealed class InvoiceMarketplaceAutoDeliveryPolicyTests
{
    private const string WritesOn = """{"ExternalWritesEnabled":true}""";

    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    [InlineData("n11")]
    public void EveryPlatformWithADeliveryAdapterIsQueuedWithoutACapabilityRecord(string platform) =>
        Assert.Null(InvoiceMarketplaceAutoDeliveryPolicy.SkipReason(platform, "ACTIVE", "PRODUCTION", WritesOn, true, true, true));

    [Fact]
    public void ShopifyNeedsTheWriteScopeItsConnectionTestRecorded()
    {
        Assert.Null(InvoiceMarketplaceAutoDeliveryPolicy.SkipReason("SHOPIFY", "ACTIVE", "PRODUCTION", WritesOn, true, true, true, shopifyWriteScopeVerified: true));
        Assert.Equal(InvoiceMarketplaceAutoDeliveryPolicy.ShopifyScopeMissingCode,
            InvoiceMarketplaceAutoDeliveryPolicy.SkipReason("SHOPIFY", "ACTIVE", "PRODUCTION", WritesOn, true, true, true, shopifyWriteScopeVerified: false));
    }

    [Theory]
    [InlineData("AMAZON", "ACTIVE", "PRODUCTION", WritesOn, true, true, true, InvoiceMarketplaceAutoDeliveryPolicy.PlatformUnsupportedCode)]
    [InlineData(null, "ACTIVE", "PRODUCTION", WritesOn, true, true, true, InvoiceMarketplaceAutoDeliveryPolicy.PlatformUnsupportedCode)]
    [InlineData("N11", "ACTIVE", "PRODUCTION", WritesOn, true, true, false, InvoiceMarketplaceAutoDeliveryPolicy.LinkMissingCode)]
    [InlineData("N11", "ACTIVE", "PRODUCTION", WritesOn, false, true, true, InvoiceMarketplaceAutoDeliveryPolicy.WritesOffCode)]
    [InlineData("N11", "ACTIVE", "PRODUCTION", WritesOn, true, false, true, InvoiceMarketplaceAutoDeliveryPolicy.WritesOffCode)]
    [InlineData("N11", "VERIFIED", "PRODUCTION", WritesOn, true, true, true, InvoiceMarketplaceAutoDeliveryPolicy.ConnectionNotLiveCode)]
    [InlineData("N11", "ACTIVE", "STAGE", WritesOn, true, true, true, InvoiceMarketplaceAutoDeliveryPolicy.ConnectionNotLiveCode)]
    [InlineData("N11", "ACTIVE", "PRODUCTION", "{}", true, true, true, InvoiceMarketplaceAutoDeliveryPolicy.ConnectionWritesOffCode)]
    [InlineData("N11", "ACTIVE", "PRODUCTION", """{"ExternalWritesEnabled":false}""", true, true, true, InvoiceMarketplaceAutoDeliveryPolicy.ConnectionWritesOffCode)]
    [InlineData("N11", "ACTIVE", "PRODUCTION", "not json", true, true, true, InvoiceMarketplaceAutoDeliveryPolicy.ConnectionWritesOffCode)]
    public void AnInvoiceThatIsNotSentGetsItsReason(string? platform, string status, string environment, string settings, bool globalWrites, bool deliveryWrites, bool linkReady, string expected) =>
        Assert.Equal(expected, InvoiceMarketplaceAutoDeliveryPolicy.SkipReason(platform, status, environment, settings, globalWrites, deliveryWrites, linkReady));

    [Fact]
    public void SkipCodesAreRecognisedAndOtherCodesAreNot()
    {
        Assert.True(InvoiceMarketplaceAutoDeliveryPolicy.IsSkipCode(InvoiceMarketplaceAutoDeliveryPolicy.WritesOffCode));
        Assert.True(InvoiceMarketplaceAutoDeliveryPolicy.IsSkipCode(InvoiceMarketplaceAutoDeliveryPolicy.PackageMissingCode));
        Assert.False(InvoiceMarketplaceAutoDeliveryPolicy.IsSkipCode("REMOTE_INVOICE_REJECTED"));
        Assert.False(InvoiceMarketplaceAutoDeliveryPolicy.IsSkipCode(null));
    }

    [Fact]
    public void RecoveryOfNeverQueuedInvoicesLooksBackTwoWeeks() =>
        Assert.Equal(TimeSpan.FromDays(14), InvoiceMarketplaceAutoDeliveryPolicy.NeverQueuedRecoveryWindow);
}
