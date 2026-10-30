using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ScheduledOrderPollingCadencePolicyTests
{
    [Theory]
    [InlineData("STAGE", 60, 600)]
    [InlineData("stage", 180, 600)]
    [InlineData("STAGE", 1800, 1800)]
    [InlineData("PRODUCTION", 60, 60)]
    [InlineData(null, 60, 60)]
    public void ATestConnectionIsReadEveryTenMinutesAtMost(string? environment, int interval, int expected) =>
        Assert.Equal(expected, ScheduledOrderPollingCadencePolicy.ForEnvironment(interval, environment));

    [Theory]
    [InlineData("TRENDYOL", "ORDERS", 60, 5, 60, 5)]
    [InlineData("TRENDYOL", "ORDER_LIFECYCLE", 180, 10, 1800, 10)]
    [InlineData("TRENDYOL", "ORDER_CARGO_INFO", 300, 15, 1800, 15)]
    [InlineData("TRENDYOL", "ORDER_RECONCILE_SHORT", 900, 30, 3600, 30)]
    [InlineData("TRENDYOL", "ORDER_RECONCILE_MEDIUM", 3600, 120, 21_600, 120)]
    [InlineData("TRENDYOL", "ORDER_RECONCILE_DAILY", 86_400, 900, 86_400, 900)]
    [InlineData("SHOPIFY", "ORDERS", 60, 5, 120, 30)]
    [InlineData("SHOPIFY", "ORDERS", 180, 5, 120, 30)]
    // Owner decision 30 Oct 2026: new orders without (healthy) notifications every two minutes, Trendyol included.
    [InlineData("TRENDYOL", "ORDERS", 180, 5, 120, 5)]
    [InlineData("HEPSIBURADA", "ORDERS", 180, 5, 120, 5)]
    [InlineData("N11", "ORDERS", 180, 5, 120, 5)]
    [InlineData("PAZARAMA", "ORDERS", 180, 5, 120, 5)]
    [InlineData("PTTAVM", "ORDERS", 180, 5, 120, 5)]
    [InlineData("PAZARAMA", "ORDER_LIFECYCLE", 180, 10, 120, 10)]
    [InlineData("PTTAVM", "ORDER_LIFECYCLE", 180, 10, 900, 10)]
    [InlineData("SHOPIFY", "ORDER_LIFECYCLE", 180, 10, 1800, 10)]
    [InlineData("TRENDYOL", "RETURN_LIFECYCLE", 180, 10, 1800, 10)]
    [InlineData("TRENDYOL", "RETURN_RECONCILE_SHORT", 900, 30, 3600, 30)]
    [InlineData("N11", "ORDER_RECONCILE_MEDIUM", 3600, 120, 21_600, 120)]
    [InlineData("TRENDYOL", "ORDER_RECOVERY", 900, 30, 900, 30)]
    [InlineData("SHOPIFY", "ORDER_RECOVERY", 900, 30, 3600, 30)]
    [InlineData("SHOPIFY", "ORDER_RECONCILE_SHORT", 900, 30, 2700, 30)]
    [InlineData("SHOPIFY", "ORDER_RECONCILE_MEDIUM", 3600, 120, 10800, 120)]
    [InlineData("SHOPIFY", "ORDER_RECONCILE_DAILY", 86400, 900, 259200, 900)]
    [InlineData("HEPSIBURADA", "ORDER_RECOVERY", 900, 30, 900, 30)]
    [InlineData("HEPSIBURADA", "ORDER_LIFECYCLE", 180, 10, 120, 10)]
    [InlineData("HEPSIBURADA", "ORDER_INVOICE_RECONCILIATION", 900, 30, 1800, 10)]
    [InlineData("HEPSIBURADA", "ORDER_INVOICE_RECONCILIATION", 300, 10, 1800, 10)]
    [InlineData("HEPSIBURADA", "ORDER_INVOICE_RECONCILIATION", 1800, 30, 1800, 30)]
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

    // The live policy rows still at an application default move to the new cadence on the worker's defaults pass
    // (startup and hourly); a policy the user saved keeps its values.
    [Theory]
    [InlineData("TRENDYOL", "ORDERS", 180, 600, 5, 120, 5)]
    [InlineData("HEPSIBURADA", "ORDERS", 180, 600, 5, 120, 5)]
    [InlineData("N11", "ORDERS", 180, 600, 5, 120, 5)]
    [InlineData("PAZARAMA", "ORDERS", 180, 600, 5, 120, 5)]
    [InlineData("SHOPIFY", "ORDERS", 180, 600, 30, 120, 30)]
    [InlineData("HEPSIBURADA", "ORDER_LIFECYCLE", 180, 0, 10, 120, 10)]
    [InlineData("PAZARAMA", "ORDER_LIFECYCLE", 180, 0, 10, 120, 10)]
    public void LivePoliciesAtTheOldDefaultMoveToTwoMinutes(string platformCode, string resourceType, int interval, int overlap, int jitter, int expectedInterval, int expectedJitter)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var current = new MarketplaceHub.Domain.ConnectionSyncPolicy { ResourceType = resourceType, IntervalSeconds = interval, OverlapSeconds = overlap, JitterSeconds = jitter };
        Assert.True(ScheduledJobProducer.ShouldUpgradeToDefault(current, platformCode));
        Assert.Equal((expectedInterval, overlap, expectedJitter), ScheduledJobProducer.DefaultPolicyValues(configuration, platformCode, resourceType));
        Assert.False(ScheduledJobProducer.ShouldUpgradeToDefault(new MarketplaceHub.Domain.ConnectionSyncPolicy { ResourceType = resourceType, IntervalSeconds = interval, OverlapSeconds = overlap, JitterSeconds = jitter, UserDefined = true }, platformCode));
        // The new default is itself a known default, so a later change can move it again.
        Assert.True(ScheduledJobProducer.IsKnownDefault(new MarketplaceHub.Domain.ConnectionSyncPolicy { ResourceType = resourceType, IntervalSeconds = expectedInterval, OverlapSeconds = overlap, JitterSeconds = expectedJitter }, platformCode));
    }

    [Fact]
    public void TrendyolReadsAtTwoMinutesOnlyWhileItsNotificationsAreUnhealthy()
    {
        Assert.Equal((120, 600, 5), ScheduledJobProducer.DefaultPolicyValues(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "TRENDYOL", "ORDERS"));
        Assert.True(ScheduledJobProducer.ShouldUpgradeToDefault(new MarketplaceHub.Domain.ConnectionSyncPolicy { ResourceType = "ORDERS", IntervalSeconds = 180, OverlapSeconds = 600, JitterSeconds = 5 }, "TRENDYOL"));
        Assert.Equal(480, MarketplaceHub.Application.WebhookPollingPolicy.EffectiveOrderIntervalSeconds("TRENDYOL", 120, healthy: true));
        Assert.Equal(120, MarketplaceHub.Application.WebhookPollingPolicy.EffectiveOrderIntervalSeconds("TRENDYOL", 120, healthy: false));
    }
}
