using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// PTT AVM's "Manuel Dış Yazmalar" switch (MANUAL_WRITE) gates the price-stock and product sends a user starts.
public sealed class PttAvmManualWritePolicyTests
{
    [Fact]
    public void Decide_AllowsWhenMasterAndManualSwitchAreOn() =>
        Assert.Equal(PttAvmManualWriteDecision.Allowed, PttAvmManualWritePolicy.Decide(masterWritesEnabled: true, manualPolicyEnabled: true));

    [Fact]
    public void Decide_RefusesWhenManualSwitchIsOff() =>
        Assert.Equal(PttAvmManualWriteDecision.PolicyOff, PttAvmManualWritePolicy.Decide(masterWritesEnabled: true, manualPolicyEnabled: false));

    [Fact]
    public void Decide_RefusesWhenThePolicyRowIsMissing() =>
        Assert.Equal(PttAvmManualWriteDecision.PolicyOff, PttAvmManualWritePolicy.Decide(masterWritesEnabled: true, manualPolicyEnabled: null));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void Decide_MasterSwitchOffRefusesWhateverTheManualSwitch(bool? manualPolicyEnabled) =>
        Assert.Equal(PttAvmManualWriteDecision.MasterSwitchOff, PttAvmManualWritePolicy.Decide(masterWritesEnabled: false, manualPolicyEnabled));

    [Fact]
    public void RefusalMessageTellsWhereToTurnItOn()
    {
        Assert.Equal("EXTERNAL_WRITE_POLICY_DISABLED", PttAvmManualWritePolicy.DisabledCode);
        Assert.Contains("Pazaryerine Yazma", PttAvmManualWritePolicy.DisabledMessage);
    }

    [Fact]
    public void ManualPriceStockSendOnPttAvmNeedsTheManualSwitchOnly() =>
        Assert.Equal([MarketplaceExternalWritePolicies.Manual], PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: true, "PTTAVM"));

    [Theory]
    [InlineData("PAZARAMA")]
    [InlineData("N11")]
    [InlineData(null)]
    public void ManualPriceStockSendElsewhereKeepsTheConnectionSwitchAlone(string? platformCode) =>
        Assert.Empty(PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: true, platformCode));

    [Fact]
    public void AutomaticPriceStockSendOnPttAvmKeepsPriceAndStockPolicies() =>
        Assert.Equal([MarketplaceExternalWritePolicies.Price, MarketplaceExternalWritePolicies.Stock], PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: false, "PTTAVM"));

    [Fact]
    public void ManualSwitchIsAPermissionThatTheMasterSwitchSweepLeavesAlone()
    {
        Assert.False(MarketplaceExternalWritePolicies.IsPolicy(MarketplaceExternalWritePolicies.Manual));
        Assert.True(MarketplaceExternalWritePolicies.IsPermissionPolicy(MarketplaceExternalWritePolicies.Manual));
        Assert.True(MarketplaceSyncPolicyRules.RequiresExternalWrites(MarketplaceExternalWritePolicies.Manual));
    }

    [Fact]
    public void ManualSwitchExistsAndStartsOnForPttAvmOnly()
    {
        Assert.True(ScheduledJobProducer.SupportsPttAvmScheduledPolicy(MarketplaceExternalWritePolicies.Manual));
        Assert.True(ScheduledJobProducer.DefaultPolicyEnabled("PTTAVM", MarketplaceExternalWritePolicies.Manual));
        foreach (var platformCode in new[] { "TRENDYOL", "SHOPIFY", "HEPSIBURADA", "N11", "PAZARAMA" })
            Assert.False(ScheduledJobProducer.DefaultPolicyEnabled(platformCode, MarketplaceExternalWritePolicies.Manual));
        Assert.False(ScheduledJobProducer.SupportsN11ScheduledPolicy(MarketplaceExternalWritePolicies.Manual));
    }
}
