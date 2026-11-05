namespace MarketplaceHub.Infrastructure.Persistence;

// The pictures a variant is published with: its own assigned pictures; else the card's general gallery; else, when the
// card keeps every picture on its other variants (imported cards do: the gallery sits on the first sizes), those
// variants' pictures, once each in their order. A card is one colour, so its other sizes show the same garment
// (live 2026-11-05: MZ021Y-CD-01 joined the Bordo card without pictures of its own and Trendyol refused it).
internal static class PublicationVariantMedia
{
    public static List<T> For<T>(IReadOnlyCollection<T> media, Guid variantId, Func<T, Guid?> variantOf, Func<T, Guid> assetOf)
    {
        var own = media.Where(item => variantOf(item) == variantId).ToList();
        if (own.Count > 0) return own;
        var general = media.Where(item => variantOf(item) is null).ToList();
        if (general.Count > 0) return general;
        var seen = new HashSet<Guid>();
        return media.Where(item => variantOf(item) is not null && seen.Add(assetOf(item))).ToList();
    }
}
