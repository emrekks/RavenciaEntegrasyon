using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The price/stock send log page (İşlem Takibi › Loglar). Every read is tenant-first and newest-first over the log's own
// indexes, at most 100 rows a page; a search first resolves the products it names to their variants (bounded), so the
// log itself is only read by variant. The page's products, pictures and stores are read once per page.
public sealed class PriceStockSendLogService(AppDbContext db) : IPriceStockSendLogService
{
    private const string LikeEscape = "\\";
    // A search naming more variants than this reads the newest matches only; a sharper term finds the rest.
    internal const int MaximumSearchVariants = 1_000;

    public async Task<ServiceResult<PageResult<PriceStockSendLogView>>> PageAsync(Guid tenantId, PriceStockSendLogQuery request, CancellationToken cancellationToken)
    {
        var empty = ServiceResult<PageResult<PriceStockSendLogView>>.Ok(new([], null, false));
        var query = db.PriceStockSendLogs.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (request.ConnectionId is { } connectionId) query = query.Where(x => x.ConnectionId == connectionId);
        var platforms = Codes(request.Platforms, null);
        if (platforms is not null) query = query.Where(x => platforms.Contains(x.PlatformCode));
        var results = Codes(request.Results, PriceStockSendResults.All);
        if (results is { Length: 0 }) return empty;
        if (results is not null) query = query.Where(x => results.Contains(x.Result));
        var triggers = Codes(request.Triggers, PriceStockSendTriggers.All);
        if (triggers is { Length: 0 }) return empty;
        if (triggers is not null) query = query.Where(x => triggers.Contains(x.Trigger));
        if (request.From is { } from) { var fromUtc = from.ToUniversalTime(); query = query.Where(x => x.CreatedAt >= fromUtc); }
        if (request.To is { } to) { var toUtc = to.ToUniversalTime(); query = query.Where(x => x.CreatedAt <= toUtc); }
        var term = request.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            if (term.Length > PriceStockSendLogQuery.MaximumSearchLength) term = term[..PriceStockSendLogQuery.MaximumSearchLength];
            var variantIds = await SearchVariantIdsAsync(tenantId, term, cancellationToken);
            if (variantIds.Count == 0) return empty;
            query = query.Where(x => variantIds.Contains(x.VariantId));
        }
        if (!string.IsNullOrWhiteSpace(request.After))
        {
            if (!JobListCursor.TryDecode(request.After, out var afterCreatedAt, out var afterId))
                return ServiceResult<PageResult<PriceStockSendLogView>>.Fail("SEND_LOG_CURSOR_INVALID", "Listenin devamı okunamadı. Listeyi yenileyip tekrar deneyin.", 400);
            query = query.Where(x => x.CreatedAt < afterCreatedAt || (x.CreatedAt == afterCreatedAt && x.Id.CompareTo(afterId) < 0));
        }
        var limit = request.EffectiveLimit;
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore) rows = rows.Take(limit).ToList();
        var nextCursor = hasMore ? JobListCursor.Encode(rows[^1].CreatedAt, rows[^1].Id) : null;
        return ServiceResult<PageResult<PriceStockSendLogView>>.Ok(new(await ViewsAsync(tenantId, rows, cancellationToken), nextCursor, hasMore));
    }

    // Null: no filter. Empty: a filter that names only unknown values, so nothing can match.
    private static string[]? Codes(IReadOnlyList<string>? values, IReadOnlyList<string>? known)
    {
        var codes = (values ?? []).Select(code => code.Trim().ToUpperInvariant()).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).Take(20).ToArray();
        if (codes.Length == 0) return null;
        return known is null ? codes : codes.Where(code => known.Contains(code, StringComparer.Ordinal)).ToArray();
    }

    // The variants a search names: by panel product (title, SKU, barcode, model code) or by the marketplace's code for it.
    private async Task<List<Guid>> SearchVariantIdsAsync(Guid tenantId, string term, CancellationToken cancellationToken)
    {
        var pattern = $"%{CatalogService.EscapeLike(term)}%";
        var byProduct = await (from variant in db.ProductVariants.AsNoTracking()
                               join product in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { product.TenantId, ProductId = product.Id }
                               where variant.TenantId == tenantId
                                   && (EF.Functions.ILike(variant.Sku, pattern, LikeEscape)
                                       || (variant.Barcode != null && EF.Functions.ILike(variant.Barcode, pattern, LikeEscape))
                                       || (variant.ModelCode != null && EF.Functions.ILike(variant.ModelCode, pattern, LikeEscape))
                                       || EF.Functions.ILike(product.Title, pattern, LikeEscape))
                               select variant.Id)
            .Take(MaximumSearchVariants)
            .ToListAsync(cancellationToken);
        var byMarketplaceCode = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && (EF.Functions.ILike(x.ExternalId, pattern, LikeEscape)
                    || (x.ExternalBarcode != null && EF.Functions.ILike(x.ExternalBarcode, pattern, LikeEscape))
                    || (x.ExternalSku != null && EF.Functions.ILike(x.ExternalSku, pattern, LikeEscape))))
            .Select(x => x.VariantId)
            .Take(MaximumSearchVariants)
            .ToListAsync(cancellationToken);
        return byProduct.Concat(byMarketplaceCode).Distinct().Take(MaximumSearchVariants).ToList();
    }

    private async Task<IReadOnlyList<PriceStockSendLogView>> ViewsAsync(Guid tenantId, List<MarketplaceHub.Domain.PriceStockSendLog> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var variantIds = rows.Select(x => x.VariantId).Distinct().ToArray();
        var connectionIds = rows.Select(x => x.ConnectionId).Distinct().ToArray();
        var variants = await (from variant in db.ProductVariants.AsNoTracking()
                              join product in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { product.TenantId, ProductId = product.Id }
                              where variant.TenantId == tenantId && variantIds.Contains(variant.Id)
                              select new { variant.Id, variant.ProductId, variant.ModelCode, product.Title })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var productIds = variants.Values.Select(x => x.ProductId).Distinct().ToArray();
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && productIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE"
                               && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.Id
                           select new { item.ProductId, item.VariantId, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        var stores = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
        string? Image(Guid variantId, Guid productId)
        {
            var picked = media.FirstOrDefault(x => x.VariantId == variantId)
                ?? media.FirstOrDefault(x => x.ProductId == productId && x.VariantId == null)
                ?? media.FirstOrDefault(x => x.ProductId == productId);
            return picked is null ? null : CatalogMediaDisplay.Url(picked.Id, picked.Classification, picked.Url);
        }
        return rows.Select(row =>
        {
            var variant = variants.GetValueOrDefault(row.VariantId);
            return new PriceStockSendLogView(row.Id, row.CreatedAt, row.CompletedAt, row.ConnectionId, row.PlatformCode, stores.GetValueOrDefault(row.ConnectionId),
                row.JobId, row.VariantId, variant?.ProductId, variant?.Title, variant is null ? null : Image(row.VariantId, variant.ProductId),
                row.Barcode, row.StockCode, variant?.ModelCode,
                row.PreviousQuantity, row.NewQuantity, row.PreviousSalePrice, row.NewSalePrice, row.PreviousListPrice, row.NewListPrice,
                row.Currency, row.Trigger, row.Result, row.ErrorCode, row.ErrorMessage, row.ExternalRequestId);
        }).ToList();
    }
}
