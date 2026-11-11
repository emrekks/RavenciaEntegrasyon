using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The pictures a variant is published with.
//
// Assignment rule (owner, 11.11.2026: "neye atarsa onda duracak, renk beden varyasyon fark etmeyecek"; "burdan atama
// yapıyorum onlar yazsın varyantta görsel olarak"): the editor's two assignment places (Seçeneklere Görsel Ata by colour
// or size, and each row's picture icon) write a variant's own pictures, and a variant with an assignment goes with
// exactly that list, in the panel's order, the size chart last; the row icon's count is that list. A variant without
// an assignment goes with its card's gallery minus the pictures assigned only to other colours of the model (an
// imported card keeps its gallery on its first size, so its other sizes still go with it; the other colours' pictures
// copied onto a gallery, live MZ051BYC Siyah, no longer go); failing that, as before. The editor shows that list on the
// row icon too.
//
// Pictures go out in the panel's order, the one in front first (owner, 6.11.2026). The panel's order is the card's
// picture strip: its product-level rows by SortOrder. A variant's own assignment says which pictures it has, not their
// order: assigning pictures renumbers that variant's rows 0, 1, 2… (live RYP006-18 put the size chart first while
// -16 and -17 kept it last, so one Trendyol content held two image lists). Own pictures therefore follow the strip;
// one the strip lacks goes after, in the variant's own order. Callers pass the media ordered by SortOrder.
internal static class PublicationVariantMedia
{
    // sizeChartAssetId: the card's size chart, always the last picture of every variant (added when the variant's own
    // pictures lack it). colours: the model's assignments (PublicationColours); without it the rule before the
    // assignment rule.
    public static List<T> For<T>(IReadOnlyCollection<T> media, Guid variantId, Func<T, Guid?> variantOf, Func<T, Guid> assetOf, Guid? sizeChartAssetId = null, PublicationColours? colours = null)
    {
        var pictures = colours is null ? Pictures(media, variantId, variantOf, assetOf) : ColourPictures(media, variantId, variantOf, assetOf, colours);
        return sizeChartAssetId is Guid chartId ? SizeCharts.SizeChartPlacement.Last(pictures, media, item => assetOf(item) == chartId) : pictures;
    }

    // The pictures a card goes out with as a whole (PTT AVM's one product per model, the product list's front
    // picture): each of its variants' pictures (For) in variant order, once each, the size chart last.
    public static List<T> ForCard<T>(IReadOnlyCollection<T> media, IEnumerable<Guid> variantIds, Func<T, Guid?> variantOf, Func<T, Guid> assetOf, Guid? sizeChartAssetId, PublicationColours colours)
    {
        var pictures = variantIds.SelectMany(variantId => For(media, variantId, variantOf, assetOf, sizeChartAssetId, colours)).DistinctBy(assetOf).ToList();
        return pictures.Count > 0 && sizeChartAssetId is Guid chartId ? SizeCharts.SizeChartPlacement.Last(pictures, media, item => assetOf(item) == chartId) : pictures;
    }

    private static List<T> ColourPictures<T>(IReadOnlyCollection<T> media, Guid variantId, Func<T, Guid?> variantOf, Func<T, Guid> assetOf, PublicationColours colours)
    {
        var general = media.Where(item => variantOf(item) is null).ToList();
        var seen = new HashSet<Guid>();
        var own = media.Where(item => variantOf(item) == variantId && seen.Add(assetOf(item))).ToList();
        if (own.Count > 0) return InPanelOrder(own, general, assetOf);
        var fromGallery = general.Where(item => !colours.AssignedOnlyToOtherColours(variantId, assetOf(item))).ToList();
        return fromGallery.Count > 0 ? fromGallery : Pictures(media, variantId, variantOf, assetOf);
    }

    // The rule before the assignment rule: its own assigned pictures; else the card's general gallery; else, when the card
    // keeps every picture on its other variants (imported cards do: the gallery sits on the first sizes), those
    // variants' pictures, once each in their order (live 2026-11-05: MZ021Y-CD-01 joined the Bordo card without
    // pictures of its own and Trendyol refused it).
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

// A model's picture assignments: each card variant's colour (its Renk) and which pictures the
// model's variants (every colour card) have assigned to which colour.
internal sealed class PublicationColours(IReadOnlyDictionary<Guid, string> colourByVariant, IReadOnlyCollection<(Guid AssetId, string Colour)> assignments)
{
    private readonly Dictionary<Guid, HashSet<string>> coloursByAsset = assignments
        .GroupBy(item => item.AssetId)
        .ToDictionary(group => group.Key, group => group.Select(item => item.Colour).ToHashSet(StringComparer.Ordinal));

    public static string Key(string? optionSignature) =>
        ProductFamilyColorOrder.ColorOf(optionSignature)?.Trim().ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR")) ?? "";

    private string ColourOf(Guid variantId) => colourByVariant.GetValueOrDefault(variantId) ?? "";

    public bool SameColour(Guid variantId, Guid otherVariantId) => string.Equals(ColourOf(variantId), ColourOf(otherVariantId), StringComparison.Ordinal);

    // A picture assigned to sizes of other colours of the model and to none of this variant's colour.
    public bool AssignedOnlyToOtherColours(Guid variantId, Guid assetId) =>
        coloursByAsset.TryGetValue(assetId, out var owners) && !owners.Contains(ColourOf(variantId));

    // The card's variants and every variant-level picture of the model (same brand and model code), with their colours.
    public static async Task<PublicationColours> LoadAsync(AppDbContext db, Guid tenantId, Product product, IReadOnlyCollection<ProductVariant> cardVariants, CancellationToken cancellationToken)
    {
        var modelCode = cardVariants.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => x.ModelCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var familyIds = await ProductFamilyScope.ProductIdsAsync(db, tenantId, product.BrandId, modelCode, db.ProductVariants.AsNoTracking(), cancellationToken);
        if (!familyIds.Contains(product.Id)) familyIds.Add(product.Id);
        var rows = await (from media in db.ProductMedia.AsNoTracking()
                          join variant in db.ProductVariants.AsNoTracking() on media.VariantId equals (Guid?)variant.Id
                          where media.TenantId == tenantId && variant.TenantId == tenantId && media.Status == "ACTIVE" && familyIds.Contains(variant.ProductId)
                          select new { media.FileAssetId, variant.OptionSignature }).ToListAsync(cancellationToken);
        return new(cardVariants.ToDictionary(x => x.Id, x => Key(x.OptionSignature)), [.. rows.Select(row => (row.FileAssetId, Key(row.OptionSignature)))]);
    }

    // LoadAsync for several cards at once (the product list, a PTT AVM model family): the same families and
    // assignments, read with two queries instead of two per card. Keyed by the card's id.
    public static async Task<Dictionary<Guid, PublicationColours>> LoadManyAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<(Product Product, IReadOnlyCollection<ProductVariant> CardVariants)> cards, CancellationToken cancellationToken)
    {
        if (cards.Count == 0) return [];
        var coded = cards.Where(card => card.CardVariants.Any(x => ProductFamilyKeyPolicy.ModelCodeKey(x.ModelCode) is not null)).ToList();
        // Every coded variant of the cards' brands (ProductFamilyScope's rows), the family key compared in memory.
        var brandIds = coded.Where(card => card.Product.BrandId is not null).Select(card => card.Product.BrandId!.Value).Distinct().ToArray();
        var anyWithoutBrand = coded.Any(card => card.Product.BrandId is null);
        var familyRows = coded.Count == 0 ? [] : await (from variant in db.ProductVariants.AsNoTracking()
                                                        join product in db.Products.AsNoTracking() on variant.ProductId equals product.Id
                                                        where variant.TenantId == tenantId && product.TenantId == tenantId && variant.ModelCode != null
                                                              && ((product.BrandId != null && brandIds.Contains(product.BrandId.Value)) || (anyWithoutBrand && product.BrandId == null))
                                                        select new { variant.ProductId, variant.ModelCode, product.BrandId }).Distinct().ToListAsync(cancellationToken);
        var familyByCard = Families(cards, [.. familyRows.Select(row => (row.ProductId, row.BrandId, (string?)row.ModelCode))]);
        var allIds = familyByCard.Values.SelectMany(ids => ids).Distinct().ToArray();
        var rows = await (from media in db.ProductMedia.AsNoTracking()
                          join variant in db.ProductVariants.AsNoTracking() on media.VariantId equals (Guid?)variant.Id
                          where media.TenantId == tenantId && variant.TenantId == tenantId && media.Status == "ACTIVE" && allIds.Contains(variant.ProductId)
                          select new { variant.ProductId, media.FileAssetId, variant.OptionSignature }).ToListAsync(cancellationToken);
        return ForCards(cards, familyByCard, [.. rows.Select(row => (row.ProductId, row.FileAssetId, row.OptionSignature))]);
    }

    // Each card's model family (ProductFamilyScope's rule over the coded variants' rows): the products whose variants
    // share its brand and model-code key, the card itself always in.
    internal static Dictionary<Guid, HashSet<Guid>> Families(IReadOnlyCollection<(Product Product, IReadOnlyCollection<ProductVariant> CardVariants)> cards, IReadOnlyCollection<(Guid ProductId, Guid? BrandId, string? ModelCode)> codedVariants)
    {
        var productsByFamily = codedVariants
            .Where(row => ProductFamilyKeyPolicy.ModelCodeKey(row.ModelCode) is not null)
            .GroupBy(row => ProductFamilyKeyPolicy.FamilyKey(row.ProductId, row.BrandId, row.ModelCode), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.ProductId).ToHashSet(), StringComparer.Ordinal);
        return cards.ToDictionary(card => card.Product.Id, card =>
        {
            var modelCode = card.CardVariants.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => x.ModelCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var key = ProductFamilyKeyPolicy.FamilyKey(card.Product.Id, card.Product.BrandId, modelCode);
            var ids = productsByFamily.TryGetValue(key, out var members) ? new HashSet<Guid>(members) : [];
            ids.Add(card.Product.Id);
            return ids;
        });
    }

    // Each card's assignments: its variants' colours and the variant-level pictures (asset, variant's option signature)
    // of every product of its family.
    internal static Dictionary<Guid, PublicationColours> ForCards(IReadOnlyCollection<(Product Product, IReadOnlyCollection<ProductVariant> CardVariants)> cards, IReadOnlyDictionary<Guid, HashSet<Guid>> familyByCard, IReadOnlyCollection<(Guid ProductId, Guid AssetId, string? OptionSignature)> variantPictures)
    {
        var assignmentsByProduct = variantPictures.GroupBy(row => row.ProductId).ToDictionary(group => group.Key, group => group.Select(row => (row.AssetId, Key(row.OptionSignature))).ToList());
        return cards.ToDictionary(card => card.Product.Id, card => new PublicationColours(
            card.CardVariants.ToDictionary(x => x.Id, x => Key(x.OptionSignature)),
            [.. familyByCard[card.Product.Id].SelectMany(id => assignmentsByProduct.GetValueOrDefault(id) ?? [])]));
    }
}
