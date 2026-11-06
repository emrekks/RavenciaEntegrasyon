namespace MarketplaceHub.Infrastructure.Persistence;

// The pictures a variant is published with: its own assigned pictures; else the card's general gallery; else, when the
// card keeps every picture on its other variants (imported cards do: the gallery sits on the first sizes), those
// variants' pictures, once each in their order. A card is one colour, so its other sizes show the same garment
// (live 2026-11-05: MZ021Y-CD-01 joined the Bordo card without pictures of its own and Trendyol refused it).
//
// Pictures go out in the panel's order, the one in front first (owner, 6.11.2026). The panel's order is the card's
// picture strip: its product-level rows by SortOrder. A variant's own assignment says which pictures it has, not their
// order: assigning pictures renumbers that variant's rows 0, 1, 2… (live RYP006-18 put the size chart first while
// -16 and -17 kept it last, so one Trendyol content held two image lists). Own pictures therefore follow the strip;
// one the strip lacks goes after, in the variant's own order. Callers pass the media ordered by SortOrder.
internal static class PublicationVariantMedia
{
    // sizeChartAssetId: the card's size chart, always the last picture of every variant (added when the variant's own
    // pictures lack it).
    public static List<T> For<T>(IReadOnlyCollection<T> media, Guid variantId, Func<T, Guid?> variantOf, Func<T, Guid> assetOf, Guid? sizeChartAssetId = null)
    {
        var pictures = Pictures(media, variantId, variantOf, assetOf);
        return sizeChartAssetId is Guid chartId ? SizeCharts.SizeChartPlacement.Last(pictures, media, item => assetOf(item) == chartId) : pictures;
    }

    private static List<T> Pictures<T>(IReadOnlyCollection<T> media, Guid variantId, Func<T, Guid?> variantOf, Func<T, Guid> assetOf)
    {
        var general = media.Where(item => variantOf(item) is null).ToList();
        var own = media.Where(item => variantOf(item) == variantId).ToList();
        if (own.Count > 0) return InPanelOrder(own, general, assetOf);
        if (general.Count > 0) return general;
        var seen = new HashSet<Guid>();
        return media.Where(item => variantOf(item) is not null && seen.Add(assetOf(item))).ToList();
    }

    // The url the card's size chart goes out with (null when it has none or its url could not be built).
    public static string? SizeChartUrl(IEnumerable<(Guid Id, string? Url)> pictures, Guid? sizeChartAssetId) =>
        sizeChartAssetId is Guid chartId ? pictures.FirstOrDefault(item => item.Id == chartId).Url : null;

    // A variant's pictures in the order of the card's strip (given in its order); a picture the strip lacks keeps its
    // place after them. The sort is stable, so equal ranks stay as they came.
    public static List<T> InPanelOrder<T, TKey>(IEnumerable<T> pictures, IEnumerable<T> strip, Func<T, TKey> keyOf, IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        var rank = new Dictionary<TKey, int>(comparer);
        foreach (var item in strip) rank.TryAdd(keyOf(item), rank.Count);
        return pictures.OrderBy(item => rank.TryGetValue(keyOf(item), out var position) ? position : int.MaxValue).ToList();
    }
}
