using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// The edit page's image strip shows the same model's other colours. Its default list is each card's product-level
// pictures (else the card's first variant picture), but a custom family order lists every active picture of the family,
// variant pictures included. Every picture the strip can show therefore gets an item carrying all of its media rows,
// so "Tümünü Temizle" and a tile's remove button take the whole picture away in one go instead of one layer per save.
internal static class CatalogFamilyMedia
{
    internal sealed record Row(Guid MediaId, Guid ProductId, Guid? VariantId, int SortOrder, string? Url);

    internal static (IReadOnlyList<string> DefaultUrls, IReadOnlyList<ProductFamilyMediaView> Items) Build(
        IReadOnlyList<Row> rows, IReadOnlyDictionary<Guid, string> productTitles)
    {
        var shown = rows.Where(row => !string.IsNullOrWhiteSpace(row.Url)).ToList();
        var defaultRows = shown
            .GroupBy(row => row.ProductId)
            .SelectMany(group =>
            {
                var productMedia = group.Where(row => row.VariantId is null).OrderBy(row => row.SortOrder).ToList();
                return productMedia.Count > 0 ? productMedia : group.OrderBy(row => row.SortOrder).Take(1).ToList();
            })
            .OrderBy(row => row.SortOrder)
            .ToList();
        var defaultUrls = CatalogImageIdentity.DistinctDisplayUrls(defaultRows.Select(row => row.Url));
        var rowsByKey = shown.GroupBy(row => CatalogImageIdentity.Key(row.Url!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        // The default pictures first, in their order; then the rest of the family in its saved order.
        var items = defaultRows.Concat(shown)
            .Select(row => CatalogImageIdentity.Key(row.Url!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                var group = rowsByKey[key];
                return new ProductFamilyMediaView(
                    defaultRows.FirstOrDefault(row => string.Equals(CatalogImageIdentity.Key(row.Url!), key, StringComparison.OrdinalIgnoreCase))?.Url ?? group[0].Url!,
                    group.Select(row => row.MediaId).Distinct().ToList(),
                    group.Select(row => productTitles.GetValueOrDefault(row.ProductId, "Ürün")).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            })
            .ToList();
        return (defaultUrls, items);
    }
}
