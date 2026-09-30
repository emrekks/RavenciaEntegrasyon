using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed class HepsiburadaProductUpdateComposer(AppDbContext db, IConfiguration configuration)
{
    public async Task<ServiceResult<ProductUpdateDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
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
                ["attributes"] = attributes.Where(pair => pair.Key.StartsWith("attribute-", StringComparison.OrdinalIgnoreCase))
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

        var payloadJson = JsonSerializer.Serialize(new { merchantId, items });
        var productLinkExists = await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(link => link.TenantId == tenantId && link.ConnectionId == connectionId && link.ProductId == productId, cancellationToken);
        var mode = productLinkExists ? "APPROVED" : "UNAPPROVED";
        var noItems = "{\"items\":[]}";
        var empty = "{}";
        var update = new ProductUpdatePublication(productId, mode, Hash(payloadJson), mode == "UNAPPROVED" ? payloadJson : empty, mode == "APPROVED" ? payloadJson : noItems, noItems, noItems);
        return ServiceResult<ProductUpdateDraft>.Ok(new(draft.ProfileId, update, draft.Variants));
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
