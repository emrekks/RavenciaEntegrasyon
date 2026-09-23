using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceInvoiceCreationPolicyTests
{
    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("SHOPIFY")]
    public void MissingSettingKeepsExistingMarketplaceConnectionsEnabled(string platformCode)
    {
        Assert.True(MarketplaceInvoiceCreationPolicy.IsEnabled(platformCode, "{\"ExternalWritesEnabled\":false}"));
    }

    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("SHOPIFY")]
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
