using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record ProductUpdateDraft(Guid ProfileId, ProductUpdatePublication Publication, IReadOnlyList<PublicationVariantDraft> Variants);
internal sealed record ProductArchiveDraft(Guid ProfileId, string PayloadHash, string PayloadJson, IReadOnlyList<PublicationVariantDraft> Variants);
internal sealed record PriceInventoryDraft(string PayloadHash, string PayloadJson, IReadOnlyList<PriceInventoryPushLine> Lines);

internal sealed class ProductUpdateComposer(AppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ServiceResult<ProductUpdateDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var create = await new ProductPublicationComposer(db).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (!create.Succeeded) return ServiceResult<ProductUpdateDraft>.Fail(create.Error!.Code, create.Error.Message, create.Error.Status, create.Error.FieldErrors);
        var draft = create.Value!;
        var productLink = await db.MarketplaceProductLinks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == productId, cancellationToken);
        var mode = productLink is null ? "UNAPPROVED" : "APPROVED";

        using var source = JsonDocument.Parse(draft.PayloadJson);
        if (!source.RootElement.TryGetProperty("items", out var sourceItems) || sourceItems.ValueKind != JsonValueKind.Array || sourceItems.GetArrayLength() == 0)
            return ServiceResult<ProductUpdateDraft>.Fail("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün güncelleme payload'ı oluşturulamadı.", 422);

        var unapprovedPayload = draft.PayloadJson;
        var contentItems = new List<Dictionary<string, object?>>();
        var variantItems = new List<Dictionary<string, object?>>();
        var deliveryItems = new List<Dictionary<string, object?>>();

        if (mode == "APPROVED")
        {
            if (!long.TryParse(productLink!.ExternalId, out var contentId)) return ServiceResult<ProductUpdateDraft>.Fail("REMOTE_CONTENT_ID_INVALID", "Trendyol contentId sayısal değil; otomatik güncelleme güvenli biçimde durduruldu.", 409);
            var first = sourceItems[0];
            contentItems.Add(new Dictionary<string, object?>
            {
                ["contentId"] = contentId,
                ["title"] = Clone(first, "title"),
                ["description"] = Clone(first, "description"),
                ["images"] = Clone(first, "images"),
                ["attributes"] = Clone(first, "attributes")
            });

            foreach (var item in sourceItems.EnumerateArray())
            {
                variantItems.Add(new Dictionary<string, object?>
                {
                    ["barcode"] = Text(item, "barcode"),
                    ["stockCode"] = Text(item, "stockCode"),
                    ["origin"] = Clone(item, "origin"),
                    ["vatRate"] = Clone(item, "vatRate"),
                });
                if (item.TryGetProperty("deliveryOption", out var delivery) && delivery.ValueKind == JsonValueKind.Object)
                {
                    deliveryItems.Add(new Dictionary<string, object?>
                    {
                        ["barcode"] = Text(item, "barcode"),
                        ["deliveryOptions"] = delivery.Clone()
                    });
                }
            }
        }

        var contentPayload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["items"] = contentItems }, JsonOptions);
        var variantPayload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["items"] = variantItems }, JsonOptions);
        var deliveryPayload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["items"] = deliveryItems }, JsonOptions);
        var canonical = string.Join("\n", mode, unapprovedPayload, contentPayload, variantPayload, deliveryPayload);
        var hash = Hash(canonical);
        return ServiceResult<ProductUpdateDraft>.Ok(new(draft.ProfileId, new(productId, mode, hash, unapprovedPayload, contentPayload, variantPayload, deliveryPayload), draft.Variants));
    }

    private static object? Clone(JsonElement source, string name) => source.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.Clone() : null;
    private static string Text(JsonElement source, string name) => source.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed class ProductArchiveComposer(AppDbContext db)
{
    public async Task<ServiceResult<ProductArchiveDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, bool archived, CancellationToken cancellationToken)
    {
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return ServiceResult<ProductArchiveDraft>.Fail("LISTING_PROFILE_REQUIRED", "Trendyol arşiv işlemi için listing profile gerekir.", 422);
        var variants = await (from listing in db.ChannelListingVariants.AsNoTracking()
                              join variant in db.ProductVariants.AsNoTracking() on new { listing.TenantId, listing.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                              where listing.TenantId == tenantId && listing.ProfileId == profile.Id && listing.ExternalBarcode != null
                              orderby variant.Id
                              select new PublicationVariantDraft(variant.Id, variant.Sku, listing.ExternalBarcode!)).ToListAsync(cancellationToken);
        if (variants.Count == 0) return ServiceResult<ProductArchiveDraft>.Fail("REMOTE_BARCODE_REQUIRED", "Arşiv işlemi için en az bir eşlenmiş Trendyol barkodu gerekir.", 422);
        if (variants.Count > 1000) return ServiceResult<ProductArchiveDraft>.Fail("PRODUCT_BATCH_LIMIT_EXCEEDED", "Tek arşiv isteğinde en fazla 1000 barkod gönderilebilir.", 422);
        var payload = JsonSerializer.Serialize(new { items = variants.Select(x => new { barcode = x.Barcode, archived }).ToArray() });
        return ServiceResult<ProductArchiveDraft>.Ok(new(profile.Id, Hash(payload), payload, variants));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed class PriceInventoryComposer(AppDbContext db)
{
    public async Task<ServiceResult<PriceInventoryDraft>> BuildAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var rows = await (from offer in db.ChannelOffers.AsNoTracking()
                          join variant in db.ProductVariants.AsNoTracking() on new { offer.TenantId, offer.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                          join listingState in db.MarketplaceListingStates.AsNoTracking() on new { offer.TenantId, offer.ConnectionId, offer.VariantId } equals new { listingState.TenantId, listingState.ConnectionId, listingState.VariantId }
                          join profile in db.ChannelListingProfiles.AsNoTracking() on new { offer.TenantId, offer.ConnectionId, ProductId = variant.ProductId } equals new { profile.TenantId, profile.ConnectionId, profile.ProductId }
                          join listingVariant in db.ChannelListingVariants.AsNoTracking() on new { profile.TenantId, ProfileId = profile.Id, VariantId = variant.Id } equals new { listingVariant.TenantId, listingVariant.ProfileId, listingVariant.VariantId }
                          join inventory in db.InventoryItems.AsNoTracking().Where(x => x.LocationCode == "MAIN") on new { offer.TenantId, offer.VariantId } equals new { inventory.TenantId, inventory.VariantId }
                          where offer.TenantId == tenantId && offer.ConnectionId == connectionId && offer.Status == "ACTIVE" && listingState.ActualStatus == "LIVE" && listingVariant.ExternalBarcode != null
                          orderby variant.Id
                          select new { Offer = offer, Variant = variant, Inventory = inventory, Barcode = listingVariant.ExternalBarcode! }).ToListAsync(cancellationToken);
        if (rows.Count == 0) return ServiceResult<PriceInventoryDraft>.Fail("LIVE_OFFER_REQUIRED", "Fiyat-stok gönderimi için LIVE eşleşmiş varyant ve ACTIVE teklif gerekir.", 422);
        if (rows.Count > 1000) return ServiceResult<PriceInventoryDraft>.Fail("PRICE_INVENTORY_BATCH_LIMIT_EXCEEDED", "Tek fiyat-stok isteğinde en fazla 1000 varyant gönderilebilir.", 422);

        var lines = new List<PriceInventoryPushLine>();
        foreach (var row in rows)
        {
            if (!string.Equals(row.Offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || row.Offer.SalePrice <= 0 || row.Offer.ListPrice < row.Offer.SalePrice)
                return ServiceResult<PriceInventoryDraft>.Fail("CHANNEL_OFFER_INVALID", $"'{row.Variant.Sku}' için TRY ve listPrice >= salePrice > 0 kuralı gerekir.", 422);
            var publishable = Math.Max(0, row.Inventory.Available - row.Offer.SafetyStock);
            var quantity = decimal.Floor(publishable);
            var priceHash = Hash($"{row.Offer.ListPrice:0.####}|{row.Offer.SalePrice:0.####}|TRY|{row.Offer.PriceVersion}");
            if (row.Offer.LastPriceHash == priceHash && row.Offer.LastStockProjectionVersion == row.Inventory.ProjectionVersion) continue;
            lines.Add(new(row.Variant.Id, row.Offer.Id, row.Barcode.Trim(), quantity, row.Offer.ListPrice, row.Offer.SalePrice, "TRY", row.Inventory.ProjectionVersion, row.Offer.PriceVersion, priceHash));
        }
        if (lines.Count == 0) return ServiceResult<PriceInventoryDraft>.Fail("NO_EXTERNAL_CHANGES", "Trendyol'a gönderilecek yeni fiyat veya stok değişikliği yok.", 409);
        var payload = JsonSerializer.Serialize(new { items = lines.Select(x => new { barcode = x.Barcode, quantity = checked((int)Math.Min((decimal)int.MaxValue, x.Quantity)), salePrice = x.SalePrice, listPrice = x.ListPrice }).ToArray() });
        return ServiceResult<PriceInventoryDraft>.Ok(new(Hash(payload), payload, lines));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
