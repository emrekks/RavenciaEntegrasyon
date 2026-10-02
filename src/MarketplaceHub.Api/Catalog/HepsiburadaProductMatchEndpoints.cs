using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Catalog;

internal static class HepsiburadaProductMatchEndpoints
{
    public static RouteGroupBuilder MapHepsiburadaProductMatchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/connections/{connectionId:guid}/product-matches", ReadPendingAsync);
        api.MapPost("/connections/{connectionId:guid}/product-matches/review", ReviewAsync);
        return api;
    }

    private static async Task<IResult> ReadPendingAsync(
        Guid connectionId,
        int? page,
        int? limit,
        HttpContext http,
        AppDbContext db,
        IHepsiburadaProductMatchPort matches,
        TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Problem(401, "AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.");
        if (page is < 0 or > 100_000) return Problem(400, "PAGE_INVALID", "page 0 ile 100000 arasında olmalıdır.");
        if (limit is not null and (< 1 or > 100)) return Problem(400, "LIMIT_INVALID", "limit 1 ile 100 arasında olmalıdır.");
        var connection = await GetHepsiburadaConnectionAsync(db, tenant.TenantId, connectionId, http.RequestAborted);
        if (connection is null) return Problem(404, "HEPSIBURADA_CONNECTION_NOT_FOUND", "Etkin veya doğrulanmış Hepsiburada bağlantısı bulunamadı.");

        var pageNumber = page ?? 0;
        var result = await matches.ListPendingProductMatchesAsync(
            new(tenant.TenantId, connectionId, http.TraceIdentifier, $"hepsi-match-read:{pageNumber}", timeProvider.GetUtcNow().AddSeconds(30)),
            new(pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), limit ?? 50),
            http.RequestAborted);
        if (!result.IsSuccess) return AdapterProblem(result.Error);

        var remoteItems = result.Value!.Items;
        var merchantSkus = remoteItems.Select(item => item.MerchantSku).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var barcodes = remoteItems.Select(item => item.Barcode).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var exactMatches = merchantSkus.Length == 0
            ? []
            : await (from listing in db.ChannelListingVariants.AsNoTracking()
                     join profile in db.ChannelListingProfiles.AsNoTracking() on new { listing.TenantId, listing.ProfileId } equals new { profile.TenantId, ProfileId = profile.Id }
                     join variant in db.ProductVariants.AsNoTracking() on new { listing.TenantId, listing.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                     join product in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { product.TenantId, ProductId = product.Id }
                     where listing.TenantId == tenant.TenantId && profile.ConnectionId == connectionId && listing.ExternalSku != null && merchantSkus.Contains(listing.ExternalSku)
                     select new CandidateRow(listing.ExternalSku!, product.Id, profile.TitleOverride ?? product.Title, variant.Sku, variant.Barcode, product.BrandId, "MERCHANT_SKU"))
                .ToListAsync(http.RequestAborted);
        var barcodeMatches = barcodes.Length == 0
            ? []
            : await (from variant in db.ProductVariants.AsNoTracking()
                     join product in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { product.TenantId, ProductId = product.Id }
                     where variant.TenantId == tenant.TenantId && variant.Barcode != null && barcodes.Contains(variant.Barcode)
                     select new CandidateRow(variant.Barcode!, product.Id, product.Title, variant.Sku, variant.Barcode, product.BrandId, "BARCODE"))
                .ToListAsync(http.RequestAborted);
        var brandIds = exactMatches.Concat(barcodeMatches).Where(row => row.BrandId is not null).Select(row => row.BrandId!.Value).Distinct().ToArray();
        var brands = brandIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Brands.AsNoTracking().Where(brand => brand.TenantId == tenant.TenantId && brandIds.Contains(brand.Id)).ToDictionaryAsync(brand => brand.Id, brand => brand.Name, http.RequestAborted);
        var exactBySku = exactMatches.GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Select(row => MapCandidate(row, brands)).ToArray(), StringComparer.OrdinalIgnoreCase);
        var suggestionsByBarcode = barcodeMatches.GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Select(row => MapCandidate(row, brands)).ToArray(), StringComparer.OrdinalIgnoreCase);

        return Results.Ok(new
        {
            items = remoteItems.Select(remote => new
            {
                remote = new { remote.MerchantSku, remote.Status, remote.HepsiburadaSku, remote.ProductName, remote.BrandName, remote.ImageUrls, remote.Barcode },
                exactMatches = exactBySku.GetValueOrDefault(remote.MerchantSku, []),
                barcodeSuggestions = string.IsNullOrWhiteSpace(remote.Barcode) ? [] : suggestionsByBarcode.GetValueOrDefault(remote.Barcode.Trim(), [])
            }),
            result.Value.NextCursor,
            result.Value.HasMore,
            result.Value.TotalCount
        });
    }

    private static async Task<IResult> ReviewAsync(
        Guid connectionId,
        ReviewRequest request,
        HttpContext http,
        AppDbContext db,
        IHepsiburadaProductMatchPort matches,
        TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Problem(401, "AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.");
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (idempotencyKey.Length is 0 or > 256) return Problem(400, "IDEMPOTENCY_KEY_INVALID", "1 ile 256 karakter arasında Idempotency-Key başlığı gereklidir.");
        if (request.Page is < 0 or > 100_000) return Problem(400, "PAGE_INVALID", "page 0 ile 100000 arasında olmalıdır.");
        if (request.Limit is < 1 or > 100) return Problem(400, "LIMIT_INVALID", "limit 1 ile 100 arasında olmalıdır.");
        if (request.MerchantSkus is null || request.MerchantSkus.Count is < 1 or > 100 || request.MerchantSkus.Any(sku => string.IsNullOrWhiteSpace(sku) || sku != sku.Trim() || sku.Length > 128 || sku.Any(char.IsWhiteSpace)) || request.MerchantSkus.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.MerchantSkus.Count)
            return Problem(422, "PRODUCT_MATCH_SKUS_INVALID", "1-100 benzersiz, boşluksuz merchantSku seçilmelidir.");

        var connection = await GetHepsiburadaConnectionAsync(db, tenant.TenantId, connectionId, http.RequestAborted);
        if (connection is null) return Problem(404, "HEPSIBURADA_CONNECTION_NOT_FOUND", "Etkin veya doğrulanmış Hepsiburada bağlantısı bulunamadı.");

        var payloadIdentity = string.Join('\n', request.MerchantSkus.Order(StringComparer.OrdinalIgnoreCase));
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadIdentity)))[..16];
        var effectType = $"HEPSIBURADA_PRODUCT_MATCH:{connectionId:N}:{(request.Approve ? "APPROVE" : "REJECT")}:{payloadHash}";
        var existing = await db.ExternalEffectRecords.SingleOrDefaultAsync(effect => effect.TenantId == tenant.TenantId && effect.EffectType == effectType && effect.IdempotencyKey == idempotencyKey, http.RequestAborted);
        if (existing is not null)
            return existing.CompletedAt is null
                ? Problem(409, "EXTERNAL_EFFECT_AMBIGUOUS", "Önceki kararın sonucu kesinleşmedi. Hepsiburada durumunu kontrol etmeden aynı ürünleri yeniden göndermeyin.")
                : Results.Ok(new { succeeded = true, alreadyCompleted = true, reviewedCount = request.MerchantSkus.Count });

        // Re-read the same remote page immediately before a decision so stale UI selections cannot approve another SKU.
        var context = new AdapterContext(tenant.TenantId, connectionId, http.TraceIdentifier, idempotencyKey, timeProvider.GetUtcNow().AddSeconds(30));
        var currentPage = await matches.ListPendingProductMatchesAsync(context, new(request.Page.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Limit), http.RequestAborted);
        if (!currentPage.IsSuccess) return AdapterProblem(currentPage.Error);
        var currentSkus = currentPage.Value!.Items.Select(item => item.MerchantSku).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.MerchantSkus.Any(sku => !currentSkus.Contains(sku))) return Problem(409, "PRODUCT_MATCH_SELECTION_STALE", "Seçili ürünlerden biri artık bu eşleşme sayfasında beklemiyor. Listeyi yenileyip tekrar deneyin.");

        var effectRecord = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, EffectType = effectType, IdempotencyKey = idempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
        db.ExternalEffectRecords.Add(effectRecord);
        foreach (var merchantSku in request.MerchantSkus)
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenant.TenantId,
                ActorUserId = tenant.UserId,
                Action = request.Approve ? "HEPSIBURADA_PRODUCT_MATCH_APPROVE_REQUESTED" : "HEPSIBURADA_PRODUCT_MATCH_REJECT_REQUESTED",
                TargetType = "HEPSIBURADA_PRODUCT_MATCH",
                TargetId = merchantSku,
                Reason = "Manual Hepsiburada product match review requested.",
                CorrelationId = http.TraceIdentifier,
                CreatedAt = timeProvider.GetUtcNow()
            });
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Problem(409, "EXTERNAL_EFFECT_AMBIGUOUS", "Bu karar isteği başka bir işlemde ele alınıyor. Durumu kontrol edin.");
        }

        var decision = await matches.ReviewProductMatchesAsync(context, request.MerchantSkus, request.Approve, http.RequestAborted);
        if (!decision.IsSuccess)
        {
            if (decision.Error?.Class is not (AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.RateLimit or AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug))
            {
                db.ExternalEffectRecords.Remove(effectRecord);
                await db.SaveChangesAsync(http.RequestAborted);
            }
            return AdapterProblem(decision.Error);
        }

        effectRecord.CompletedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { succeeded = true, alreadyCompleted = false, reviewedCount = request.MerchantSkus.Count });
    }

    private static async Task<PlatformConnection?> GetHepsiburadaConnectionAsync(AppDbContext db, Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(connection => connection.TenantId == tenantId && connection.Id == connectionId && connection.PlatformCode == "HEPSIBURADA" && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"), cancellationToken);

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;

    private static object MapCandidate(CandidateRow row, IReadOnlyDictionary<Guid, string> brands) => new
    {
        row.ProductId,
        row.Title,
        BrandName = row.BrandId is { } brandId ? brands.GetValueOrDefault(brandId) : null,
        row.Sku,
        row.Barcode,
        row.MatchReason
    };

    private static IResult AdapterProblem(AdapterError? error)
    {
        var status = error?.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502;
        return Results.Json(new { type = $"https://marketplacehub.invalid/problems/{(error?.Code ?? "ADAPTER_ERROR").ToLowerInvariant().Replace('_', '-')}", title = error?.SafeMessage ?? "Pazaryeri yanıtı alınamadı.", status, code = error?.Code ?? "ADAPTER_ERROR", retryable = error?.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.RateLimit }, statusCode: status, contentType: "application/problem+json");
    }

    private static IResult Problem(int status, string code, string message) => Results.Json(new { type = $"https://marketplacehub.invalid/problems/{code.ToLowerInvariant().Replace('_', '-')}", title = message, status, code, retryable = false }, statusCode: status, contentType: "application/problem+json");

    private sealed record CandidateRow(string Key, Guid ProductId, string Title, string Sku, string? Barcode, Guid? BrandId, string MatchReason);
    private sealed record ReviewRequest(int Page, int Limit, bool Approve, IReadOnlyList<string> MerchantSkus);
}
