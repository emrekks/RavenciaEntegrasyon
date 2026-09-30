using MarketplaceHub.Api.Marketplace;
using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceConnectionTestDispatchPolicyTests
{
    [Theory]
    [InlineData("TRENDYOL", true, MarketplaceJobTypes.ConnectionTest)]
    [InlineData("SHOPIFY", true, MarketplaceJobTypes.ShopifyConnectionTest)]
    [InlineData("HEPSIBURADA", true, MarketplaceJobTypes.HepsiburadaConnectionTest)]
    [InlineData("TRENDYOL_EFATURAM", false, InvoicingJobTypes.ConnectionTest)]
    public void Resolve_UsesTheProcessorAndJobTypeForThePlatform(
        string platformCode,
        bool expectedMarketplace,
        string expectedJobType)
    {
        var route = MarketplaceConnectionTestDispatchPolicy.Resolve(platformCode);

        Assert.Equal(expectedMarketplace, route.IsMarketplace);
        Assert.Equal(expectedJobType, route.JobType);
    }
}
