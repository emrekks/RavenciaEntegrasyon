using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class CatalogImportOrdering
{
    public static IEnumerable<IReadOnlyList<RemoteCatalogProduct>> GroupByModel(IEnumerable<RemoteCatalogProduct> source)
    {
        return source
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.ExternalProductId))
            // GroupBy keeps the first-seen model order while making every
            // snapshot belonging to that model contiguous. This avoids
            // interleaving variants from unrelated models during import.
            .GroupBy(ModelKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => (IReadOnlyList<RemoteCatalogProduct>)group.ToList());
    }

    internal static string ModelKey(RemoteCatalogProduct snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ProductMainId)
            && !string.Equals(snapshot.ProductMainId.Trim(), snapshot.ExternalProductId.Trim(), StringComparison.OrdinalIgnoreCase))
            return snapshot.ProductMainId.Trim();

        var variantIdentity = snapshot.Variants
            .Select(variant => variant.ModelCode ?? variant.Barcode)
            .FirstOrDefault(identity => !string.IsNullOrWhiteSpace(identity));
        return !string.IsNullOrWhiteSpace(variantIdentity)
            && !string.Equals(variantIdentity.Trim(), snapshot.ExternalProductId.Trim(), StringComparison.OrdinalIgnoreCase)
            ? variantIdentity.Trim()
            : snapshot.ExternalProductId.Trim();
    }
}
