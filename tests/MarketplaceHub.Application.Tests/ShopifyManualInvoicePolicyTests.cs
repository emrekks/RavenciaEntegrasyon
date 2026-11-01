using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyManualInvoicePolicyTests
{
    [Theory]
    [InlineData("SHOPIFY", "SALE", true)]
    [InlineData("shopify", "SHOPIFY_MANUAL", true)]
    [InlineData("TRENDYOL_EFATURAM", "SHOPIFY_MANUAL", true)]
    [InlineData("TRENDYOL_EFATURAM", "SALE", false)]
    public void Manual_tracking_records_are_never_fiscal_drafts(string platform, string sequencePurpose, bool expected) =>
        Assert.Equal(expected, ShopifyManualInvoicePolicy.IsManualOnly(platform, sequencePurpose));
}
