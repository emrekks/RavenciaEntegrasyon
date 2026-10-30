using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class StockPushPipelinePolicyTests
{
    private static readonly DateTimeOffset ChangedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void AutomaticGate_NeedsTheMasterSwitchAndBothPolicies(bool master, bool price, bool stock, bool open) =>
        Assert.Equal(open, AutomaticPriceStockWriteGate.IsOpen(master, price, stock));

    [Fact]
    public void AutomaticGate_TreatsAMissingPolicyRowAsOff() =>
        Assert.False(AutomaticPriceStockWriteGate.IsOpen(true, null, true));

    [Fact]
    public void OrderFreshness_WaitsUntilAReadStartedAfterTheChangeHasFinished()
    {
        var now = ChangedAt.AddSeconds(40);
        var maxWait = TimeSpan.FromMinutes(10);
        // The last read started before the change.
        Assert.Equal(OrderFreshnessDecision.Wait, OrderFreshnessGatePolicy.Decide(true, ChangedAt, ChangedAt.AddSeconds(-30), ChangedAt.AddSeconds(-29), now, maxWait));
        // A read started after the change is still running.
        Assert.Equal(OrderFreshnessDecision.Wait, OrderFreshnessGatePolicy.Decide(true, ChangedAt, ChangedAt.AddSeconds(10), ChangedAt.AddSeconds(-29), now, maxWait));
        // It finished.
        Assert.Equal(OrderFreshnessDecision.Send, OrderFreshnessGatePolicy.Decide(true, ChangedAt, ChangedAt.AddSeconds(10), ChangedAt.AddSeconds(12), now, maxWait));
    }

    [Fact]
    public void OrderFreshness_HealthyNotificationsCoverTheChangeOnceTheyHadTimeToArrive()
    {
        var maxWait = TimeSpan.FromMinutes(10);
        var lastAttempt = ChangedAt.AddSeconds(-30);
        var lastSuccess = ChangedAt.AddSeconds(-20);
        // Within the delivery grace an order placed just before the change may still be on its way.
        Assert.Equal(OrderFreshnessDecision.Wait, OrderFreshnessGatePolicy.Decide(true, ChangedAt, lastAttempt, lastSuccess, ChangedAt.AddSeconds(90), maxWait, webhookHealthy: true));
        Assert.False(OrderFreshnessGatePolicy.NotificationGraceElapsed(ChangedAt, ChangedAt.AddSeconds(90)));
        // After it, healthy notifications with nothing left to ingest cover the change without the (eight-minute) read.
        Assert.True(OrderFreshnessGatePolicy.NotificationGraceElapsed(ChangedAt, ChangedAt.AddMinutes(2)));
        Assert.Equal(OrderFreshnessDecision.Send, OrderFreshnessGatePolicy.Decide(true, ChangedAt, lastAttempt, lastSuccess, ChangedAt.AddMinutes(2), maxWait, webhookHealthy: true));
        // A received notification still waiting to be ingested holds the send.
        Assert.Equal(OrderFreshnessDecision.Wait, OrderFreshnessGatePolicy.Decide(true, ChangedAt, lastAttempt, lastSuccess, ChangedAt.AddMinutes(3), maxWait, webhookHealthy: true, notificationsPending: true));
        // Unhealthy notifications leave the scheduled read as the only cover.
        Assert.Equal(OrderFreshnessDecision.Wait, OrderFreshnessGatePolicy.Decide(true, ChangedAt, lastAttempt, lastSuccess, ChangedAt.AddMinutes(3), maxWait, webhookHealthy: false));
        Assert.Equal(OrderFreshnessDecision.SendAfterTimeout, OrderFreshnessGatePolicy.Decide(true, ChangedAt, lastAttempt, lastSuccess, ChangedAt.AddMinutes(10), maxWait, webhookHealthy: true, notificationsPending: true));
    }

    [Theory]
    [InlineData(false, false, "POLLING")]
    [InlineData(false, true, "POLLING")]
    [InlineData(true, true, "WEBHOOK")]
    [InlineData(true, false, "WEBHOOK_FALLBACK")]
    public void OrderSource_IsTheWebhookOnlyWhileItIsHealthy(bool hasSubscription, bool healthy, string expected) =>
        Assert.Equal(expected, WebhookPollingPolicy.OrderSource(hasSubscription, healthy));

    [Theory]
    [InlineData("TRENDYOL", 180, true, 480)]
    [InlineData("TRENDYOL", 180, false, 180)]
    [InlineData("HEPSIBURADA", 120, true, 480)]
    [InlineData("SHOPIFY", 120, false, 120)]
    [InlineData("N11", 120, false, 120)]
    public void OrderSource_ShowsTheCadenceTheSchedulerActuallyReadsAt(string platform, int policyInterval, bool healthy, int expected) =>
        Assert.Equal(expected, WebhookPollingPolicy.EffectiveOrderIntervalSeconds(platform, policyInterval, healthy));

    [Fact]
    public void OrderFreshness_SendsAfterTheBoundOrWhenNoOrdersAreRead()
    {
        var maxWait = TimeSpan.FromMinutes(10);
        Assert.Equal(OrderFreshnessDecision.SendAfterTimeout, OrderFreshnessGatePolicy.Decide(true, ChangedAt, null, null, ChangedAt.AddMinutes(10), maxWait));
        Assert.Equal(OrderFreshnessDecision.Send, OrderFreshnessGatePolicy.Decide(false, ChangedAt, null, null, ChangedAt, maxWait));
        Assert.Equal(OrderFreshnessDecision.Send, OrderFreshnessGatePolicy.Decide(true, null, null, null, ChangedAt, maxWait));
    }

    [Fact]
    public void PriceChangeKey_IsTheOffersPriceVersionNotTheStockVersion()
    {
        var connection = Guid.NewGuid();
        var variant = Guid.NewGuid();
        Assert.NotEqual(StockProjectionOutboxPolicy.PriceChangeDedupKey(connection, variant, 3), StockProjectionOutboxPolicy.DedupKey(connection, variant, 3));
        Assert.NotEqual(StockProjectionOutboxPolicy.PriceChangeDedupKey(connection, variant, 3), StockProjectionOutboxPolicy.PriceChangeDedupKey(connection, variant, 4));
    }

    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    [InlineData("SHOPIFY")]
    public void EveryMarketplace_HasTheStockCatchUpAndStartsWithAutomaticWritesOff(string platform)
    {
        var supported = platform switch
        {
            "HEPSIBURADA" => ScheduledJobProducer.SupportsHepsiburadaScheduledPolicy("STOCK_RECONCILE_SHORT"),
            "N11" => ScheduledJobProducer.SupportsN11ScheduledPolicy("STOCK_RECONCILE_SHORT"),
            "PAZARAMA" => ScheduledJobProducer.SupportsPazaramaScheduledPolicy("STOCK_RECONCILE_SHORT"),
            "PTTAVM" => ScheduledJobProducer.SupportsPttAvmScheduledPolicy("STOCK_RECONCILE_SHORT"),
            "SHOPIFY" => ScheduledJobProducer.SupportsShopifyPolicy("STOCK_RECONCILE_SHORT"),
            _ => true
        };
        Assert.True(supported);
        Assert.True(ScheduledJobProducer.DefaultPolicyEnabled(platform, "STOCK_RECONCILE_DAILY"));
        Assert.False(ScheduledJobProducer.DefaultPolicyEnabled(platform, MarketplaceExternalWritePolicies.Price));
        Assert.False(ScheduledJobProducer.DefaultPolicyEnabled(platform, MarketplaceExternalWritePolicies.Stock));
    }
}
