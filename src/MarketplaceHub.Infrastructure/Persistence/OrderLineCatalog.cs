using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The one place that finds an order line's panel variant and photo, for every screen that shows order lines (order
// list, order detail, invoice workspace and its detail): by the line's variant, its stock code and barcode (also in
// their normalized forms), then by the marketplace link through the line's stock code or listing ids. A line whose
// codes changed on the marketplace only is still found by its listing (Trendyol MZ052BOD-16 → MZ051BYC-16).
internal sealed class OrderLineCatalog
{
    private readonly IReadOnlyDictionary<Guid, ProductVariant> variants;
    private readonly IReadOnlyDictionary<string, ProductVariant> variantsBySku;
    private readonly IReadOnlyDictionary<string, ProductVariant> variantsByNormalizedSku;
    private readonly IReadOnlyDictionary<string, ProductVariant> variantsByBarcode;
    private readonly IReadOnlyDictionary<(Guid ConnectionId, string ExternalId), Guid> variantsByMarketplaceIdentity;
    private readonly IReadOnlyDictionary<Guid, string> imageUrls;
    private readonly Func<OrderLine, Guid?> connectionOf;

    private OrderLineCatalog(List<ProductVariant> variantRows, List<MarketplaceVariantLink> links, IReadOnlyDictionary<Guid, string> imageUrls, Func<OrderLine, Guid?> connectionOf)
    {
        variants = variantRows.ToDictionary(x => x.Id);
        variantsBySku = variantRows.GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        variantsByNormalizedSku = variantRows.GroupBy(x => x.SkuNormalized, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        variantsByBarcode = MarketplaceSalesService.BarcodeVariantLookup(variantRows);
        variantsByMarketplaceIdentity = links
            .SelectMany(link => MarketplaceSalesService.LinkIdentities(link).Select(identity => (Key: (link.ConnectionId, ExternalId: identity), link.VariantId)))
            .GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.First().VariantId);
        this.imageUrls = imageUrls;
        this.connectionOf = connectionOf;
    }

    public static async Task<OrderLineCatalog> LoadAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<OrderLine> lines, Func<OrderLine, Guid?> connectionOf, CancellationToken cancellationToken)
    {
        var variantIds = lines.Where(x => x.VariantId is not null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var lineSkus = lines.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var lineSkuKeys = lines.SelectMany(x => MarketplaceSalesService.CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.Ordinal).ToArray();
        var lineBarcodes = lines.Select(x => x.Barcode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var lineBarcodeKeys = lines.SelectMany(x => MarketplaceSalesService.CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var identityKeys = lines.SelectMany(MarketplaceSalesService.LineMarketplaceIdentities).Distinct().ToArray();
        var connectionIds = lines.Select(connectionOf).Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray();
        var links = identityKeys.Length == 0 || connectionIds.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId
                    && connectionIds.Contains(x.ConnectionId)
                    && (identityKeys.Contains(x.ExternalId.Trim().ToUpper()) || x.ExternalSku != null && identityKeys.Contains(x.ExternalSku.Trim().ToUpper())))
                .ToListAsync(cancellationToken);
        var linkedVariantIds = links.Select(link => link.VariantId).Distinct().ToArray();
        var variantRows = variantIds.Length + linkedVariantIds.Length + lineSkus.Length + lineBarcodes.Length == 0
            ? []
            : await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId &&
                (variantIds.Contains(x.Id) || linkedVariantIds.Contains(x.Id) || lineSkus.Contains(x.Sku) || lineSkuKeys.Contains(x.SkuNormalized) ||
                 lineBarcodes.Contains(x.Barcode) || lineBarcodeKeys.Contains(x.BarcodeNormalized))).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var imageUrls = await MediaUrlsAsync(db, tenantId, variantRows.Select(x => x.Id), cancellationToken);
        return new OrderLineCatalog(variantRows, links, imageUrls, connectionOf);
    }

    public ProductVariant? Variant(OrderLine line)
    {
        if (line.VariantId is { } variantId) return variants.GetValueOrDefault(variantId);
        if (variantsBySku.TryGetValue(line.Sku, out var bySku)) return bySku;
        foreach (var key in MarketplaceSalesService.CatalogLookupKeys(line.Sku, 160))
            if (variantsByNormalizedSku.TryGetValue(key, out var byNormalizedSku)) return byNormalizedSku;
        foreach (var key in MarketplaceSalesService.CatalogLookupKeys(line.Barcode, 160))
            if (variantsByBarcode.TryGetValue(key, out var byBarcode)) return byBarcode;
        if (connectionOf(line) is not { } connectionId) return null;
        foreach (var identity in MarketplaceSalesService.LineMarketplaceIdentities(line))
            if (variantsByMarketplaceIdentity.TryGetValue((connectionId, identity), out var linkedId) && variants.GetValueOrDefault(linkedId) is { } linked) return linked;
        return null;
    }

    // The catalog's photo of the variant (its own first, else the product's).
    public string? CatalogImageUrl(ProductVariant? variant) => variant is null ? null : imageUrls.GetValueOrDefault(variant.Id);

    // The line's photo: the marketplace's own address when the line carries one, with the catalog's as the fallback.
    public (string? ImageUrl, string? FallbackImageUrl) Images(OrderLine line, ProductVariant? variant) =>
        MarketplaceSalesService.LineImageUrls(MarketplaceSalesService.SourceLineImageUrl(line.SourceSnapshotJson), CatalogImageUrl(variant));

    public static async Task<Dictionary<Guid, string>> MediaUrlsAsync(AppDbContext db, Guid tenantId, IEnumerable<Guid> variantIds, CancellationToken cancellationToken)
    {
        var ids = variantIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await (from variant in db.ProductVariants.AsNoTracking()
                          join media in db.ProductMedia.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { media.TenantId, media.ProductId }
                          join asset in db.FileAssets.AsNoTracking() on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                          where variant.TenantId == tenantId && ids.Contains(variant.Id) && (media.VariantId == null || media.VariantId == variant.Id) && media.Status == "ACTIVE" && asset.Status == "ACTIVE" && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                          orderby media.VariantId == variant.Id ? 0 : 1, media.SortOrder
                          select new { VariantId = variant.Id, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First().Classification == "PRODUCT_MEDIA_URL" ? x.First().Url : $"/api/v1/files/product-media/{x.First().Id:D}/content");
    }
}
