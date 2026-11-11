using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// The edit page's image strip shows the same model's other colours. Its default list is each card's pictures (its
// product-level rows, then those only its variants hold), card by card in the family's order, and a custom family order lists every active picture
// of the family, variant pictures included. Every picture the strip can show therefore gets an item carrying all of its
// media rows, so "Tümünü Temizle" and a tile's remove button take the whole picture away in one go instead of one layer
// per save.
internal static class CatalogFamilyMedia
{
    internal sealed record Row(Guid MediaId, Guid ProductId, Guid? VariantId, int SortOrder, string? Url);

    // productOrder: the family's cards in the strip's order (the edited card first); a card it lacks goes after them.
    internal static (IReadOnlyList<string> DefaultUrls, IReadOnlyList<ProductFamilyMediaView> Items) Build(
        IReadOnlyList<Row> rows, IReadOnlyDictionary<Guid, string> productTitles, IReadOnlyList<Guid>? productOrder = null)
    {
        var shown = rows.Where(row => !string.IsNullOrWhiteSpace(row.Url)).ToList();
        var productRank = Ranks(productOrder);
        // Card by card: every card numbers its own rows from 0, so ordering the family by SortOrder alone dealt the
        // colours out as every card's first picture, then every second.
        var defaultRows = shown
            .GroupBy(row => row.ProductId)
            .Select((group, index) => (Group: group, Index: index))
            .OrderBy(entry => productRank.TryGetValue(entry.Group.Key, out var rank) ? rank : int.MaxValue)
            .ThenBy(entry => entry.Group.Min(row => row.SortOrder))
            .ThenBy(entry => entry.Index)
            .SelectMany(entry =>
            {
                // Every picture of the card, its product-level rows first: a picture only its variants hold is in the
                // strip too, so the strip is the same whichever colour row opened the editor (owner, 11.11.2026).
                var productMedia = entry.Group.Where(row => row.VariantId is null).OrderBy(row => row.SortOrder).ThenBy(row => row.MediaId);
                var variantMedia = entry.Group.Where(row => row.VariantId is not null).OrderBy(row => row.SortOrder).ThenBy(row => row.VariantId).ThenBy(row => row.MediaId);
                return productMedia.Concat(variantMedia).ToList();
            })
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

    // Each card's own pictures as saved, in the family's card order: its gallery (product-level rows by SortOrder), the
    // pictures its variants hold, and its size chart. The editor writes a model's pictures colour by colour from these,
    // so the result does not depend on which colour row was opened.
    internal static IReadOnlyList<ProductFamilyGalleryView> Galleries(IReadOnlyList<Row> rows, IReadOnlyList<Guid> productOrder, IReadOnlyDictionary<Guid, string?> sizeChartUrls)
    {
        var shown = rows.Where(row => !string.IsNullOrWhiteSpace(row.Url)).ToList();
        return productOrder.Distinct().Select(productId =>
        {
            var own = shown.Where(row => row.ProductId == productId).ToList();
            var gallery = own.Where(row => row.VariantId is null).OrderBy(row => row.SortOrder).ThenBy(row => row.MediaId).Select(row => row.Url);
            var variantPictures = own.Where(row => row.VariantId is not null).OrderBy(row => row.SortOrder).ThenBy(row => row.VariantId).ThenBy(row => row.MediaId).Select(row => row.Url);
            return new ProductFamilyGalleryView(productId, CatalogImageIdentity.DistinctDisplayUrls(gallery), CatalogImageIdentity.DistinctDisplayUrls(variantPictures), sizeChartUrls.GetValueOrDefault(productId));
        }).ToList();
    }

    // The model's front picture (the products list's model row): the first picture of the family strip the editor opens
    // with (GetProductAsync: the custom family order when a card of the family has one, else Build's card-by-card list in
    // the family's card order), never a size chart. Null when the family has no other picture.
    internal static string? FrontUrl(IReadOnlyList<Row> rows, IReadOnlyList<Guid> cardOrder, bool customOrder, IEnumerable<string?> chartUrls)
    {
        var chartKeys = chartUrls.OfType<string>().Where(url => !string.IsNullOrWhiteSpace(url)).Select(CatalogImageIdentity.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var strip = customOrder ? OrderedUrls(rows) : Build(rows, new Dictionary<Guid, string>(), cardOrder).DefaultUrls;
        return strip.FirstOrDefault(url => !chartKeys.Contains(CatalogImageIdentity.Key(url)));
    }

    // The custom family order: StripUrls over the whole family's rows (a family reorder numbers every row by its rank).
    internal static IReadOnlyList<string> OrderedUrls(IReadOnlyList<Row> rows) => StripUrls(rows, customOrder: true);

    // A picture strip's order (a card's mediaUrls, the family's custom order): the product-level rows by SortOrder, the
    // order the owner arranged and saved, a new upload going after them. A picture stands where its product-level row
    // stands: assigning pictures to variants renumbers that variant's own rows 0, 1, 2… (its primary picture first), so
    // a variant row's number says nothing about the strip (owner 6.11.2026, RYP007: a size chart uploaded last and
    // assigned to every colour came back 4th, after the first colour's 3 pictures, because the card's list was built
    // colour by colour from the variant pictures). A picture only variants hold follows: by its saved number in a custom
    // order (a family reorder ranks every row), else variant by variant (variantOrder), each in its own order.
    internal static IReadOnlyList<string> StripUrls(IReadOnlyList<Row> rows, bool customOrder, IReadOnlyList<Guid>? variantOrder = null)
    {
        var shown = rows.Where(row => !string.IsNullOrWhiteSpace(row.Url)).ToList();
        var productLevel = shown.Where(row => row.VariantId is null).OrderBy(row => row.SortOrder).ThenBy(row => row.ProductId).ThenBy(row => row.MediaId);
        var variantRows = shown.Where(row => row.VariantId is not null);
        var variantRank = Ranks(variantOrder);
        var variantOnly = customOrder
            ? variantRows.OrderBy(row => row.SortOrder).ThenBy(row => row.ProductId).ThenBy(row => row.MediaId)
            : variantRows.OrderBy(row => variantRank.TryGetValue(row.VariantId!.Value, out var rank) ? rank : int.MaxValue)
                .ThenBy(row => row.VariantId).ThenBy(row => row.SortOrder).ThenBy(row => row.MediaId);
        return CatalogImageIdentity.DistinctDisplayUrls(productLevel.Concat(variantOnly).Select(row => row.Url));
    }

    private static Dictionary<Guid, int> Ranks(IReadOnlyList<Guid>? order)
    {
        var ranks = new Dictionary<Guid, int>();
        foreach (var id in order ?? []) ranks.TryAdd(id, ranks.Count);
        return ranks;
    }
}
