using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The price/stock audit of 30 Oct 2026 and the owner's decisions on it.
public sealed class PriceStockAuditFixesTests
{
    private static ChannelOffer Offer() => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        ConnectionId = Guid.CreateVersion7(),
        VariantId = Guid.CreateVersion7(),
        ListPrice = 150m,
        SalePrice = 120m,
        Currency = "TRY",
        VatRate = 20m,
        VatInclusion = "INCLUDED",
        RoundingMode = "HALF_EVEN",
        SafetyStock = 1m,
        Status = "ACTIVE",
        PriceVersion = 3,
        Version = 5
    };

    private static PriceInventoryPushLine Line(bool quantityOmitted = false, bool priceOmitted = false, decimal? safetyStock = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "CODE-1", 2m, 150m, 120m, "TRY", 7, 3, "hash", "MSKU-1", "HBV00000001", "gid://shopify/ProductVariant/1", quantityOmitted, priceOmitted, safetyStock);

    // Item 1 ("Fixle"): an offer save is a new price version only when a value of the outbound price changed.
    [Fact]
    public void OfferSave_IsAPriceChangeOnlyWhenAPriceValueChanged()
    {
        var offer = Offer();
        Assert.False(ChannelOfferChangePolicy.Classify(offer, 150m, 120m, "try", 20m, "included", "half_even", 1m, "ACTIVE").Any);
        Assert.Equal(new ChannelOfferChange(false, true, false), ChannelOfferChangePolicy.Classify(offer, 150m, 120m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 2m, "ACTIVE"));
        Assert.Equal(new ChannelOfferChange(false, false, true), ChannelOfferChangePolicy.Classify(offer, 150m, 120m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 1m, "PASSIVE"));
        Assert.True(ChannelOfferChangePolicy.Classify(offer, 150m, 119.99m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 1m, "ACTIVE").Price);
        Assert.True(ChannelOfferChangePolicy.Classify(offer, 150m, 120m, "TRY", 10m, "INCLUDED", "HALF_EVEN", 1m, "ACTIVE").Price);
        Assert.True(ChannelOfferChangePolicy.Classify(offer, 150m, 120m, "TRY", 20m, "EXCLUDED", "HALF_EVEN", 1m, "ACTIVE").Price);
        Assert.True(ChannelOfferChangePolicy.Classify(offer, 150m, 120m, "TRY", 20m, "INCLUDED", "UP", 1m, "ACTIVE").Price);
    }

    // A save that changed no price gets a dispatch of its own instead of being taken for the price's earlier one.
    [Fact]
    public void OfferSaveDispatchKey_NamesTheOfferVersion()
    {
        var connection = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();
        Assert.NotEqual(StockProjectionOutboxPolicy.PriceChangeDedupKey(connection, variant, 3, 5), StockProjectionOutboxPolicy.PriceChangeDedupKey(connection, variant, 3, 6));
        Assert.Equal($"price-change:{connection:N}:{variant:N}:p3", StockProjectionOutboxPolicy.PriceChangeDedupKey(connection, variant, 3));
    }

    // Item 7: an answer polled for an older safety stock does not set the stock mark the safety stock edit cleared.
    [Fact]
    public void PolledLine_IsStockCurrentOnlyWithTheSameProjectionAndSafetyStock()
    {
        Assert.True(PriceInventorySendPolicy.StockCurrent(Line(safetyStock: 1m), 7, 1m));
        Assert.False(PriceInventorySendPolicy.StockCurrent(Line(safetyStock: 1m), 7, 2m));
        Assert.False(PriceInventorySendPolicy.StockCurrent(Line(safetyStock: 1m), 8, 1m));
        // A line queued before lines carried the safety stock is judged by its projection.
        Assert.True(PriceInventorySendPolicy.StockCurrent(Line(), 7, 2m));
        Assert.True(PriceInventorySendPolicy.PriceCurrent(Line(), 3));
        Assert.False(PriceInventorySendPolicy.PriceCurrent(Line(), 4));
    }

    // Item 5 ("Stoksa sadece stok gitmeli"): a stock change alone carries no price on Hepsiburada, n11 and Shopify.
    [Fact]
    public void StockOnlyLines_CarryNoPrice()
    {
        var hepsiburada = JsonNode.Parse(PriceInventoryComposer.HepsiburadaPayload("merchant-1", [Line(priceOmitted: true)]))!["items"]![0]!.AsObject();
        Assert.False(hepsiburada.ContainsKey("price"));
        Assert.Equal(2, hepsiburada["availableStock"]!.GetValue<int>());
        Assert.True(HepsiburadaHttpClient.ValidPriceInventoryPayload(PriceInventoryComposer.HepsiburadaPayload("merchant-1", [Line(priceOmitted: true)]), "merchant-1"));
        Assert.DoesNotContain("<Price>", HepsiburadaHttpClient.BuildInventoryUploadXml(PriceInventoryComposer.HepsiburadaPayload("merchant-1", [Line(priceOmitted: true)])), StringComparison.Ordinal);
        Assert.Contains("<Price>", HepsiburadaHttpClient.BuildInventoryUploadXml(PriceInventoryComposer.HepsiburadaPayload("merchant-1", [Line()])), StringComparison.Ordinal);
        // A line with neither value is never valid.
        Assert.False(HepsiburadaHttpClient.ValidPriceInventoryPayload("""{"merchantId":"merchant-1","items":[{"hepsiburadaSku":"HBV00000001","merchantSku":"MSKU-1"}]}""", "merchant-1"));

        var n11 = JsonNode.Parse(N11PriceInventoryRules.Payload([Line(priceOmitted: true)]))!["payload"]!["skus"]![0]!.AsObject();
        Assert.False(n11.ContainsKey("listPrice"));
        Assert.False(n11.ContainsKey("salePrice"));
        Assert.Equal(2, n11["quantity"]!.GetValue<int>());
        var n11Priced = JsonNode.Parse(N11PriceInventoryRules.Payload([Line()]))!["payload"]!["skus"]![0]!.AsObject();
        Assert.True(n11Priced.ContainsKey("salePrice"));

        var shopify = JsonNode.Parse(PriceInventoryComposer.ShopifyPayload([Line(priceOmitted: true)]))!["items"]![0]!.AsObject();
        Assert.False(shopify.ContainsKey("salePrice"));
        Assert.False(shopify.ContainsKey("listPrice"));
        Assert.Equal(2, shopify["quantity"]!.GetValue<int>());
        Assert.Equal(120m, JsonNode.Parse(PriceInventoryComposer.ShopifyPayload([Line()]))!["items"]![0]!["salePrice"]!.GetValue<decimal>());
    }

    // Item 5, PTT AVM: PriceWithVAT is required, so a stock change alone carries the price PTT AVM last confirmed.
    [Fact]
    public void PttAvmStockOnly_UsesTheConfirmedPriceOfTheSameCode()
    {
        var offer = Offer();
        Assert.Null(PriceInventoryComposer.PttAvmConfirmedPrice(offer, "CODE-1"));
        offer.LastSentCode = "CODE-1";
        offer.LastSentSalePrice = 99.5m;
        Assert.Equal(99.5m, PriceInventoryComposer.PttAvmConfirmedPrice(offer, "CODE-1"));
        Assert.Null(PriceInventoryComposer.PttAvmConfirmedPrice(offer, "CODE-2"));
        offer.LastSentSalePrice = 1m;
        Assert.Null(PriceInventoryComposer.PttAvmConfirmedPrice(offer, "CODE-1"));
    }

    // Item 2 ("fixle"): the emergency stop cancels only automatic sends not yet submitted.
    [Fact]
    public void EmergencyStop_CancelsOnlyAutomaticJobsNotYetSubmitted()
    {
        string Payload(string phase, bool automatic, bool manual = false) => JsonSerializer.Serialize(new PriceInventoryJobPayload(Guid.CreateVersion7(), Guid.CreateVersion7(), phase, "h", "{}", [], null, null, Manual: manual, Automatic: automatic));
        Assert.True(PriceStockEmergencyStopPolicy.Cancels(MarketplaceJobTypes.StockProjectionDispatch, "{}"));
        Assert.True(PriceStockEmergencyStopPolicy.Cancels(MarketplaceJobTypes.StockReconciliation, "{}"));
        Assert.True(PriceStockEmergencyStopPolicy.Cancels(MarketplaceJobTypes.PriceInventorySync, Payload("SUBMIT", automatic: true)));
        Assert.False(PriceStockEmergencyStopPolicy.Cancels(MarketplaceJobTypes.PriceInventorySync, Payload("POLL", automatic: true)));
        Assert.False(PriceStockEmergencyStopPolicy.Cancels(MarketplaceJobTypes.PriceInventorySync, Payload("SUBMIT", automatic: false)));
        Assert.False(PriceStockEmergencyStopPolicy.Cancels(MarketplaceJobTypes.PriceInventorySync, Payload("SUBMIT", automatic: false, manual: true)));
        Assert.False(PriceStockEmergencyStopPolicy.Cancels(MarketplaceJobTypes.PriceInventorySync, "{bozuk"));
    }

    // Item 3: the repair names exactly the 11 lines of the 28 and 30 Oct outages, each on its own inventory item.
    [Fact]
    public void Repair_NamesTheElevenMissedLinesOnDistinctItems()
    {
        Assert.Equal(11, RepairOrderStockAndReleaseReservations.MissedLines.Length);
        Assert.Equal(11, RepairOrderStockAndReleaseReservations.MissedLines.Select(x => x.LineId).Distinct().Count());
        Assert.Equal(11, RepairOrderStockAndReleaseReservations.MissedLines.Select(x => x.ItemId).Distinct().Count());
        var sql = RepairOrderStockAndReleaseReservations.RepairMissedLinesSql(RepairOrderStockAndReleaseReservations.MissedLines);
        Assert.All(RepairOrderStockAndReleaseReservations.MissedLines, line => Assert.Contains(line.LineId, sql, StringComparison.Ordinal));
        Assert.DoesNotContain("TenantId\" = '", RepairOrderStockAndReleaseReservations.ReleaseReservationsSql(), StringComparison.Ordinal);
    }
}
