using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 9 Nov 2026: a price/stock send started on a product is manual. Only the general external-write gate (server
// switch and the connection's master switch) decides it; the automatic price/stock switch stays the gate of what the
// system sends on its own.
public sealed class PanelPriceStockSendPolicyTests
{
    [Fact]
    public void ManualSend_PassesWithTheGeneralGateOpen_WhateverTheAutomaticSwitch()
    {
        Assert.Equal(PanelPriceStockSendDecision.Allowed, PanelPriceStockSendPolicy.Decide(globalWritesEnabled: true, connectionWritesEnabled: true));
        // The job is manual, so the worker asks no automatic policy (PRICE_WRITE, STOCK_WRITE) on these marketplaces.
        foreach (var platform in new[] { "TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA" })
            Assert.Empty(PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: true, platform));
    }

    [Fact]
    public void AutomaticSend_StaysBlockedWhileTheAutomaticSwitchIsOff()
    {
        // The live state since 30 Oct 2026: master switch on, PRICE_WRITE and STOCK_WRITE off.
        Assert.False(AutomaticPriceStockWriteGate.IsOpen(masterSwitchOn: true, priceWriteEnabled: false, stockWriteEnabled: false));
        Assert.False(AutomaticPriceStockWriteGate.IsOpen(masterSwitchOn: true, priceWriteEnabled: null, stockWriteEnabled: null));
        Assert.Equal([MarketplaceExternalWritePolicies.Price, MarketplaceExternalWritePolicies.Stock], PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: false, "TRENDYOL"));
    }

    [Fact]
    public void GeneralGateClosed_StillBlocksAManualSend_AndNamesTheSwitch()
    {
        var connectionOff = PanelPriceStockSendPolicy.Decide(globalWritesEnabled: true, connectionWritesEnabled: false);
        Assert.Equal(PanelPriceStockSendDecision.ConnectionWritesOff, connectionOff);
        var message = PanelPriceStockSendPolicy.Message(connectionOff, "Trendyol");
        Assert.Contains("Trendyol bağlantısında Dış Yazma Ana Anahtarı kapalı", message);
        Assert.Contains("Entegrasyonlar", message);

        var globalOff = PanelPriceStockSendPolicy.Decide(globalWritesEnabled: false, connectionWritesEnabled: true);
        Assert.Equal(PanelPriceStockSendDecision.GlobalWritesOff, globalOff);
        Assert.Contains("genel dış yazma anahtarı", PanelPriceStockSendPolicy.Message(globalOff, "Trendyol"));
        Assert.Equal(PanelPriceStockSendDecision.GlobalWritesOff, PanelPriceStockSendPolicy.Decide(globalWritesEnabled: false, connectionWritesEnabled: false));
    }

    [Fact]
    public void BlockMessage_NeverBlamesTheAutomaticSwitch()
    {
        foreach (var decision in new[] { PanelPriceStockSendDecision.GlobalWritesOff, PanelPriceStockSendDecision.ConnectionWritesOff })
        {
            var message = PanelPriceStockSendPolicy.Message(decision, "N11");
            Assert.DoesNotContain("Fiyat dış yazma akışı", message);
            Assert.DoesNotContain("Stok dış yazma akışı", message);
        }
    }

    [Fact]
    public void ConnectionSwitchMessage_NamesTheTabTheSwitchIsOn()
    {
        var message = PanelPriceStockSendPolicy.Message(PanelPriceStockSendDecision.ConnectionWritesOff, "N11");
        Assert.Contains("Pazaryerine Yazma sekmesinden", message);
        Assert.DoesNotContain("Dış Yazma Akışları", message);
    }
}
