using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaReferencePolicyTests
{
    [Fact]
    public void Hepsiburada_categories_refresh_is_scheduled_with_reference_data()
    {
        Assert.True(ScheduledJobProducer.SupportsHepsiburadaScheduledPolicy("REFERENCE_DATA"));
        Assert.True(ScheduledJobProducer.SupportsHepsiburadaScheduledPolicy("ORDERS"));
        Assert.False(ScheduledJobProducer.SupportsHepsiburadaScheduledPolicy("PRODUCTS"));
    }

    [Theory]
    [InlineData("PRICE_WRITE")]
    [InlineData("STOCK_WRITE")]
    [InlineData("SHIPMENT_WRITE")]
    [InlineData("RETURN_WRITE")]
    public void Hepsiburada_external_write_policies_are_off_by_default(string resourceType)
    {
        Assert.False(ScheduledJobProducer.DefaultPolicyEnabled("HEPSIBURADA", resourceType));
        Assert.True(ScheduledJobProducer.DefaultPolicyEnabled("TRENDYOL", resourceType));
    }

    [Theory]
    [InlineData("HEPSIBURADA:ACTIVE", "HEPSIBURADA", "ACTIVE")]
    [InlineData("HEPSIBURADA:PARTIAL", "HEPSIBURADA", "PARTIAL")]
    [InlineData("HEPSIBURADA:PASSIVE", "HEPSIBURADA", "PASSIVE")]
    public void Hepsiburada_product_status_filters_parse(string filter, string platformCode, string state)
    {
        Assert.True(CatalogService.TryProductPlatformFilter(filter, out var parsedPlatform, out var parsedState));
        Assert.Equal(platformCode, parsedPlatform);
        Assert.Equal(state, parsedState);
    }
}
