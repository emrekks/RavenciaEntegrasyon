namespace MarketplaceHub.Application;

// Owner rule (18 Nov 2026): "Özel Fiyat açık değilse paneli kullanacak". The rules of the panel price an offer follows.
public static class PanelPriceOfferPolicy
{
    // The platforms whose missing offers are opened at the panel price. Their stock request can carry the stock alone
    // (Trendyol sends only what differs from what it confirmed; Hepsiburada and Shopify leave the price out while its
    // mark holds), so an opened offer sends no price until the panel price changes. n11, Pazarama and PTT AVM build every
    // line whole (stock with price): an opened offer's first stock send would carry a price the marketplace never had,
    // without the owner pricing it, so they get none (their offers come from an import or the product editor).
    public static bool OpensOffers(string? platformCode) =>
        (platformCode ?? "").Trim().ToUpperInvariant() is "TRENDYOL" or "HEPSIBURADA" or "SHOPIFY";

    // Platforms that take a sale price only (the web's SALE_PRICE_ONLY_PLATFORMS): their offers keep list = sale.
    public static bool TakesListPrice(string? platformCode) =>
        (platformCode ?? "").Trim().ToUpperInvariant() is not ("HEPSIBURADA" or "PTTAVM");

    // The panel price of a variant as CatalogPricePolicy reads it: the variant's own above zero, else its card's. The list
    // price is the variant's, else the card's, else the sale price; a list price below the sale price is no price to
    // send (null: unknown stays unknown). Rounded to two decimals as the panel stores prices.
    public static (decimal ListPrice, decimal SalePrice)? PanelPrices(string? platformCode, decimal? variantSalePrice, decimal? productSalePrice, decimal? variantListPrice, decimal? productListPrice)
    {
        if (CatalogPricePolicy.EffectiveSalePrice(null, variantSalePrice, productSalePrice) is not { } sale) return null;
        var list = variantListPrice is > 0 ? variantListPrice.Value : productListPrice is > 0 ? productListPrice.Value : sale;
        if (list < sale) return null;
        if (!TakesListPrice(platformCode)) list = sale;
        return (decimal.Round(list, 4, MidpointRounding.ToEven), decimal.Round(sale, 4, MidpointRounding.ToEven));
    }

    // Whether an offer follows the panel price when the panel price changes. "Özel Fiyat" on (false) keeps its own price.
    // A choice never stored (null) follows only a panel price that was already saved: before that the panel had no price
    // of its own (the screens show the first offer's), so the first panel price saved never reprices the offers at once;
    // "Fiyatları Panel Fiyatına Eşitle" aligns them on purpose, after a preview.
    public static bool FollowsOnPanelChange(bool? followsPanelPrice, bool panelPriceWasSaved) =>
        followsPanelPrice ?? panelPriceWasSaved;
}
