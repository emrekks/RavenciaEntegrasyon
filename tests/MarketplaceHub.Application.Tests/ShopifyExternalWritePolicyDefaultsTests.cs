using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyExternalWritePolicyDefaultsTests
{
    [Theory]
    [InlineData(MarketplaceExternalWritePolicies.Price)]
    [InlineData(MarketplaceExternalWritePolicies.Stock)]
    [InlineData(MarketplaceExternalWritePolicies.Shipment)]
    [InlineData(MarketplaceExternalWritePolicies.Return)]
    public void Shopify_write_controls_are_provisioned_but_start_disabled(string resourceType)
    {
        Assert.True(ScheduledJobProducer.SupportsShopifyPolicy(resourceType));
        Assert.False(ScheduledJobProducer.DefaultPolicyEnabled("SHOPIFY", resourceType));
    }

    [Theory]
    [InlineData(MarketplaceExternalWritePolicies.Price)]
    [InlineData(MarketplaceExternalWritePolicies.Stock)]
    [InlineData(MarketplaceExternalWritePolicies.Shipment)]
    [InlineData(MarketplaceExternalWritePolicies.Return)]
    public void Shopify_write_controls_are_not_scheduled_as_background_jobs(string resourceType)
    {
        Assert.False(ScheduledJobProducer.IsShopifyReadPolicy(resourceType));
    }
}
