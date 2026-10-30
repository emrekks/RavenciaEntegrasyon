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
