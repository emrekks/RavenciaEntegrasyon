using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner rule of 18 Nov 2026: a linked variant without an offer goes at the panel price; Özel Fiyat keeps its own.
public sealed class PanelPriceOfferPolicyTests
{
    [Theory]
    [InlineData("TRENDYOL", true)]
    [InlineData("hepsiburada", true)]
    [InlineData("SHOPIFY", true)]
    // Their stock request carries the price: an opened offer's first stock send would carry a price never confirmed.
    [InlineData("N11", false)]
    [InlineData("PAZARAMA", false)]
    [InlineData("PTTAVM", false)]
    [InlineData(null, false)]
    public void OnlyPlatformsWhoseStockGoesAlone_OpenOffers(string? platformCode, bool opens) =>
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
    // A choice never stored follows a panel price that was saved before; the first panel price saved reprices nothing.
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void WhoFollowsAPanelPriceChange(bool? followsPanelPrice, bool panelPriceWasSaved, bool follows) =>
        Assert.Equal(follows, PanelPriceOfferPolicy.FollowsOnPanelChange(followsPanelPrice, panelPriceWasSaved));
}
