namespace MarketplaceHub.Application;

public static class ConnectionDataResetPolicy
{
    private static readonly IReadOnlySet<string> Marketplace = new HashSet<string>(StringComparer.Ordinal)
    {
        "ORDERS", "RETURNS", "INVOICES", "PRODUCTS", "CATEGORIES", "CATEGORY_ATTRIBUTES", "BRANDS"
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
        "TRENDYOL" => Marketplace,
        "HEPSIBURADA" => Hepsiburada,
        "SHOPIFY" => Shopify,
        "TRENDYOL_EFATURAM" => EInvoice,
        _ => None
    };
}
