namespace MarketplaceHub.Application;

public static class CatalogPricePolicy
{
    public static decimal? EffectiveSalePrice(decimal? channelSalePrice, decimal? variantDefaultSalePrice, decimal? productDefaultSalePrice)
    {
        if (channelSalePrice is > 0) return channelSalePrice;
        if (variantDefaultSalePrice is > 0) return variantDefaultSalePrice;
        return productDefaultSalePrice is > 0 ? productDefaultSalePrice : null;
    }

    public static decimal? MinimumPositivePrice(IEnumerable<decimal?> prices)
    {
        var positivePrices = prices.Where(price => price is > 0).Select(price => price!.Value).ToList();
        return positivePrices.Count > 0 ? positivePrices.Min() : null;
    }
}
