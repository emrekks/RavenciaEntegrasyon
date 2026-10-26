namespace MarketplaceHub.Application;

public static class ConnectionDataResetPolicy
{
    private static readonly IReadOnlySet<string> Marketplace = new HashSet<string>(StringComparer.Ordinal)
    {
        "ORDERS", "RETURNS", "INVOICES", "PRODUCTS", "CATEGORIES", "CATEGORY_ATTRIBUTES", "BRANDS"
    };

    // Trendyol also has the files uploaded on the Promotions page (CommissionTariffPlatforms).
    private static readonly IReadOnlySet<string> Trendyol = new HashSet<string>(Marketplace, StringComparer.Ordinal)
    {
        "COMMISSION_TARIFFS", "PROMOTION_OFFERS"
    };

    private static readonly IReadOnlySet<string> Hepsiburada = new HashSet<string>(StringComparer.Ordinal)
    {
        "ORDERS", "RETURNS", "INVOICES", "PRODUCTS", "CATEGORIES", "CATEGORY_ATTRIBUTES"
    };

    private static readonly IReadOnlySet<string> Shopify = new HashSet<string>(StringComparer.Ordinal)
    {
        "ORDERS", "RETURNS", "INVOICES", "PRODUCTS"
    };

    private static readonly IReadOnlySet<string> EInvoice = new HashSet<string>(StringComparer.Ordinal)
    {
        "INVOICES"
    };

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    public static IReadOnlySet<string> ScopesFor(string platformCode) => platformCode.Trim().ToUpperInvariant() switch
    {
        "TRENDYOL" => Trendyol,
        "PAZARAMA" or "PTTAVM" => Marketplace,
        "HEPSIBURADA" or "N11" => Hepsiburada,
        "SHOPIFY" => Shopify,
        "TRENDYOL_EFATURAM" => EInvoice,
        _ => None
    };
}
