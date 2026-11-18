using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner rule of 18 Nov 2026: a linked variant without an offer goes at the panel price; Özel Fiyat keeps its own.
public sealed class PanelPriceOfferPolicyTests
{
    [Theory]
    [InlineData("TRENDYOL", true)]
    [InlineData("hepsiburada", true)]
    [InlineData("SHOPIFY", true)]
    // Owner rule of 18 Nov 2026: every stock send carries the price in force on every marketplace, so all open them.
    [InlineData("N11", true)]
    [InlineData("PAZARAMA", true)]
    [InlineData("PTTAVM", true)]
    [InlineData("TRENDYOL_EFATURAM", false)]
    [InlineData(null, false)]
    public void EveryMarketplace_OpensOffers(string? platformCode, bool opens) =>
        Assert.Equal(opens, PanelPriceOfferPolicy.OpensOffers(platformCode));

    [Fact]
    public void ThePanelPrice_IsTheVariantsElseTheCards_AndUnknownStaysUnknown()
    {
        Assert.Equal((250m, 200m), PanelPriceOfferPolicy.PanelPrices("TRENDYOL", 200m, 180m, 250m, 300m));
        // The card's when the variant has none (or 0); the sale price is the list price when no list price is known.
        Assert.Equal((300m, 180m), PanelPriceOfferPolicy.PanelPrices("TRENDYOL", 0m, 180m, null, 300m));
        Assert.Equal((180m, 180m), PanelPriceOfferPolicy.PanelPrices("N11", null, 180m, null, null));
        // No sale price above zero, or a list price below it: nothing to send.
        Assert.Null(PanelPriceOfferPolicy.PanelPrices("TRENDYOL", null, null, 250m, null));
        Assert.Null(PanelPriceOfferPolicy.PanelPrices("TRENDYOL", 0m, 0m, 0m, 0m));
        Assert.Null(PanelPriceOfferPolicy.PanelPrices("TRENDYOL", 200m, null, 150m, null));
        // Sale-price-only marketplaces keep list = sale.
        Assert.Equal((200m, 200m), PanelPriceOfferPolicy.PanelPrices("HEPSIBURADA", 200m, null, 250m, null));
        Assert.Equal((200m, 200m), PanelPriceOfferPolicy.PanelPrices("PTTAVM", 200m, null, 250m, null));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    // Özel Fiyat on keeps its own price.
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    // One rule (owner decision of 18 Nov 2026): a choice never stored follows the panel price, also the first one saved.
    [InlineData(null, true, true)]
    [InlineData(null, false, true)]
    public void WhoFollowsAPanelPriceChange(bool? followsPanelPrice, bool panelPriceWasSaved, bool follows) =>
        Assert.Equal(follows, PanelPriceOfferPolicy.FollowsOnPanelChange(followsPanelPrice, panelPriceWasSaved));

    [Fact]
    public void AnImportedVariantsPanelPrice_IsTheMarketplacePrice_AndNoneWithoutOne()
    {
        Assert.Equal((150m, 120m), PanelPriceOfferPolicy.ImportedPanelPrices(120m, 150m));
        // A list price missing or below the sale price is the sale price.
        Assert.Equal((120m, 120m), PanelPriceOfferPolicy.ImportedPanelPrices(120m, null));
        Assert.Equal((120m, 120m), PanelPriceOfferPolicy.ImportedPanelPrices(120m, 100m));
        Assert.Equal((99.99m, 99.99m), PanelPriceOfferPolicy.ImportedPanelPrices(99.994m, null));
        // No sale price above zero: nothing is written.
        Assert.Null(PanelPriceOfferPolicy.ImportedPanelPrices(0m, 150m));
        Assert.Null(PanelPriceOfferPolicy.ImportedPanelPrices(null, null));
    }

    [Fact]
    public void ThePriceInForce_IsOwnWithOzelFiyat_PanelWhenSaved_ElseTheStoredPrice()
    {
        Assert.Equal(new PanelPriceOfferPolicy.PriceInForce(330m, 300m, false), PanelPriceOfferPolicy.InForce("TRENDYOL", false, 330m, 300m, 250m, null, 280m, null));
        Assert.Equal(new PanelPriceOfferPolicy.PriceInForce(280m, 250m, true), PanelPriceOfferPolicy.InForce("TRENDYOL", null, 330m, 300m, 250m, null, 280m, null));
        Assert.Equal(new PanelPriceOfferPolicy.PriceInForce(330m, 300m, false), PanelPriceOfferPolicy.InForce("TRENDYOL", null, 330m, 300m, null, null, null, null));
        // A saved panel price that cannot be sent, or an offer price of 0 without one: no price in force.
        Assert.Null(PanelPriceOfferPolicy.InForce("TRENDYOL", true, 330m, 300m, 250m, null, 200m, null));
        Assert.Null(PanelPriceOfferPolicy.InForce("TRENDYOL", null, 0m, 0m, null, null, null, null));
    }
}
