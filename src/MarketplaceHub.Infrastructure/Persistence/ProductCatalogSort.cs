namespace MarketplaceHub.Infrastructure.Persistence;

// A product card's place in a price or stock sorted catalog: the card's shown price (lowest) or stock (total), then
// the default catalog order (newest first) so equal values keep one stable order across pages. A card with no price
// or no stock record sorts last in both directions: unknown is not zero.
internal readonly record struct ProductCatalogSortKey(decimal? Metric, DateTimeOffset CreatedAt, Guid Id);

internal sealed class ProductCatalogSort : IComparer<ProductCatalogSortKey>
{
    public const string PriceAscending = "price-asc";
    public const string PriceDescending = "price-desc";
    public const string StockAscending = "stock-asc";
    public const string StockDescending = "stock-desc";

    private ProductCatalogSort(string value)
    {
        Value = value;
        ByPrice = value is PriceAscending or PriceDescending;
        Descending = value is PriceDescending or StockDescending;
    }

    public string Value { get; }
    public bool ByPrice { get; }
    public bool Descending { get; }

    // Null for the default order (or a value the list does not know).
    public static ProductCatalogSort? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        PriceAscending => new(PriceAscending),
        PriceDescending => new(PriceDescending),
        StockAscending => new(StockAscending),
        StockDescending => new(StockDescending),
        _ => null
    };

    public int Compare(ProductCatalogSortKey left, ProductCatalogSortKey right)
    {
        if (left.Metric != right.Metric)
        {
            if (left.Metric is null) return 1;
            if (right.Metric is null) return -1;
            var byMetric = left.Metric.Value.CompareTo(right.Metric.Value);
            return Descending ? -byMetric : byMetric;
        }
        var byCreated = right.CreatedAt.CompareTo(left.CreatedAt);
        return byCreated != 0 ? byCreated : right.Id.CompareTo(left.Id);
    }
}
