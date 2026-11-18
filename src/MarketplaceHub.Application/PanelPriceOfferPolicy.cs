namespace MarketplaceHub.Application;

// Owner rule (18 Nov 2026): "Özel Fiyat açık değilse paneli kullanacak". The rules of the panel price an offer follows.
public static class PanelPriceOfferPolicy
{
    // The platforms whose missing offers are opened at the panel price: every marketplace. Since the owner's decision of
    // 18 Nov 2026 every stock send carries the price in force on every marketplace (InForce), so the price an opened
    // offer's first stock send carries is the panel's by rule, on n11, Pazarama and PTT AVM as everywhere else.
    public static bool OpensOffers(string? platformCode) =>
        (platformCode ?? "").Trim().ToUpperInvariant() is "TRENDYOL" or "HEPSIBURADA" or "SHOPIFY" or "N11" or "PAZARAMA" or "PTTAVM";

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

    // Owner rule of 18 Nov 2026 ("stokla beraber fiyatı da gönderelim"): the price a send carries for an offer. Özel Fiyat
    // on (false): the offer's own price. Otherwise the saved panel price (the variant's, else its card's); without a saved
    // panel price, the offer's stored price (the value read from the marketplace), never a made-up one. Null: the price in
    // force cannot be sent (a saved panel price with a list price below the sale price, or an offer price of 0); the
    // caller sends the stock alone where the marketplace takes it, or refuses the line.
    public sealed record PriceInForce(decimal ListPrice, decimal SalePrice, bool FromPanel);

    public static PriceInForce? InForce(string? platformCode, bool? followsPanelPrice, decimal offerListPrice, decimal offerSalePrice, decimal? variantSalePrice, decimal? productSalePrice, decimal? variantListPrice, decimal? productListPrice)
    {
        var panelSaved = variantSalePrice is > 0 || productSalePrice is > 0;
        if (followsPanelPrice != false && panelSaved)
            return PanelPrices(platformCode, variantSalePrice, productSalePrice, variantListPrice, productListPrice) is { } panel ? new(panel.ListPrice, panel.SalePrice, true) : null;
        if (offerSalePrice <= 0 || offerListPrice < offerSalePrice) return null;
        return new(offerListPrice, offerSalePrice, false);
    }

    public const string PriceNotSentNote = "Fiyat gönderilmedi: geçerli panel fiyatı yok (boş, 0 ya da liste fiyatı satış fiyatından küçük); yalnız stok gitti.";

    // Whether an offer follows the panel price when the panel price changes. "Özel Fiyat" on (false) keeps its own price.
    // A choice never stored (null) follows only a panel price that was already saved: before that the panel had no price
    // of its own (the screens show the first offer's), so the first panel price saved never reprices the offers at once;
    // "Fiyatları Panel Fiyatına Eşitle" aligns them on purpose, after a preview.
    public static bool FollowsOnPanelChange(bool? followsPanelPrice, bool panelPriceWasSaved) =>
        followsPanelPrice ?? panelPriceWasSaved;
}
