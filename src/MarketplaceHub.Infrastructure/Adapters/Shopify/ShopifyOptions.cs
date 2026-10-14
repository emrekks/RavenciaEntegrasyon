namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed class ShopifyOptions
{
    public const string SectionName = "Shopify";
    public string ApiVersion { get; init; } = "2026-07";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);
    public int PageSize { get; init; } = 100;
    public int OrderPageSize { get; init; } = 100;
}
