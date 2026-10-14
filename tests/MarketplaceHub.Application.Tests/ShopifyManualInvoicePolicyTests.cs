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

    [Fact]
    public void Tracking_amount_prefers_a_real_package_amount_and_falls_back_to_order_total()
    {
        var cases = new (decimal Order, decimal? Package, decimal Expected)[]
        {
            (854.05m, 854.05m, 854.05m),
            (854.05m, 0m, 854.05m),
            (854.05m, null, 854.05m),
            (854.05m, 89.90m, 89.90m)
        };

        foreach (var (orderAmount, packageAmount, expected) in cases)
            Assert.Equal(expected, ShopifyManualInvoicePolicy.TrackingAmount(orderAmount, packageAmount));
    }

}
