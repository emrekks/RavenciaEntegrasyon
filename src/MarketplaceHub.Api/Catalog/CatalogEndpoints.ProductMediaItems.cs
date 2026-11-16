using System.Net;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static async Task<IResult> RegisterProductMediaUrlAsync(RegisterProductMediaUrl command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (!Uri.TryCreate(command.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsoluteUri.Length > 512)
            return Problem(http, new("PRODUCT_MEDIA_URL_INVALID", "Kalıcı, kullanıcı bilgisi içermeyen ve en fazla 512 karakterlik HTTPS görsel URL'si gereklidir.", 422, new Dictionary<string, string[]> { ["url"] = ["Geçerli bir HTTPS görsel URL'si girin."] }));
        if (uri.IsLoopback || !IsPublicHost(uri.Host) || IPAddress.TryParse(uri.Host, out var address) && !IsPublicAddress(address))
            return Problem(http, new("PRODUCT_MEDIA_URL_INVALID", "Yerel, özel ağ veya loopback görsel adresi kullanılamaz.", 422, new Dictionary<string, string[]> { ["url"] = ["Trendyol tarafından internet üzerinden erişilebilen bir adres girin."] }));
        if (command.SortOrder is < 0 or > 999) return Problem(http, new("PRODUCT_MEDIA_SORT_INVALID", "sortOrder 0-999 arasında olmalıdır.", 422));
        if (!await db.Products.AnyAsync(x => x.TenantId == tenant.TenantId && x.Id == command.ProductId, http.RequestAborted) || command.VariantId is Guid variantId && !await db.ProductVariants.AnyAsync(x => x.TenantId == tenant.TenantId && x.ProductId == command.ProductId && x.Id == variantId, http.RequestAborted))
            return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün veya varyant bulunamadı.", 404));

        var url = uri.AbsoluteUri;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var asset = await db.FileAssets.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.RelativePath == url, http.RequestAborted);
        if (asset is not null && asset.Classification != "PRODUCT_MEDIA_URL") return Problem(http, new("PRODUCT_MEDIA_URL_CONFLICT", "Bu URL farklı bir dosya sınıfında kayıtlı.", 409));
        if (asset is null)
        {
            var originalName = Path.GetFileName(uri.AbsolutePath);
            asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, Classification = "PRODUCT_MEDIA_URL", RelativePath = url, OriginalNameSafe = string.IsNullOrWhiteSpace(originalName) ? null : originalName[..Math.Min(originalName.Length, 256)], MimeType = "image/remote", SizeBytes = 0, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
        }
        else
        {
            asset.Status = "ACTIVE";
            asset.ArchivedAt = null;
        }

        // K27: a product gallery the user has ordered keeps its ORDERED_* roles for URL images too, the same
        // rule the upload path applies.
        var isCustomProductGallery = command.VariantId is null && await db.ProductMedia.AnyAsync(x => x.TenantId == tenant.TenantId && x.ProductId == command.ProductId && x.VariantId == null && x.Status == "ACTIVE" && x.MediaRole.StartsWith("ORDERED_"), http.RequestAborted);
        // Like an upload: a new link never takes the place of a live picture; it goes after the gallery then.
        var sortOrder = ProductFamilyMediaOrdering.UploadSortOrder(command.SortOrder, (await db.ProductMedia.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.ProductId == command.ProductId && x.VariantId == command.VariantId)
            .Select(x => new { x.SortOrder, x.Status, x.FileAssetId }).ToListAsync(http.RequestAborted))
            .Select(x => (x.SortOrder, x.Status == "ACTIVE", x.FileAssetId)), asset.Id);
        var mediaRole = isCustomProductGallery ? sortOrder == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY" : MediaRole(sortOrder == command.SortOrder ? command.MediaRole : "GALLERY");
        var media = await db.ProductMedia.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.ProductId == command.ProductId && x.VariantId == command.VariantId && x.SortOrder == sortOrder, http.RequestAborted);
        if (media is null)
        {
            media = new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, ProductId = command.ProductId, VariantId = command.VariantId, FileAssetId = asset.Id, MediaRole = mediaRole, SortOrder = sortOrder, AltText = SafeAltText(command.AltText), Status = "ACTIVE" };
            db.ProductMedia.Add(media);
        }
        else
        {
            media.FileAssetId = asset.Id;
            media.MediaRole = mediaRole;
            media.AltText = SafeAltText(command.AltText);
            media.Status = "ACTIVE";
        }
        await CatalogLocalEdits.MarkMediaEditedAsync(db, tenant.TenantId, [command.ProductId], timeProvider.GetUtcNow(), http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Created($"/api/v1/products/{command.ProductId:D}", new { media.Id, media.ProductId, media.VariantId, media.FileAssetId, url, media.MediaRole, media.SortOrder, media.AltText, media.Status });
    }

    private static async Task<IResult> ClearProductMediaAsync(Guid productId, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (!await db.Products.AnyAsync(x => x.TenantId == tenant.TenantId && x.Id == productId))
            return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün bulunamadı.", 404));

        var media = await db.ProductMedia
            .Where(x => x.TenantId == tenant.TenantId && x.ProductId == productId && x.VariantId == null && x.Status == "ACTIVE")
            .ToListAsync(http.RequestAborted);
        foreach (var item in media) item.Status = "ARCHIVED";
        await ClearLeftSizeChartsAsync(db, tenant.TenantId, media, timeProvider.GetUtcNow(), http.RequestAborted);
        if (media.Count > 0) await CatalogLocalEdits.MarkMediaEditedAsync(db, tenant.TenantId, [productId], timeProvider.GetUtcNow(), http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    // D5 on the bulk paths (scan, 13 Nov 2026): a card's size chart whose picture the card no longer shows on any active
    // row once these rows are archived is no longer its chart; left set, it went to every variant as the last picture and
    // kept the file from the purge. Nothing is saved here.
    private static async Task ClearLeftSizeChartsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<ProductMedia> archived, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (archived.Count == 0) return;
        var productIds = archived.Select(item => item.ProductId).Distinct().ToArray();
        var assetIds = archived.Select(item => item.FileAssetId).Distinct().ToArray();
        var archivedIds = archived.Select(item => item.Id).ToArray();
        var charts = await db.ProductSizeCharts
            .Where(chart => chart.TenantId == tenantId && productIds.Contains(chart.ProductId) && chart.FileAssetId != null && assetIds.Contains(chart.FileAssetId.Value))
            .ToListAsync(cancellationToken);
        foreach (var chart in charts)
        {
            var stillShown = await db.ProductMedia.AnyAsync(item => item.TenantId == tenantId && item.ProductId == chart.ProductId && item.FileAssetId == chart.FileAssetId
                && item.Status == "ACTIVE" && !archivedIds.Contains(item.Id), cancellationToken);
            if (stillShown) continue;
            chart.FileAssetId = null;
            chart.UpdatedAt = now;
        }
    }

    private static async Task<IResult> DeleteProductMediaItemsAsync([FromBody] DeleteProductMediaItemsCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        var mediaIds = command.MediaIds?.Distinct().ToArray() ?? [];
        if (mediaIds.Length is 0 or > 100) return Problem(http, new("PRODUCT_MEDIA_DELETE_LIMIT", "Bir işlemde 1-100 görsel bağlantısı kaldırılabilir.", 422));

        // A row the same save already archived (the variant pictures Kaydet reconciled a moment before) counts as removed;
        // only an id this tenant never had is refused.
        var found = await db.ProductMedia.Where(item => item.TenantId == tenant.TenantId && mediaIds.Contains(item.Id)).ToListAsync(http.RequestAborted);
        if (found.Count != mediaIds.Length) return Problem(http, new("PRODUCT_MEDIA_NOT_FOUND", "Görsel bağlantılarından biri bulunamadı.", 404));
        var media = found.Where(item => item.Status == "ACTIVE").ToList();
        if (media.Count == 0) return Results.Ok(new { deletedCount = 0 });
        var productIds = media.Select(item => item.ProductId).Distinct().ToArray();
        var products = await db.Products.Where(product => product.TenantId == tenant.TenantId && productIds.Contains(product.Id)).ToListAsync(http.RequestAborted);
        if (products.Count != productIds.Length) return Problem(http, new("PRODUCT_MEDIA_NOT_FOUND", "Görselin kaynak ürünü bulunamadı.", 404));

        foreach (var item in media) item.Status = "ARCHIVED";
        await ClearLeftSizeChartsAsync(db, tenant.TenantId, media, timeProvider.GetUtcNow(), http.RequestAborted);
        await CatalogLocalEdits.MarkMediaEditedAsync(db, tenant.TenantId, products, timeProvider.GetUtcNow(), http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { deletedCount = media.Count });
    }

    private static async Task<IResult> DeleteProductMediaItemAsync([FromBody] DeleteProductMediaItemCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } keyFailure) return keyFailure;
        if (!TryIfMatch(http, out var expectedVersion, out var versionFailure)) return versionFailure!;

        var product = await db.Products.SingleOrDefaultAsync(item => item.TenantId == tenant.TenantId && item.Id == command.ProductId, http.RequestAborted);
        if (product is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün bulunamadı.", 404));
        if (product.Version != expectedVersion)
        {
            http.Response.Headers.ETag = $"\"v{product.Version}\"";
            return Problem(http, new("PRECONDITION_FAILED", "Ürün başka bir işlemle değişti. Sayfayı yenileyip tekrar deneyin.", 412));
        }

        Guid assetId;
        if (TryGetStoredProductMediaAssetId(command.Url, out var storedAssetId))
        {
            assetId = storedAssetId;
        }
        else
        {
            if (!TryNormalizePublicProductMediaUrl(command.Url, out var normalizedUrl, out var issue))
                return Problem(http, new("PRODUCT_MEDIA_URL_INVALID", issue!, 422));
            var remoteAsset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(asset =>
                asset.TenantId == tenant.TenantId && asset.RelativePath == normalizedUrl && asset.Classification == "PRODUCT_MEDIA_URL" && asset.Status == "ACTIVE",
                http.RequestAborted);
            if (remoteAsset is null) return Problem(http, new("PRODUCT_MEDIA_NOT_FOUND", "Görsel üründe artık etkin değil.", 404));
            assetId = remoteAsset.Id;
        }

        var asset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == tenant.TenantId && item.Id == assetId && item.Status == "ACTIVE" && (item.Classification == "PRODUCT_MEDIA" || item.Classification == "PRODUCT_MEDIA_URL"),
            http.RequestAborted);
        if (asset is null) return Problem(http, new("PRODUCT_MEDIA_NOT_FOUND", "Görsel üründe artık etkin değil.", 404));

        var modelCode = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenant.TenantId && variant.ProductId == product.Id && variant.ModelCode != null && variant.ModelCode != "")
            .OrderBy(variant => variant.SortOrder).ThenBy(variant => variant.Id)
            .Select(variant => variant.ModelCode)
            .FirstOrDefaultAsync(http.RequestAborted);
        var familyProductIds = await VisibleFamilyProductIdsAsync(db, tenant.TenantId, product.BrandId, modelCode, http.RequestAborted);
        if (!familyProductIds.Contains(product.Id)) familyProductIds.Add(product.Id);

        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var media = await db.ProductMedia
            .Where(item => item.TenantId == tenant.TenantId && familyProductIds.Contains(item.ProductId) && item.FileAssetId == assetId && item.Status == "ACTIVE")
            .ToListAsync(http.RequestAborted);
        if (media.Count == 0) return Problem(http, new("PRODUCT_MEDIA_NOT_FOUND", "Görsel ürün ailesinde artık etkin değil.", 404));

        foreach (var item in media) item.Status = "ARCHIVED";
        var affectedProductIds = media.Select(item => item.ProductId).Distinct().ToArray();
        var affectedProducts = await db.Products
            .Where(item => item.TenantId == tenant.TenantId && affectedProductIds.Contains(item.Id))
            .ToListAsync(http.RequestAborted);
        // D5: a picture that leaves its card is no longer the card's size chart.
        foreach (var chart in await db.ProductSizeCharts.Where(chart => chart.TenantId == tenant.TenantId && affectedProductIds.Contains(chart.ProductId) && chart.FileAssetId == assetId).ToListAsync(http.RequestAborted))
        {
            chart.FileAssetId = null;
            chart.UpdatedAt = timeProvider.GetUtcNow();
        }
        await CatalogLocalEdits.MarkMediaEditedAsync(db, tenant.TenantId, affectedProducts, timeProvider.GetUtcNow(), http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        var version = affectedProducts.SingleOrDefault(item => item.Id == product.Id)?.Version ?? product.Version;
        http.Response.Headers.ETag = $"\"v{version}\"";
        return Results.Ok(new { deletedCount = media.Count, version, productVersions = affectedProducts.ToDictionary(item => item.Id.ToString("D"), item => item.Version) });
    }
}
