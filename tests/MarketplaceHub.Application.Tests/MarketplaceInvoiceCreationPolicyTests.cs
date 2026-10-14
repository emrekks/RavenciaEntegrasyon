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

    [Fact]
    public void InvalidMarketplaceSettingsFailClosed()
    {
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("TRENDYOL", "not-json"));
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("SHOPIFY", "[]"));
    }
}
