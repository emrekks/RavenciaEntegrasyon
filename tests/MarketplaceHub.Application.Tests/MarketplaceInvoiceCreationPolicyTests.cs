using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceInvoiceCreationPolicyTests
{
    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    public void MissingSettingKeepsExistingMarketplaceConnectionsEnabled(string platformCode)
    {
        Assert.True(MarketplaceInvoiceCreationPolicy.IsEnabled(platformCode, "{\"ExternalWritesEnabled\":false}"));
    }

    [Fact]
    public void ShopifyFiscalCreationRemainsDisabledUntilProviderContractIsVerified()
    {
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("SHOPIFY", "{}"));
    }

    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    public void ExplicitFalseDisablesInvoiceCreationForMarketplaceConnection(string platformCode)
    {
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled(platformCode, "{\"ExternalWritesEnabled\":false,\"InvoiceCreationEnabled\":false}"));
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled(platformCode, "{\"invoiceCreationEnabled\":false}"));
    }

    [Fact]
    public void ProviderConnectionIsNotControlledByMarketplaceInvoiceSetting()
    {
        Assert.True(MarketplaceInvoiceCreationPolicy.IsEnabled("TRENDYOL_EFATURAM", "{\"InvoiceCreationEnabled\":false}"));
    }

    // Every marketplace connection carries the Faturalama switch both ways, Hepsiburada included.
    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    public void EveryMarketplaceButShopifyMayTurnInvoiceCreationOnAndOff(string platformCode)
    {
        Assert.Null(MarketplaceInvoiceCreationPolicy.UpdateRefusal(platformCode, true));
        Assert.Null(MarketplaceInvoiceCreationPolicy.UpdateRefusal(platformCode, false));
        Assert.Null(MarketplaceInvoiceCreationPolicy.UpdateRefusal(platformCode, null));
        Assert.True(MarketplaceInvoiceCreationPolicy.IsEnabled(platformCode, "{\"ExternalWritesEnabled\":true,\"InvoiceCreationEnabled\":true}"));
    }

    // Shopify's switch cannot be turned on: the invoicing flow would ignore it, so the request is refused instead.
    [Fact]
    public void ShopifyInvoiceCreationCannotBeTurnedOnButMayBeKeptOff()
    {
        Assert.Equal(MarketplaceInvoiceCreationPolicy.UnsupportedFiscalProviderMessage, MarketplaceInvoiceCreationPolicy.UpdateRefusal("SHOPIFY", true));
        Assert.Null(MarketplaceInvoiceCreationPolicy.UpdateRefusal("SHOPIFY", false));
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("SHOPIFY", "{\"ExternalWritesEnabled\":true,\"InvoiceCreationEnabled\":true}"));
    }

    [Fact]
    public void TheInvoiceSwitchBelongsToMarketplaceConnectionsOnly()
    {
        Assert.NotNull(MarketplaceInvoiceCreationPolicy.UpdateRefusal("TRENDYOL_EFATURAM", false));
        Assert.Null(MarketplaceInvoiceCreationPolicy.UpdateRefusal("TRENDYOL_EFATURAM", null));
    }

    [Fact]
    public void InvalidMarketplaceSettingsFailClosed()
    {
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("TRENDYOL", "not-json"));
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("SHOPIFY", "[]"));
    }
}
