using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ScheduledOrderPollingCadencePolicyTests
{
    [Theory]
    [InlineData("TRENDYOL", "ORDERS", 60, 5, 180, 5)]
    [InlineData("TRENDYOL", "ORDER_LIFECYCLE", 180, 10, 180, 10)]
    [InlineData("SHOPIFY", "ORDERS", 60, 5, 480, 30)]
    [InlineData("SHOPIFY", "ORDER_LIFECYCLE", 180, 10, 480, 10)]
    public void ForPlatform_UsesRequestedOrderCadence(
        string platformCode,
        string resourceType,
        int configuredInterval,
        int configuredJitter,
        int expectedInterval,
        int expectedJitter)
    {
        var result = ScheduledOrderPollingCadencePolicy.ForPlatform(
            platformCode,
            resourceType,
            configuredInterval,
            configuredJitter);

        Assert.Equal((expectedInterval, expectedJitter), result);
    }

    [Theory]
    [InlineData("SHOPIFY", "RETURNS", 180, 10)]
    [InlineData("TRENDYOL", "ORDER_RECOVERY", 900, 30)]
    [InlineData("OTHER", "ORDERS", 60, 5)]
    public void ForPlatform_LeavesOtherSchedulesUnchanged(
        string platformCode,
        string resourceType,
        int intervalSeconds,
        int jitterSeconds)
    {
        var result = ScheduledOrderPollingCadencePolicy.ForPlatform(
            platformCode,
            resourceType,
            intervalSeconds,
            jitterSeconds);

        Assert.Equal((intervalSeconds, jitterSeconds), result);
    }
}
