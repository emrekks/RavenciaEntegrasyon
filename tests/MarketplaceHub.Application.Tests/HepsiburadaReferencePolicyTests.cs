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

    [Fact]
    public void Trendyol_cargo_info_read_policy_is_enabled_only_for_Trendyol()
    {
        Assert.True(ScheduledJobProducer.DefaultPolicyEnabled("TRENDYOL", "ORDER_CARGO_INFO"));
        Assert.False(ScheduledJobProducer.DefaultPolicyEnabled("SHOPIFY", "ORDER_CARGO_INFO"));
        Assert.False(ScheduledJobProducer.DefaultPolicyEnabled("HEPSIBURADA", "ORDER_CARGO_INFO"));
    }

    [Fact]
    public void Trendyol_cargo_info_schedule_default_is_recognized()
    {
        var policy = new MarketplaceHub.Domain.ConnectionSyncPolicy
        {
            ResourceType = "ORDER_CARGO_INFO",
            IntervalSeconds = 300,
            OverlapSeconds = 0,
            JitterSeconds = 15
        };

        Assert.True(ScheduledJobProducer.IsKnownDefault(policy, "TRENDYOL"));
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
