using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class CatalogService
{
    // The products list's model row (owner, 12.11.2026: "51 de 1. görsel yeşil ama kahve duruyor?"): a model is one card
    // with one gallery, so its picture is the first picture of the family gallery as the editor shows it
    // (CatalogFamilyMedia.FrontUrl), not the newest colour card's own. The whole visible family is read, also when a
    // filter (search, Düşük Stok) leaves only some of its cards on the page; each card keeps its own PrimaryImageUrl.
    private async Task<IReadOnlyList<ProductView>> WithFamilyImagesAsync(Guid tenantId, IReadOnlyList<ProductView> views, CancellationToken cancellationToken)
    {
        if (views.Count == 0) return views;
        var keyed = views.Where(view => ProductFamilyKeyPolicy.ModelCodeKey(view.ModelCode) is not null).ToList();
        var brandIds = keyed.Select(view => view.BrandId).Distinct().ToList();
        List<FamilyCandidate> candidates = keyed.Count == 0 ? [] : [.. (await (
                from variant in db.ProductVariants.AsNoTracking()
                join product in VisibleProducts(tenantId) on variant.ProductId equals product.Id
                where variant.TenantId == tenantId && variant.ModelCode != null && brandIds.Contains(product.BrandId)
                select new { variant.ProductId, product.BrandId, variant.ModelCode })
            .Distinct()
            .ToListAsync(cancellationToken)).Select(row => new FamilyCandidate(row.ProductId, row.BrandId, row.ModelCode))];
        var familyByView = views.ToDictionary(view => view.Id, view =>
        {
            var ids = ProductFamilyKeyPolicy.ModelCodeKey(view.ModelCode) is null ? [] : candidates
                .Where(row => ProductFamilyKeyPolicy.SameFamily(view.BrandId, view.ModelCode, row.BrandId, row.ModelCode))
                .Select(row => row.ProductId)
                .ToHashSet();
            ids.Add(view.Id);
            return ids;
        });
        var familyIds = familyByView.Values.SelectMany(ids => ids).Distinct().ToArray();
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && familyIds.Contains(x.Id))
            .Select(x => new { x.Id, x.CreatedAt }).ToListAsync(cancellationToken);
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && familyIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE" && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.ProductId, item.Id
                           select new { MediaId = item.Id, item.ProductId, item.VariantId, item.SortOrder, item.MediaRole, AssetId = asset.Id, asset.Classification, Url = asset.RelativePath })
            .ToListAsync(cancellationToken);
        var chartAssets = await db.ProductSizeCharts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && familyIds.Contains(x.ProductId) && x.FileAssetId != null)
            .Select(x => new { x.ProductId, AssetId = x.FileAssetId!.Value })
            .ToListAsync(cancellationToken);
        var chartSet = chartAssets.Select(x => (x.ProductId, x.AssetId)).ToHashSet();

        var frontByFamily = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Front(HashSet<Guid> ids)
        {
            var key = string.Join(',', ids.Order());
            if (frontByFamily.TryGetValue(key, out var cached)) return cached;
            var rows = media.Where(item => ids.Contains(item.ProductId)).ToList();
            // The editor's card order: oldest first (GetProductAsync familyCardOrder).
            var cardOrder = cards.Where(card => ids.Contains(card.Id)).OrderBy(card => card.CreatedAt).ThenBy(card => card.Id).Select(card => card.Id).ToList();
            var charts = rows.Where(item => chartSet.Contains((item.ProductId, item.AssetId))).Select(item => CatalogMediaDisplay.Url(item.AssetId, item.Classification, item.Url));
            return frontByFamily[key] = CatalogFamilyMedia.FrontUrl(
                [.. rows.Select(item => new CatalogFamilyMedia.Row(item.MediaId, item.ProductId, item.VariantId, item.SortOrder, CatalogMediaDisplay.Url(item.AssetId, item.Classification, item.Url)))],
                cardOrder,
                customOrder: rows.Any(item => item.MediaRole.StartsWith("ORDERED_", StringComparison.Ordinal)),
                charts);
        }
        return [.. views.Select(view => view with { FamilyImageUrl = Front(familyByView[view.Id]) })];
    }

    private sealed record FamilyCandidate(Guid ProductId, Guid? BrandId, string? ModelCode);
}
