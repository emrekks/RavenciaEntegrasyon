namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// C10: Trendyol accepts at most 20,000 units of stock per barcode (price-inventory and product create). The clamped
// value is what goes into the request, so the request hash and the recorded quantity follow what Trendyol receives.
public static class TrendyolStockRules
{
    public const int MaxQuantity = 20_000;

    public static int Quantity(decimal publishable) => (int)Math.Clamp(decimal.Floor(Math.Max(0m, publishable)), 0m, MaxQuantity);
}
