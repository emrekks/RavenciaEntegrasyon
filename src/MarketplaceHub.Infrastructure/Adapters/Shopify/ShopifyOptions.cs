namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed class ShopifyOptions
{
    public const string SectionName = "Shopify";
    // The latest stable Admin API (released 1 October 2026). Nothing this adapter reads or writes changed between
    // 2026-07 and 2026-10; the connection card and new connections carry the same version.
    public string ApiVersion { get; init; } = "2026-10";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);
    public int PageSize { get; init; } = 100;
    public int OrderPageSize { get; init; } = 100;
}
