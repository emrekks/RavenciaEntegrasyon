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
    private static async Task<byte[]> ReadStoredBytesAsync(IPrivateFileStorage storage, Guid tenantId, string relativePath, CancellationToken cancellationToken)
    {
        await using var original = await storage.OpenReadAsync(tenantId, relativePath, cancellationToken);
        using var buffer = new MemoryStream();
        await original.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static async Task<IResult> UploadProductMediaAsync(HttpContext http, AppDbContext db, IPrivateFileStorage storage, ProductMediaRenditions renditions, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http); var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (!http.Request.HasFormContentType) return Problem(http, new("VALIDATION_FAILED", "Multipart form-data gereklidir.", 422));
        var form = await http.Request.ReadFormAsync(http.RequestAborted); var file = form.Files.GetFile("file");
        if (file is null || !Guid.TryParse(form["productId"], out var productId)) return Problem(http, new("VALIDATION_FAILED", "file ve productId zorunludur.", 422));
        var variantId = Guid.TryParse(form["variantId"], out var parsedVariant) ? parsedVariant : (Guid?)null;
        if (!await db.Products.AnyAsync(x => x.TenantId == tenant.TenantId && x.Id == productId, http.RequestAborted) || variantId is Guid variant && !await db.ProductVariants.AnyAsync(x => x.TenantId == tenant.TenantId && x.ProductId == productId && x.Id == variant, http.RequestAborted)) return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün veya varyant bulunamadı.", 404));
        if (file.Length is <= 0 or > MaxUploadBytes || file.ContentType is not ("image/jpeg" or "image/png")) return Problem(http, new("VALIDATION_FAILED", "Yalnız en fazla 6 MiB JPEG veya PNG kabul edilir.", 422));
        await using var input = file.OpenReadStream(); await using var buffer = new MemoryStream(); await input.CopyToAsync(buffer, http.RequestAborted); var bytes = buffer.ToArray();
        var validMagic = file.ContentType == "image/jpeg" ? bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff : bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (!validMagic) return Problem(http, new("VALIDATION_FAILED", "Dosya imzası MIME türüyle eşleşmiyor.", 422));
        var sortOrder = 0;
        if (form.ContainsKey("sortOrder") && (!int.TryParse(form["sortOrder"], out sortOrder) || sortOrder is < 0 or > 999))
            return Problem(http, new("PRODUCT_MEDIA_SORT_INVALID", "sortOrder 0-999 arasında olmalıdır.", 422));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var asset = await FindUploadedAssetAsync(db, tenant.TenantId, hash, http.RequestAborted);
        if (asset is null)
        {
            buffer.Position = 0; var id = Guid.CreateVersion7(); var stored = await storage.SaveAsync(tenant.TenantId, $"{id:N}{(file.ContentType == "image/jpeg" ? ".jpg" : ".png")}", file.ContentType, buffer, MaxUploadBytes, http.RequestAborted);
            asset = new FileAsset { Id = id, TenantId = tenant.TenantId, Classification = "PRODUCT_MEDIA", RelativePath = stored, OriginalNameSafe = Path.GetFileName(file.FileName), MimeType = file.ContentType, SizeBytes = bytes.Length, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
            try { await db.SaveChangesAsync(http.RequestAborted); }
            catch (DbUpdateException)
            {
                // K11: a parallel upload of the same image may have stored it first (the partial unique index
                // UX_file_assets_active_product_media_sha256 refuses the second); reuse that asset. The file
                // written here stays unreferenced.
                db.ChangeTracker.Clear();
                asset = await FindUploadedAssetAsync(db, tenant.TenantId, hash, http.RequestAborted);
                if (asset is null) throw;
            }
        }
        // The thumbnails the panel shows (168 and 480 px WebP). A failure is logged and does not refuse the upload:
        // the first request for a missing copy makes it.
        await renditions.TryEnsureAsync(tenant.TenantId, asset.Id, _ => Task.FromResult(bytes), http.RequestAborted);
        var isCustomProductGallery = variantId is null && await db.ProductMedia.AnyAsync(x => x.TenantId == tenant.TenantId && x.ProductId == productId && x.VariantId == null && x.Status == "ACTIVE" && x.MediaRole.StartsWith("ORDERED_"), http.RequestAborted);
        // A new picture never takes the place of a live one (another colour's included): it goes after the gallery then.
        var requestedSortOrder = sortOrder;
        sortOrder = ProductFamilyMediaOrdering.UploadSortOrder(sortOrder, (await db.ProductMedia.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.ProductId == productId && x.VariantId == variantId)
            .Select(x => new { x.SortOrder, x.Status, x.FileAssetId }).ToListAsync(http.RequestAborted))
            .Select(x => (x.SortOrder, x.Status == "ACTIVE", x.FileAssetId)), asset.Id);
        var mediaRole = isCustomProductGallery ? sortOrder == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY" : MediaRole(sortOrder == requestedSortOrder ? form["mediaRole"].ToString() : "GALLERY");
        var media = await db.ProductMedia.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.ProductId == productId && x.VariantId == variantId && x.SortOrder == sortOrder, http.RequestAborted);
        if (media is null) { media = new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, ProductId = productId, VariantId = variantId, FileAssetId = asset.Id, MediaRole = mediaRole, SortOrder = sortOrder, AltText = string.IsNullOrWhiteSpace(form["altText"]) ? null : form["altText"].ToString().Trim(), Status = "ACTIVE" }; db.ProductMedia.Add(media); }
        else { media.FileAssetId = asset.Id; media.MediaRole = mediaRole; media.AltText = string.IsNullOrWhiteSpace(form["altText"]) ? null : form["altText"].ToString().Trim(); media.Status = "ACTIVE"; }
        await CatalogLocalEdits.MarkMediaEditedAsync(db, tenant.TenantId, [productId], timeProvider.GetUtcNow(), http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Created($"/api/v1/products/{productId:D}", new { media.Id, media.ProductId, media.VariantId, media.FileAssetId, media.MediaRole, media.SortOrder, media.AltText, media.Status });
    }

    private static async Task<IResult> ReconcileProductMediaAsync([FromBody] ReconcileProductMediaCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        var items = command.Items ?? [];
        if (items.Count > 100 || items.Any(item => item.SortOrder is < 0 or > 999) || items.Select(item => item.SortOrder).Distinct().Count() != items.Count)
            return Problem(http, new("PRODUCT_MEDIA_RECONCILE_INVALID", "Görsel sayısı en fazla 100 olabilir; sıralar 0-999 arasında ve benzersiz olmalıdır.", 422));

        var product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == command.ProductId, http.RequestAborted);
        if (product is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün bulunamadı.", 404));
        if (command.VariantId is Guid variantId && !await db.ProductVariants.AnyAsync(x => x.TenantId == tenant.TenantId && x.ProductId == command.ProductId && x.Id == variantId, http.RequestAborted))
            return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün varyantı bulunamadı.", 404));

        var references = new List<(Guid? AssetId, string? Url, int SortOrder)>();
        var externalUrls = new HashSet<string>(StringComparer.Ordinal);
        var storedAssetIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (TryGetStoredProductMediaAssetId(item.Url, out var assetId))
            {
                storedAssetIds.Add(assetId);
                references.Add((assetId, null, item.SortOrder));
                continue;
            }
            if (!TryNormalizePublicProductMediaUrl(item.Url, out var url, out var issue))
                return Problem(http, new("PRODUCT_MEDIA_URL_INVALID", issue!, 422, new Dictionary<string, string[]> { ["url"] = [issue!] }));
            externalUrls.Add(url!);
            references.Add((null, url, item.SortOrder));
        }

        var assets = await db.FileAssets
            .Where(asset => asset.TenantId == tenant.TenantId && (storedAssetIds.Contains(asset.Id) || externalUrls.Contains(asset.RelativePath)))
            .ToListAsync(http.RequestAborted);
        var activeTenantMediaAssetIds = storedAssetIds.Count == 0
            ? new HashSet<Guid>()
            : (await db.ProductMedia.AsNoTracking()
                .Where(media => media.TenantId == tenant.TenantId && storedAssetIds.Contains(media.FileAssetId) && media.Status == "ACTIVE")
                .Select(media => media.FileAssetId)
                .Distinct()
                .ToListAsync(http.RequestAborted)).ToHashSet();
        // A size chart generated for this card ("Kullan") has no picture row yet; its save attaches it. Any of the card's
        // tries counts, not only the newest: Kullan, then Yeniden Oluştur without using it, then Kaydet (O8).
        activeTenantMediaAssetIds.UnionWith(await MarketplaceHub.Infrastructure.SizeCharts.SizeChartService.GeneratedForProductAsync(db, tenant.TenantId, command.ProductId, storedAssetIds, http.RequestAborted));
        var assetsById = assets.ToDictionary(asset => asset.Id);
        var assetsByUrl = assets.Where(asset => externalUrls.Contains(asset.RelativePath)).ToDictionary(asset => asset.RelativePath, StringComparer.Ordinal);
        var desiredAssets = new List<FileAsset>(references.Count);
        var now = timeProvider.GetUtcNow();
        foreach (var reference in references)
        {
            if (reference.AssetId is Guid storedId)
            {
                if (!activeTenantMediaAssetIds.Contains(storedId) || !assetsById.TryGetValue(storedId, out var storedAsset) || storedAsset.Status != "ACTIVE" || storedAsset.Classification != "PRODUCT_MEDIA")
                    return Problem(http, new("PRODUCT_MEDIA_NOT_FOUND", "Kayıtlı görsellerden biri bulunamadı veya artık kullanılamıyor.", 404));
                desiredAssets.Add(storedAsset);
                continue;
            }

            var url = reference.Url!;
            if (!assetsByUrl.TryGetValue(url, out var remoteAsset))
            {
                var uri = new Uri(url, UriKind.Absolute);
                var fileName = Path.GetFileName(uri.AbsolutePath);
                remoteAsset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, Classification = "PRODUCT_MEDIA_URL", RelativePath = url, OriginalNameSafe = string.IsNullOrWhiteSpace(fileName) ? null : fileName[..Math.Min(fileName.Length, 256)], MimeType = "image/remote", SizeBytes = 0, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))), Status = "ACTIVE", CreatedAt = now };
                db.FileAssets.Add(remoteAsset);
                assetsByUrl[url] = remoteAsset;
            }
            else if (remoteAsset.Classification != "PRODUCT_MEDIA_URL")
                return Problem(http, new("PRODUCT_MEDIA_URL_CONFLICT", "Bu URL farklı bir dosya sınıfında kayıtlı.", 409));
            else { remoteAsset.Status = "ACTIVE"; remoteAsset.ArchivedAt = null; }
            desiredAssets.Add(remoteAsset);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var scopedMedia = await db.ProductMedia
            .Where(media => media.TenantId == tenant.TenantId && media.ProductId == command.ProductId && media.VariantId == command.VariantId)
            .OrderBy(media => media.SortOrder)
            .ToListAsync(http.RequestAborted);
        var desiredOrder = desiredAssets
            .Select((asset, index) => new
            {
                AssetId = asset.Id,
                SortOrder = references[index].SortOrder,
                MediaRole = command.VariantId is null
                    ? references[index].SortOrder == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY"
                    : references[index].SortOrder == 0 ? "PRIMARY" : "GALLERY"
            })
            .OrderBy(item => item.SortOrder)
            .ToArray();
        var activeScopedMedia = scopedMedia.Where(media => media.Status == "ACTIVE").OrderBy(media => media.SortOrder).ToArray();
        var safeAltText = SafeAltText(command.AltText);
        if (activeScopedMedia.Length == desiredOrder.Length && activeScopedMedia.Select((media, index) =>
                media.SortOrder == desiredOrder[index].SortOrder
                && media.FileAssetId == desiredOrder[index].AssetId
                && media.MediaRole == desiredOrder[index].MediaRole
                && media.AltText == safeAltText).All(isSame => isSame))
        {
            await transaction.CommitAsync(http.RequestAborted);
            return Results.Ok(new { productId = product.Id, variantId = command.VariantId, mediaCount = activeScopedMedia.Length });
        }
        var occupiedSortOrders = scopedMedia.Select(media => media.SortOrder).ToHashSet();
        var temporarySortOrders = new HashSet<int>();
        var nextTemporarySortOrder = -1;
        int TemporarySortOrder()
        {
            while (occupiedSortOrders.Contains(nextTemporarySortOrder) || !temporarySortOrders.Add(nextTemporarySortOrder))
            {
                if (nextTemporarySortOrder == int.MinValue) throw new InvalidOperationException("Geçici görsel sıralama alanı bulunamadı.");
                nextTemporarySortOrder--;
            }
            var value = nextTemporarySortOrder;
            if (nextTemporarySortOrder != int.MinValue) nextTemporarySortOrder--;
            return value;
        }

        foreach (var media in scopedMedia)
        {
            var temporarySortOrder = TemporarySortOrder();
            var updatedCount = await db.ProductMedia
                .Where(item => item.TenantId == tenant.TenantId && item.Id == media.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.SortOrder, temporarySortOrder), http.RequestAborted);
            if (updatedCount != 1) return Problem(http, new("PRODUCT_MEDIA_CHANGED", "Görsel sırası eşzamanlı olarak değişti. Sayfayı yenileyip tekrar deneyin.", 409));
            media.SortOrder = temporarySortOrder;
            var sortOrderEntry = db.Entry(media).Property(item => item.SortOrder);
            sortOrderEntry.OriginalValue = temporarySortOrder;
            sortOrderEntry.IsModified = false;
        }

        var availableMedia = scopedMedia.ToList();
        var desiredMedia = new List<ProductMedia>(references.Count);
        foreach (var (asset, index) in desiredAssets.Select((asset, index) => (asset, index)))
        {
            var media = availableMedia.FirstOrDefault(candidate => candidate.FileAssetId == asset.Id) ?? availableMedia.FirstOrDefault();
            if (media is not null) availableMedia.Remove(media);
            else
            {
                media = new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, ProductId = command.ProductId, VariantId = command.VariantId, FileAssetId = asset.Id, MediaRole = "GALLERY", SortOrder = TemporarySortOrder(), Status = "ACTIVE" };
                db.ProductMedia.Add(media);
            }
            media.FileAssetId = asset.Id;
            media.MediaRole = command.VariantId is null
                ? references[index].SortOrder == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY"
                : MediaRole(references[index].SortOrder == 0 ? "PRIMARY" : "GALLERY");
            media.SortOrder = references[index].SortOrder;
            db.Entry(media).Property(item => item.SortOrder).IsModified = true;
            media.AltText = safeAltText;
            media.Status = "ACTIVE";
            desiredMedia.Add(media);
        }
        foreach (var media in availableMedia) media.Status = "ARCHIVED";
        await CatalogLocalEdits.MarkMediaEditedAsync(db, tenant.TenantId, [product], now, http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        return Results.Ok(new { productId = product.Id, variantId = command.VariantId, mediaCount = desiredMedia.Count });
    }
}
