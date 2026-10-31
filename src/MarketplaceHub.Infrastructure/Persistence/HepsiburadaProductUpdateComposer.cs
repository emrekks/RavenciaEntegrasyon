using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed class HepsiburadaProductUpdateComposer(AppDbContext db, IConfiguration configuration)
{
    // sections: Hepsiburada is the product package (ticket-api), Delivery the dispatch time (listing shipping-info upload);
    // null is both.
    public async Task<ServiceResult<ProductUpdateDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken, ProductUpdateSections? sections = null)
    {
        sections ??= ProductUpdateSections.All;
        var create = await new ProductPublicationComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (!create.Succeeded) return ServiceResult<ProductUpdateDraft>.Fail(create.Error!.Code, create.Error.Message, create.Error.Status, create.Error.FieldErrors);
        var draft = create.Value!;
        var merchantId = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && connection.Id == connectionId && connection.PlatformCode == "HEPSIBURADA")
            .Select(connection => connection.ExternalStoreId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(merchantId)) return ServiceResult<ProductUpdateDraft>.Fail("HEPSIBURADA_CONNECTION_REQUIRED", "Hepsiburada bağlantısı bulunamadı.", 404);

        var variantIds = draft.Variants.Select(variant => variant.VariantId).ToArray();
        var remoteLinks = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(link => link.TenantId == tenantId && link.ConnectionId == connectionId && variantIds.Contains(link.VariantId))
            .ToDictionaryAsync(link => link.VariantId, link => link.ExternalId, cancellationToken);
        if (draft.Variants.Any(variant => !remoteLinks.TryGetValue(variant.VariantId, out var hbSku) || string.IsNullOrWhiteSpace(hbSku)))
            return ServiceResult<ProductUpdateDraft>.Fail("HEPSIBURADA_HB_SKU_REQUIRED", "Güncelleme için her varyantın Hepsiburada hbSku bağlantısı katalog okumasıyla doğrulanmalıdır.", 409);
        if (remoteLinks.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != draft.Variants.Count)
            return ServiceResult<ProductUpdateDraft>.Fail("HEPSIBURADA_HB_SKU_AMBIGUOUS", "Hepsiburada hbSku bağlantıları yineleniyor; güncelleme durduruldu.", 409);

        using var source = JsonDocument.Parse(draft.PayloadJson);
        if (source.RootElement.ValueKind != JsonValueKind.Array || source.RootElement.GetArrayLength() != draft.Variants.Count)
            return ServiceResult<ProductUpdateDraft>.Fail("PRODUCT_UPDATE_PAYLOAD_INVALID", "Hepsiburada ürün güncelleme kaynak satırları varyantlarla eşleşmiyor.", 422);

        var items = new List<Dictionary<string, object?>>(draft.Variants.Count);
        var sourceBySku = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source.RootElement.EnumerateArray())
        {
            var attributes = ReadAttributes(item);
            var merchantSku = attributes.TryGetValue("merchantSku", out var rawSku) && rawSku.ValueKind == JsonValueKind.String ? rawSku.GetString() : null;
            if (string.IsNullOrWhiteSpace(merchantSku) || !sourceBySku.TryAdd(merchantSku, item))
                return ServiceResult<ProductUpdateDraft>.Fail("PRODUCT_UPDATE_SKU_INVALID", "Hepsiburada ürün güncelleme satırlarında merchantSku eksik veya yineleniyor.", 422);
        }

        foreach (var variant in draft.Variants)
        {
            if (!sourceBySku.TryGetValue(variant.Sku, out var sourceItem))
                return ServiceResult<ProductUpdateDraft>.Fail("PRODUCT_UPDATE_SKU_MISMATCH", "Hepsiburada ürün güncelleme merchantSku satırı yerel varyantla eşleşmiyor.", 422);
            var attributes = ReadAttributes(sourceItem);
            var updateItem = new Dictionary<string, object?>
            {
                ["hbSku"] = remoteLinks[variant.VariantId],
                ["merchantSku"] = variant.Sku,
                ["productName"] = String(attributes, "UrunAdi"),
                ["productDescription"] = String(attributes, "UrunAciklamasi"),
                ["barcode"] = String(attributes, "Barcode"),
                ["attributes"] = attributes.Where(pair => !HepsiburadaProductPublicationComposer.ReservedAttributeKeys.Contains(pair.Key) && !pair.Key.StartsWith("Image", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Clone(), StringComparer.Ordinal),
                ["kdv"] = String(attributes, "tax_vat_rate"),
                ["warrantyPeriod"] = String(attributes, "GarantiSuresi"),
                ["desi"] = String(attributes, "kg")
            };
            for (var imageIndex = 1; imageIndex <= 10; imageIndex++)
            {
                var image = String(attributes, $"Image{imageIndex}");
                if (!string.IsNullOrWhiteSpace(image)) updateItem[$"image{imageIndex}"] = image;
            }
            if (!string.IsNullOrWhiteSpace(String(attributes, "Video1"))) updateItem["video"] = String(attributes, "Video1");
            items.Add(updateItem);
        }

        var noItems = "{\"items\":[]}";
        var empty = "{}";
        // The product package goes only when one of its fields was picked; a pick of the dispatch time alone sends none.
        var payloadJson = sections.Hepsiburada ? JsonSerializer.Serialize(new { merchantId, items }) : null;
        string? dispatchJson = null;
        if (sections.Delivery)
        {
            // Teslimat Süresi: the listing's own override, else the product card's days (as Trendyol's deliveryDuration).
            var listingDays = await db.ChannelListingProfiles.AsNoTracking().Where(profile => profile.TenantId == tenantId && profile.Id == draft.ProfileId).Select(profile => profile.DeliveryTimeDays).SingleOrDefaultAsync(cancellationToken);
            var productDays = await db.Products.AsNoTracking().Where(product => product.TenantId == tenantId && product.Id == productId).Select(product => product.DeliveryDays).SingleOrDefaultAsync(cancellationToken);
            dispatchJson = DispatchTimePayload(merchantId, draft.Variants.Select(variant => (remoteLinks[variant.VariantId], variant.Sku)), ProductPublicationComposer.DeliveryDays(listingDays, productDays));
        }
        if (payloadJson is null && dispatchJson is null)
            return ServiceResult<ProductUpdateDraft>.Fail("PRODUCT_UPDATE_NOTHING_TO_SEND", "Seçilen ürün bilgileri için Hepsiburada'ya gönderilecek değer yok (ürün kartında teslimat süresi yok).", 422);
        var productLinkExists = await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(link => link.TenantId == tenantId && link.ConnectionId == connectionId && link.ProductId == productId, cancellationToken);
        var mode = productLinkExists ? "APPROVED" : "UNAPPROVED";
        var update = new ProductUpdatePublication(productId, mode, Hash(string.Join("\n", payloadJson ?? "", dispatchJson ?? "")),
            mode == "UNAPPROVED" ? payloadJson ?? empty : empty, mode == "APPROVED" ? payloadJson ?? noItems : noItems, noItems, dispatchJson ?? noItems);
        return ServiceResult<ProductUpdateDraft>.Ok(new(draft.ProfileId, update, draft.Variants));
    }

    // The listing shipping-info upload (POST listings/merchantid/{merchantId}/shipping-info-uploads) per listing:
    // hepsiburadaSku, merchantSku and dispatchTime (whole days to hand the order to the carrier). Every other field of that
    // request (cargo companies, addresses, shipping profile) is left out, never sent empty. Null when the card holds no
    // days: an unknown dispatch time is not sent.
    internal static string? DispatchTimePayload(string merchantId, IEnumerable<(string HepsiburadaSku, string MerchantSku)> listings, int? days)
    {
        if (days is not > 0) return null;
        var items = listings.Select(listing => new Dictionary<string, object> { ["hepsiburadaSku"] = listing.HepsiburadaSku, ["merchantSku"] = listing.MerchantSku, ["dispatchTime"] = days.Value }).ToList();
        return items.Count == 0 ? null : JsonSerializer.Serialize(new { merchantId, items });
    }

    private static Dictionary<string, JsonElement> ReadAttributes(JsonElement item)
    {
        if (!item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            return new(StringComparer.OrdinalIgnoreCase);
        return attributes.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string? String(IReadOnlyDictionary<string, JsonElement> attributes, string key) =>
        attributes.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
