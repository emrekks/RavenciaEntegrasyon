using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Shopify;

namespace MarketplaceHub.Infrastructure.Persistence;

public static class LocalReconciliationPolicy
{
    private static readonly HashSet<string> SupportedPlatforms = new(StringComparer.Ordinal)
    {
        "TRENDYOL",
        ShopifyContract.PlatformCode,
        HepsiburadaContract.PlatformCode
    };

    public static bool Supports(string platformCode) => SupportedPlatforms.Contains(platformCode);
}
