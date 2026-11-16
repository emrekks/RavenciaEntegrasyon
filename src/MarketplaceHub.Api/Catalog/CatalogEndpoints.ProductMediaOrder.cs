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
    // D9: the products of the same brand whose model code has the same key, among products that are not linked
    // only to hidden connections. Empty when the model code is empty.
    private static Task<List<Guid>> VisibleFamilyProductIdsAsync(AppDbContext db, Guid tenantId, Guid? brandId, string? modelCode, CancellationToken cancellationToken) =>
        ProductFamilyScope.ProductIdsAsync(db, tenantId, brandId, modelCode, db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId
                && (!db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == variant.ProductId)
                    || db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == variant.ProductId
                        && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == link.ConnectionId
                            && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))))),
            cancellationToken);

    private static Task<IResult> ReorderProductFamilyMediaAsync([FromBody] ReorderProductFamilyMediaCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider) =>
        command.UrlsInOrder is null
            ? ReorderProductFamilyMediaLegacyAsync(command, http, db, timeProvider)
            : ReorderProductFamilyMediaByUrlAsync(command, http, db, timeProvider);

    private static async Task<IResult> ReorderProductFamilyMediaByUrlAsync(ReorderProductFamilyMediaCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        var requestedUrls = command.UrlsInOrder ?? [];
        if (requestedUrls.Count > 1000 || requestedUrls.Any(url => string.IsNullOrWhiteSpace(url) || url.Length > 2048))
            return Problem(http, new("PRODUCT_MEDIA_REORDER_INVALID", "En fazla 1000 adet geçerli ürün görseli sıralanabilir.", 422));
        var requestedKeys = requestedUrls.Select(CatalogImageIdentity.Key).ToArray();
        if (requestedKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requestedKeys.Length)
            return Problem(http, new("PRODUCT_MEDIA_REORDER_INVALID", "Görsel sıralama isteğinde aynı görsel birden fazla kez bulunamaz.", 422));

        var sourceProduct = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == command.ProductId, http.RequestAborted);
        if (sourceProduct is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün bulunamadı.", 404));
        var modelCode = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.ProductId == sourceProduct.Id && x.ModelCode != null && x.ModelCode != "")
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.ModelCode)
            .FirstOrDefaultAsync(http.RequestAborted);
        var familyProductIds = await VisibleFamilyProductIdsAsync(db, tenant.TenantId, sourceProduct.BrandId, modelCode, http.RequestAborted);
        if (!familyProductIds.Contains(sourceProduct.Id)) familyProductIds.Add(sourceProduct.Id);

        var media = await db.ProductMedia
            .Where(item => item.TenantId == tenant.TenantId && familyProductIds.Contains(item.ProductId))
            .ToListAsync(http.RequestAborted);
        var assetIds = media.Select(item => item.FileAssetId).Distinct().ToArray();
        var assetsById = await db.FileAssets.AsNoTracking()
            .Where(asset => asset.TenantId == tenant.TenantId && assetIds.Contains(asset.Id))
            .ToDictionaryAsync(asset => asset.Id, http.RequestAborted);
        var visibleMedia = media
            .Where(item => item.Status == "ACTIVE"
                && assetsById.TryGetValue(item.FileAssetId, out var asset)
                && asset.Status == "ACTIVE"
                && asset.Classification is "PRODUCT_MEDIA" or "PRODUCT_MEDIA_URL")
            .Select(item =>
            {
                var asset = assetsById[item.FileAssetId];
                var url = asset.Classification == "PRODUCT_MEDIA_URL"
                    ? asset.RelativePath
                    : $"/api/v1/files/product-media/{asset.Id:D}/content";
                return new ProductFamilyMediaRow(item, url, CatalogImageIdentity.Key(url));
            })
            .ToArray();
        // The editor combines a product gallery with variant-level color images.
        // Order every active scope by the same visible URL list; selecting only
        // product-gallery rows here makes valid variant URLs look stale.
        var selectedRows = visibleMedia
            .OrderBy(item => item.Media.SortOrder).ThenBy(item => item.Media.ProductId).ThenBy(item => item.Media.Id)
            .ToArray();
        var familyGroups = selectedRows
            .GroupBy(item => item.ImageKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProductFamilyImageGroup(group.Key, group.First().Url, group.ToArray(), group.Min(item => item.Media.SortOrder), group.Min(item => item.Media.ProductId), group.Min(item => item.Media.Id)))
            .OrderBy(group => group.SortOrder).ThenBy(group => group.FirstProductId).ThenBy(group => group.FirstMediaId)
            .ToArray();
        var groupKeys = familyGroups.Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedKeys.Any(key => !groupKeys.Contains(key)))
            return Problem(http, new("PRODUCT_MEDIA_REORDER_STALE", "Görsel listesi değişmiş. Sayfayı yenileyip sıralamayı tekrar yapın.", 409));
        var orderedGroups = ProductFamilyMediaOrdering.OrderByRequestedKeys(familyGroups, requestedKeys, group => group.Key);
        if (orderedGroups.Count == 0) return Results.Ok(new { changedProductCount = 0, changedGalleryCount = 0 });

        var stride = (long)selectedRows.Length + 1;
        if ((long)orderedGroups.Count * stride + (long)media.Count * 2 + 1 > int.MaxValue)
            return Problem(http, new("PRODUCT_MEDIA_REORDER_INVALID", "Ürün görsel ailesi sıralanamayacak kadar büyük.", 422));
        var orderByImageKey = orderedGroups.Select((group, index) => (group.Key, Index: index))
            .ToDictionary(item => item.Key, item => item.Index, StringComparer.OrdinalIgnoreCase);
        var selectedByProduct = selectedRows.GroupBy(item => item.Media.ProductId).ToDictionary(group => group.Key, group => group.ToArray());
        var visibleByMediaId = selectedRows.ToDictionary(item => item.Media.Id);
        var planned = new Dictionary<Guid, (int SortOrder, string MediaRole)>();
        void Plan(ProductMedia item, long sortOrder, string? mediaRole = null) => planned[item.Id] = ((int)sortOrder, mediaRole ?? item.MediaRole);

        foreach (var productId in selectedByProduct.Keys)
        {
            var productMedia = media.Where(item => item.ProductId == productId).ToArray();
            foreach (var scope in productMedia.GroupBy(item => item.VariantId))
            {
                var scopeRows = scope.ToArray();
                var activeRows = scopeRows.Where(item => item.Status == "ACTIVE")
                    .OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToArray();
                var orderedVisibleRows = activeRows
                    .Select(item => visibleByMediaId.GetValueOrDefault(item.Id))
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .OrderBy(item => orderByImageKey[item.ImageKey])
                    .ThenBy(item => item.Media.SortOrder).ThenBy(item => item.Media.Id)
                    .ToArray();
                if (orderedVisibleRows.Length == 0) continue;

                var selectedIds = orderedVisibleRows.Select(item => item.Media.Id).ToHashSet();
                var occurrenceByImage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < orderedVisibleRows.Length; index++)
                {
                    var row = orderedVisibleRows[index];
                    occurrenceByImage.TryGetValue(row.ImageKey, out var occurrence);
                    occurrenceByImage[row.ImageKey] = occurrence + 1;
                    var rank = (long)orderByImageKey[row.ImageKey] * stride + occurrence;
                    var role = index == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY";
                    Plan(row.Media, rank, role);
                }
                var hiddenActive = activeRows.Where(item => !selectedIds.Contains(item.Id))
                    .OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToArray();
                var archivedGallery = scopeRows.Where(item => item.Status != "ACTIVE")
                    .OrderBy(item => item.SortOrder).ThenBy(item => item.Id).ToArray();
                var hiddenStart = (long)orderedGroups.Count * stride;
                for (var index = 0; index < hiddenActive.Length; index++)
                    Plan(hiddenActive[index], hiddenStart + index, scope.Key is null ? "ORDERED_GALLERY" : null);
                for (var index = 0; index < archivedGallery.Length; index++)
                    Plan(archivedGallery[index], hiddenStart + hiddenActive.Length + index);
            }
        }

        var plannedIds = planned.Keys.ToHashSet();
        var changedScopes = media
            .Where(item => plannedIds.Contains(item.Id))
            .GroupBy(item => (item.ProductId, item.VariantId))
            .Where(scope => scope.Any(item => item.SortOrder != planned[item.Id].SortOrder || item.MediaRole != planned[item.Id].MediaRole))
            .ToArray();
        if (changedScopes.Length == 0) return Results.Ok(new { changedProductCount = 0, changedGalleryCount = 0 });

        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        foreach (var scope in changedScopes)
        {
            var occupied = scope.Select(item => item.SortOrder).ToHashSet();
            var temporary = -1;
            foreach (var item in scope.OrderBy(item => item.SortOrder).ThenBy(item => item.Id))
            {
                while (occupied.Contains(temporary))
                {
                    if (temporary == int.MinValue) return Problem(http, new("PRODUCT_MEDIA_REORDER_INVALID", "Geçici görsel sıralama alanı bulunamadı.", 422));
                    temporary--;
                }
                item.SortOrder = temporary;
                occupied.Add(temporary);
                if (temporary > int.MinValue) temporary--;
            }
        }
        await db.SaveChangesAsync(http.RequestAborted);

        foreach (var scope in changedScopes)
            foreach (var item in scope)
            {
                item.SortOrder = planned[item.Id].SortOrder;
                item.MediaRole = planned[item.Id].MediaRole;
            }
        var changedProductIds = changedScopes.Select(scope => scope.Key.ProductId).Distinct().ToHashSet();
        var siblingProducts = await db.Products
            .Where(product => product.TenantId == tenant.TenantId && changedProductIds.Contains(product.Id))
            .ToListAsync(http.RequestAborted);
        var now = timeProvider.GetUtcNow();
        await CatalogLocalEdits.MarkMediaEditedAsync(db, tenant.TenantId, siblingProducts, now, http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        return Results.Ok(new { changedProductCount = changedProductIds.Count, changedGalleryCount = changedScopes.Length });
    }

    private static async Task<IResult> ReorderProductFamilyMediaLegacyAsync(ReorderProductFamilyMediaCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        var keyFailure = RequireIdempotency(http); if (keyFailure is not null) return keyFailure;
        if (command.SourceIndex is not int sourceIndex || command.TargetIndex is not int targetIndex || sourceIndex is < 0 or > 999 || targetIndex is < 0 or > 999)
            return Problem(http, new("PRODUCT_MEDIA_REORDER_INVALID", "Görsel sıralama konumları 0-999 arasında olmalıdır.", 422));

        var sourceProduct = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == command.ProductId, http.RequestAborted);
        if (sourceProduct is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Ürün bulunamadı.", 404));
        var modelCode = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.ProductId == sourceProduct.Id && x.ModelCode != null && x.ModelCode != "")
            .Select(x => x.ModelCode)
            .FirstOrDefaultAsync(http.RequestAborted);
        if (string.IsNullOrWhiteSpace(modelCode)) return Results.Ok(new { changedProductCount = 0, changedGalleryCount = 0 });

        var familyProductIds = await VisibleFamilyProductIdsAsync(db, tenant.TenantId, sourceProduct.BrandId, modelCode, http.RequestAborted);
        familyProductIds.Remove(sourceProduct.Id);
        if (familyProductIds.Count == 0) return Results.Ok(new { changedProductCount = 0, changedGalleryCount = 0 });

        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var siblingProducts = await db.Products
            .Where(product => product.TenantId == tenant.TenantId && familyProductIds.Contains(product.Id))
            .ToListAsync(http.RequestAborted);
        var siblingMedia = await db.ProductMedia
            .Where(media => media.TenantId == tenant.TenantId && familyProductIds.Contains(media.ProductId))
            .ToListAsync(http.RequestAborted);

        var galleryScopes = new List<(Guid ProductId, Guid? VariantId, List<ProductMedia> All, List<ProductMedia> Active)>();
        foreach (var productGroup in siblingMedia.GroupBy(media => media.ProductId))
        {
            var productGallery = productGroup.Where(media => media.VariantId is null).ToList();
            var activeProductGallery = productGallery.Where(media => media.Status == "ACTIVE")
                .OrderBy(media => media.SortOrder).ThenBy(media => media.Id).ToList();
            if (activeProductGallery.Count > 0)
            {
                galleryScopes.Add((productGroup.Key, null, productGallery, activeProductGallery));
                continue;
            }

            foreach (var variantGroup in productGroup.Where(media => media.VariantId is not null).GroupBy(media => media.VariantId))
            {
                var all = variantGroup.ToList();
                var active = all.Where(media => media.Status == "ACTIVE")
                    .OrderBy(media => media.SortOrder).ThenBy(media => media.Id).ToList();
                if (active.Count > 0) galleryScopes.Add((productGroup.Key, variantGroup.Key, all, active));
            }
        }

        var changedScopes = new List<(Guid ProductId, Guid? VariantId, List<ProductMedia> All, List<ProductMedia> Active, List<ProductMedia> Archived)>();
        foreach (var scope in galleryScopes)
        {
            var originalOrder = scope.Active.ToArray();
            if (!ProductFamilyMediaOrdering.Move(scope.Active, sourceIndex, targetIndex)
                || originalOrder.SequenceEqual(scope.Active)) continue;
            var archived = scope.All.Where(media => media.Status != "ACTIVE")
                .OrderBy(media => media.SortOrder).ThenBy(media => media.Id).ToList();
            changedScopes.Add((scope.ProductId, scope.VariantId, scope.All, scope.Active, archived));
        }

        if (changedScopes.Count == 0)
        {
            await transaction.CommitAsync(http.RequestAborted);
            return Results.Ok(new { changedProductCount = 0, changedGalleryCount = 0 });
        }

        foreach (var scope in changedScopes)
        {
            var occupied = scope.All.Select(media => media.SortOrder).ToHashSet();
            var temporary = int.MinValue;
            foreach (var media in scope.All.OrderBy(media => media.SortOrder).ThenBy(media => media.Id))
            {
                while (occupied.Contains(temporary)) temporary++;
                media.SortOrder = temporary;
                occupied.Add(temporary++);
            }
        }
        await db.SaveChangesAsync(http.RequestAborted);

        foreach (var scope in changedScopes)
        {
            for (var index = 0; index < scope.Active.Count; index++)
            {
                scope.Active[index].SortOrder = index;
                scope.Active[index].MediaRole = scope.VariantId is null
                    ? index == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY"
                    : index == 0 ? "PRIMARY" : "GALLERY";
            }
            for (var index = 0; index < scope.Archived.Count; index++)
                scope.Archived[index].SortOrder = scope.Active.Count + index;
        }
        var changedProductIds = changedScopes.Select(scope => scope.ProductId).Distinct().ToHashSet();
        var now = timeProvider.GetUtcNow();
        await CatalogLocalEdits.MarkMediaEditedAsync(db, tenant.TenantId, siblingProducts.Where(product => changedProductIds.Contains(product.Id)).ToList(), now, http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        return Results.Ok(new { changedProductCount = changedProductIds.Count, changedGalleryCount = changedScopes.Count });
    }
}
