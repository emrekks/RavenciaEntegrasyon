using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ScheduledOrderPollingCadencePolicyTests
{
    [Theory]
    [InlineData("TRENDYOL", "ORDERS", 60, 5, 180, 5)]
    [InlineData("TRENDYOL", "ORDER_LIFECYCLE", 180, 10, 180, 10)]
    [InlineData("SHOPIFY", "ORDERS", 60, 5, 540, 30)]
    [InlineData("SHOPIFY", "ORDER_LIFECYCLE", 180, 10, 540, 10)]
    [InlineData("TRENDYOL", "ORDER_RECOVERY", 900, 30, 900, 30)]
    [InlineData("SHOPIFY", "ORDER_RECOVERY", 900, 30, 2700, 30)]
    [InlineData("TRENDYOL", "ORDER_RECONCILE_SHORT", 900, 30, 900, 30)]
    [InlineData("SHOPIFY", "ORDER_RECONCILE_SHORT", 900, 30, 2700, 30)]
    [InlineData("TRENDYOL", "ORDER_RECONCILE_MEDIUM", 3600, 120, 3600, 120)]
    [InlineData("SHOPIFY", "ORDER_RECONCILE_MEDIUM", 3600, 120, 10800, 120)]
    [InlineData("TRENDYOL", "ORDER_RECONCILE_DAILY", 86400, 900, 86400, 900)]
    [InlineData("SHOPIFY", "ORDER_RECONCILE_DAILY", 86400, 900, 259200, 900)]
    [InlineData("HEPSIBURADA", "ORDER_RECOVERY", 900, 30, 900, 30)]
    [InlineData("HEPSIBURADA", "ORDER_INVOICE_RECONCILIATION", 900, 30, 900, 30)]
    [InlineData("HEPSIBURADA", "ORDERS", 60, 5, 60, 5)]
    [InlineData("HEPSIBURADA", "RETURNS", 180, 10, 180, 10)]
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
