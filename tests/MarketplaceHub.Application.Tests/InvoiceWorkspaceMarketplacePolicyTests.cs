using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceWorkspaceMarketplacePolicyTests
{
    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("SHOPIFY")]
    [InlineData("HEPSIBURADA")]
    public void SupportedMarketplaceOrdersCanAppearInInvoiceWorkspace(string platformCode)
    {
        Assert.True(InvoiceWorkspaceMarketplacePolicy.Supports(platformCode));
    }

    [Theory]
    [InlineData("TRENDYOL_EFATURAM")]
    [InlineData("UNKNOWN")]
    [InlineData(null)]
    public void NonMarketplaceConnectionsAreNotInvoiceWorkspaceSources(string? platformCode)
    {
        Assert.False(InvoiceWorkspaceMarketplacePolicy.Supports(platformCode));
    }

    [Fact]
    public void StatusFeedPackagesAreSafeToShowWhenAllocationIsKnown()
    {
        Assert.False(InvoiceWorkspacePackagePolicy.ShouldInclude("HEPSIBURADA_STATUS_FEED", false, 2));
        Assert.True(InvoiceWorkspacePackagePolicy.ShouldInclude("HEPSIBURADA_STATUS_FEED", false, 1));
        Assert.True(InvoiceWorkspacePackagePolicy.ShouldInclude("HEPSIBURADA_STATUS_FEED", true, 2));
        Assert.True(InvoiceWorkspacePackagePolicy.ShouldInclude("MARKETPLACE_DETAIL", false, 2));
    }
}
