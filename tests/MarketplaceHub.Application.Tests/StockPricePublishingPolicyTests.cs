using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class StockPricePublishingPolicyTests
{
    private static readonly Guid OrderConnection = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherConnection = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid UnrelatedConnection = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Variant = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherVariant = Guid.Parse("20000000-0000-0000-0000-000000000002");

    // K1: a stock change reaches every connection selling the variant, at the latest projection version.
    [Fact]
    public void StockChange_FansOutToEveryConnectionWithAnActiveOffer()
    {
        var targets = StockProjectionOutboxPolicy.FanOut(
            [new StockProjectionChange(Variant, 4, "order:a"), new StockProjectionChange(Variant, 5, "order:b")],
            [(OrderConnection, Variant), (OtherConnection, Variant), (UnrelatedConnection, OtherVariant), (OtherConnection, Variant)]);

        Assert.Equal(2, targets.Count);
        Assert.Equal([OrderConnection, OtherConnection], targets.Select(target => target.ConnectionId));
        Assert.All(targets, target => Assert.Equal("order:b", target.SourceEventId));
        Assert.Equal(StockProjectionOutboxPolicy.DedupKey(OtherConnection, Variant, 5), targets[1].DedupKey);
    }

    [Fact]
    public void StockChange_WithoutActiveOffers_QueuesNothing()
    {
        Assert.Empty(StockProjectionOutboxPolicy.FanOut([new StockProjectionChange(Variant, 2, "manual:1")], [(OtherConnection, OtherVariant)]));
    }

    // K2: these outcomes finish the dispatch instead of blocking it (and stopping the reconcile scan).
    [Theory]
    [InlineData("NO_EXTERNAL_CHANGES", true)]
    [InlineData("LIVE_OFFER_REQUIRED", true)]
    [InlineData("CHANNEL_OFFER_INVALID", true)]
    [InlineData("HEPSIBURADA_LISTING_LINK_REQUIRED", false)]
    [InlineData("INVENTORY_REQUIRED", false)]
    public void StockDispatch_SettledBuildFailures(string code, bool settled)
    {
        Assert.Equal(settled, StockProjectionOutboxPolicy.IsSettledBuildFailure(code));
    }

    // K3: Trendyol's price request carries stock too, so an automatic send needs both policies.
    [Fact]
    public void AutomaticPriceInventorySend_RequiresPriceAndStockPolicies()
    {
        Assert.Equal([MarketplaceExternalWritePolicies.Price, MarketplaceExternalWritePolicies.Stock], PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: false));
        Assert.Empty(PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: true));
    }

    // K4: with the price policy off, a stock dispatch is held and reported instead of failing later.
    [Fact]
    public void StockDispatch_WithPricePolicyOff_IsHeldNotSent()
    {
        Assert.Equal(StockProjectionDispatchDecision.Skip, StockProjectionOutboxPolicy.Decide(stockWriteEnabled: false, priceWriteEnabled: true));
        Assert.Equal(StockProjectionDispatchDecision.HoldForPricePolicy, StockProjectionOutboxPolicy.Decide(stockWriteEnabled: true, priceWriteEnabled: false));
        Assert.Equal(StockProjectionDispatchDecision.Dispatch, StockProjectionOutboxPolicy.Decide(stockWriteEnabled: true, priceWriteEnabled: true));
    }

    // K5: an offer stored without VAT is sent grossed up with its own VAT rate.
    [Fact]
    public void VatExcludedOffer_IsGrossedUpWhenSent()
    {
        Assert.Equal(120.00m, ChannelOfferPricePolicy.Outbound(100m, 20m, "EXCLUDED", "HALF_EVEN"));
        Assert.Equal(100.00m, ChannelOfferPricePolicy.Outbound(100m, 20m, "INCLUDED", "HALF_EVEN"));
        Assert.Equal(110.00m, ChannelOfferPricePolicy.Outbound(100m, 10m, "excluded", null));

        var offer = Offer(listPrice: 150m, salePrice: 100m, vatInclusion: "EXCLUDED", vatRate: 20m);
        Assert.Equal(120.00m, PriceInventoryComposer.OutboundSalePrice(offer));
        Assert.Equal(180.00m, PriceInventoryComposer.OutboundListPrice(offer));
    }

    [Theory]
    [InlineData("HALF_EVEN", 10.12)]
    [InlineData("HALF_UP", 10.13)]
    [InlineData("DOWN", 10.12)]
    [InlineData("CEILING", 10.13)]
    public void OutboundPrice_UsesTheOfferRoundingMode(string roundingMode, double expected)
    {
        Assert.Equal((decimal)expected, ChannelOfferPricePolicy.Outbound(10.125m, 0m, "INCLUDED", roundingMode));
    }

    [Fact]
    public void OfferPricingModes_AreValidated()
    {
        Assert.True(ChannelOfferPricePolicy.IsKnownVatInclusion("EXCLUDED"));
        Assert.False(ChannelOfferPricePolicy.IsKnownVatInclusion("NET"));
        Assert.True(ChannelOfferPricePolicy.IsKnownRoundingMode("half_up"));
        Assert.False(ChannelOfferPricePolicy.IsKnownRoundingMode("BANKERS"));
    }

    // K6: the order shortfall is reserved only where the connection policy reserves the status.
    [Fact]
    public void OrderShortfall_IsReservedOnlyWhenThePolicyReservesTheStatus()
    {
        var policy = new ConnectionInventoryPolicy
        {
            AuthorityMode = InventoryAuthorityPolicy.Central,
            ReservationMode = "ORDER_LINE",
            ReserveOnStatuses = "Created",
            ReleaseOnStatuses = "Cancelled"
        };

        Assert.Equal(2m, OrderInventoryReservationPolicy.ReservedQuantity(2m, policy, "Created"));
        Assert.Equal(0m, OrderInventoryReservationPolicy.ReservedQuantity(2m, policy, "Cancelled"));
        Assert.Equal(0m, OrderInventoryReservationPolicy.ReservedQuantity(2m, null, "Created"));
        Assert.Equal(0m, OrderInventoryReservationPolicy.ReservedQuantity(0m, policy, "Created"));
        var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        Assert.Equal(at.AddDays(14), OrderInventoryReservationPolicy.ExpiresAt(at));
    }

    // K7: outbound prices have two decimals and the sent-price marker is taken over them; a price that
    // already had two decimals keeps the marker it had, so no offer is resent because of the change.
    [Fact]
    public void OutboundPrice_HasTwoDecimalsAndKeepsTheExistingMarkerForTwoDecimalPrices()
    {
        var fourDecimals = Offer(listPrice: 120.4567m, salePrice: 99.9951m);
        Assert.Equal(100.00m, PriceInventoryComposer.OutboundSalePrice(fourDecimals));
        Assert.Equal(120.46m, PriceInventoryComposer.OutboundListPrice(fourDecimals));
        Assert.Equal(PriceInventoryComposer.StandardPriceHash(120.46m, 100.00m, 3), PriceInventoryComposer.StandardPriceHash(fourDecimals));

        var twoDecimals = Offer(listPrice: 120.5000m, salePrice: 100.0000m);
        Assert.Equal(PriceInventoryComposer.StandardPriceHash(120.5m, 100m, 3), PriceInventoryComposer.StandardPriceHash(twoDecimals));
    }

    // K9: a marketplace price stored on an offer kept without VAT grosses up to the same price, so the
    // marker written at import matches what the next send computes and the price is not pushed back.
    [Theory]
    [InlineData("HALF_EVEN")]
    [InlineData("DOWN")]
    [InlineData("CEILING")]
    public void ImportedGrossPrice_RoundTripsThroughAVatExcludedOffer(string roundingMode)
    {
        var sale = ChannelOfferPricePolicy.StoredFromGross(119.99m, 20m, "EXCLUDED", roundingMode);
        var list = ChannelOfferPricePolicy.StoredFromGross(149.90m, 20m, "EXCLUDED", roundingMode);
        var offer = Offer(listPrice: list, salePrice: sale, vatInclusion: "EXCLUDED", vatRate: 20m, roundingMode: roundingMode);

        Assert.Equal(119.99m, PriceInventoryComposer.OutboundSalePrice(offer));
        Assert.Equal(149.90m, PriceInventoryComposer.OutboundListPrice(offer));
        Assert.Equal(PriceInventoryComposer.StandardPriceHash(149.90m, 119.99m, offer.PriceVersion), PriceInventoryComposer.StandardPriceHash(offer));
        Assert.Equal(119.99m, ChannelOfferPricePolicy.StoredFromGross(119.99m, 20m, "INCLUDED", roundingMode));
    }

    private static ChannelOffer Offer(decimal listPrice, decimal salePrice, string vatInclusion = "INCLUDED", decimal vatRate = 20m, string roundingMode = "HALF_EVEN") => new()
    {
        Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
        ConnectionId = OrderConnection,
        VariantId = Variant,
        ListPrice = listPrice,
        SalePrice = salePrice,
        Currency = "TRY",
        VatRate = vatRate,
        VatInclusion = vatInclusion,
        RoundingMode = roundingMode,
        Status = "ACTIVE",
        PriceVersion = 3
    };
}
