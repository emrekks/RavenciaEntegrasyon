using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record ProductUpdateDraft(Guid ProfileId, ProductUpdatePublication Publication, IReadOnlyList<PublicationVariantDraft> Variants);
internal sealed record ProductArchiveDraft(Guid ProfileId, string PayloadHash, string PayloadJson, IReadOnlyList<PublicationVariantDraft> Variants);
internal sealed record PriceInventoryDraft(string PayloadHash, string PayloadJson, IReadOnlyList<PriceInventoryPushLine> Lines)
{
    // Active offers left out because their variant has no MAIN stock row.
    public IReadOnlyList<string> MissingInventorySkus { get; init; } = [];
}

internal sealed class ProductUpdateComposer(AppDbContext db, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ServiceResult<ProductUpdateDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            return await new HepsiburadaProductUpdateComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        var create = await new ProductPublicationComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
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
                    ["dimensionalWeight"] = Clone(item, "dimensionalWeight"),
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
    private const int MaximumBatchSize = 1000;

    public async Task<ServiceResult<PriceInventoryDraft>> BuildAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, Guid? variantId = null, Guid? productId = null)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.Id == connectionId)
            .Select(item => new { item.PlatformCode, item.ExternalStoreId })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null) return ServiceResult<PriceInventoryDraft>.Fail("CONNECTION_NOT_FOUND", "Pazaryeri bağlantısı bulunamadı.", 404);
        if (string.Equals(connection.PlatformCode, "N11", StringComparison.OrdinalIgnoreCase))
            return await BuildN11Async(tenantId, connectionId, cancellationToken, variantId, productId);
        if (string.Equals(connection.PlatformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase))
            return await BuildPazaramaAsync(tenantId, connectionId, cancellationToken, variantId, productId);
        if (string.Equals(connection.PlatformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase))
            return await BuildPttAvmAsync(tenantId, connectionId, cancellationToken, variantId, productId);
        var isHepsiburada = string.Equals(connection.PlatformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var isTrendyol = string.Equals(connection.PlatformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase);
        var rows = await (from offer in db.ChannelOffers.AsNoTracking()
                          join variant in db.ProductVariants.AsNoTracking() on new { offer.TenantId, offer.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                          join listingState in db.MarketplaceListingStates.AsNoTracking() on new { offer.TenantId, offer.ConnectionId, offer.VariantId } equals new { listingState.TenantId, listingState.ConnectionId, listingState.VariantId }
                          join profile in db.ChannelListingProfiles.AsNoTracking() on new { offer.TenantId, offer.ConnectionId, ProductId = variant.ProductId } equals new { profile.TenantId, profile.ConnectionId, profile.ProductId }
                          join listingVariant in db.ChannelListingVariants.AsNoTracking() on new { profile.TenantId, ProfileId = profile.Id, VariantId = variant.Id } equals new { listingVariant.TenantId, listingVariant.ProfileId, listingVariant.VariantId }
                          join remoteLink in db.MarketplaceVariantLinks.AsNoTracking().Where(link => link.TenantId == tenantId && link.ConnectionId == connectionId)
                              on new { offer.TenantId, offer.ConnectionId, offer.VariantId } equals new { remoteLink.TenantId, remoteLink.ConnectionId, remoteLink.VariantId } into remoteLinks
                          from remoteLink in remoteLinks.DefaultIfEmpty()
                          join inventory in db.InventoryItems.AsNoTracking().Where(x => x.LocationCode == "MAIN") on new { offer.TenantId, offer.VariantId } equals new { inventory.TenantId, inventory.VariantId } into inventories
                          from inventory in inventories.DefaultIfEmpty()
                          where offer.TenantId == tenantId && offer.ConnectionId == connectionId && offer.Status == "ACTIVE" && listingState.ActualStatus == "LIVE" && listingVariant.ExternalBarcode != null
                               && (variantId == null || offer.VariantId == variantId)
                               && (productId == null || variant.ProductId == productId)
                          orderby variant.Id
                          select new { Offer = offer, Variant = variant, Inventory = inventory, Barcode = listingVariant.ExternalBarcode!, MerchantSku = listingVariant.ExternalSku, HepsiburadaSku = remoteLink == null ? null : remoteLink.ExternalId }).ToListAsync(cancellationToken);
        if (rows.Count == 0) return ServiceResult<PriceInventoryDraft>.Fail("LIVE_OFFER_REQUIRED", "Fiyat-stok gönderimi için LIVE eşleşmiş varyant ve ACTIVE teklif gerekir.", 422);
        var missingInventory = rows.Where(row => row.Inventory is null).Select(row => row.Variant.Sku).ToList();
        if (missingInventory.Count == rows.Count) return MissingInventory(missingInventory);

        var lines = new List<PriceInventoryPushLine>();
        foreach (var row in rows.Where(row => row.Inventory is not null))
        {
            var salePrice = OutboundSalePrice(row.Offer);
            var listPrice = OutboundListPrice(row.Offer);
            if (!string.Equals(row.Offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || salePrice <= 0 || row.Offer.ListPrice < row.Offer.SalePrice)
                return ServiceResult<PriceInventoryDraft>.Fail("CHANNEL_OFFER_INVALID", $"'{row.Variant.Sku}' için TRY ve listPrice >= salePrice > 0 kuralı gerekir.", 422);
            if (isHepsiburada && (string.IsNullOrWhiteSpace(row.MerchantSku) || !string.Equals(row.MerchantSku, row.Variant.Sku, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(row.HepsiburadaSku)))
                return ServiceResult<PriceInventoryDraft>.Fail("HEPSIBURADA_LISTING_LINK_REQUIRED", $"'{row.Variant.Sku}' için doğrulanmış merchantSku ve hbSku eşleştirmesi gerekir.", 409);
            var publishable = Math.Max(0, row.Inventory!.Available - row.Offer.SafetyStock);
            // C10: Trendyol stock is capped at 20,000; the capped quantity is what the payload (and its hash) carries.
            var quantity = isTrendyol ? TrendyolStockRules.Quantity(publishable) : decimal.Floor(publishable);
            var priceHash = StandardPriceHash(listPrice, salePrice, row.Offer.PriceVersion);
            if (row.Offer.LastPriceHash == priceHash && row.Offer.LastStockProjectionVersion == row.Inventory.ProjectionVersion) continue;
            lines.Add(new(row.Variant.Id, row.Offer.Id, row.Barcode.Trim(), quantity, listPrice, salePrice, "TRY", row.Inventory.ProjectionVersion, row.Offer.PriceVersion, priceHash, row.MerchantSku?.Trim(), row.HepsiburadaSku?.Trim()));
        }
        if (lines.Count == 0) return NoChanges(missingInventory);
        // Dirty filtering must happen before the provider batch limit. A large
        // catalog with only a few changed offers is a valid request, while a
        // larger dirty set is drained deterministically in follow-up passes.
        lines = lines.Take(isHepsiburada ? HepsiburadaHttpClient.MaxInventoryUploadItems : MaximumBatchSize).ToList();
        var payload = isHepsiburada
            ? JsonSerializer.Serialize(new
            {
                merchantId = connection.ExternalStoreId,
                items = lines.Select(x => new { hepsiburadaSku = x.HepsiburadaSku, merchantSku = x.MerchantSku, price = x.SalePrice, availableStock = checked((int)Math.Min((decimal)int.MaxValue, x.Quantity)) }).ToArray()
            })
            : JsonSerializer.Serialize(new { items = lines.Select(x => new { barcode = x.Barcode, quantity = checked((int)Math.Min((decimal)int.MaxValue, x.Quantity)), salePrice = x.SalePrice, listPrice = x.ListPrice }).ToArray() });
        return ServiceResult<PriceInventoryDraft>.Ok(new(Hash(payload), payload, lines) { MissingInventorySkus = missingInventory });
    }

    // Hepsiburada SIT certification (STAGE connection, manual send from the panel only). The inventory
    // products Hepsiburada loads into the SIT account reach the panel through the product import (a
    // variant link and an ACTIVE offer) and have no listing profile or LIVE state, so the Hepsiburada
    // branch above finds nothing. This batch is built from the imported links instead, with the same
    // payload shape and line record, so the POLL phase reconciles it the same way. Every ACTIVE offer is
    // sent, not only changed ones, since the point is a fresh inventoryUploadId; links that carry the
    // merchant SKU instead of an hbSku are left out because the upload needs both SKUs.
    public async Task<ServiceResult<PriceInventoryDraft>> BuildHepsiburadaStageAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, Guid? variantId = null)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Id == connectionId, cancellationToken);
        if (connection is null) return ServiceResult<PriceInventoryDraft>.Fail("CONNECTION_NOT_FOUND", "Pazaryeri bağlantısı bulunamadı.", 404);
        if (!HepsiburadaStageCertificationPolicy.IsStageHepsiburada(connection))
            return ServiceResult<PriceInventoryDraft>.Fail("HEPSIBURADA_STAGE_CONNECTION_REQUIRED", "Bu fiyat-stok yüklemesi yalnız Hepsiburada test (STAGE) bağlantısında kullanılabilir.", 422);
        var rows = await (from offer in db.ChannelOffers.AsNoTracking()
                          join variant in db.ProductVariants.AsNoTracking() on new { offer.TenantId, offer.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                          join remoteLink in db.MarketplaceVariantLinks.AsNoTracking() on new { offer.TenantId, offer.ConnectionId, offer.VariantId } equals new { remoteLink.TenantId, remoteLink.ConnectionId, remoteLink.VariantId }
                          join inventory in db.InventoryItems.AsNoTracking().Where(x => x.LocationCode == "MAIN") on new { offer.TenantId, offer.VariantId } equals new { inventory.TenantId, inventory.VariantId } into inventories
                          from inventory in inventories.DefaultIfEmpty()
                          where offer.TenantId == tenantId && offer.ConnectionId == connectionId && offer.Status == "ACTIVE"
                               && (variantId == null || offer.VariantId == variantId)
                          orderby variant.Id
                          select new { Offer = offer, Variant = variant, Inventory = inventory, ExternalId = remoteLink.ExternalId })
            .ToListAsync(cancellationToken);
        var candidates = rows.Where(row => HepsiburadaStageCertificationPolicy.LooksLikeHepsiburadaSku(row.ExternalId)).ToList();
        if (candidates.Count == 0) return ServiceResult<PriceInventoryDraft>.Fail("HEPSIBURADA_SKU_LINK_REQUIRED", "SIT fiyat-stok yüklemesi için hbSku (HB…) ile içe aktarılmış ACTIVE Hepsiburada teklifi gerekir.", 422);
        var missingInventory = candidates.Where(row => row.Inventory is null).Select(row => row.Variant.Sku).ToList();
        if (missingInventory.Count == candidates.Count) return MissingInventory(missingInventory);

        var lines = new List<PriceInventoryPushLine>();
        var merchantSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hepsiburadaSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in candidates.Where(row => row.Inventory is not null))
        {
            var merchantSku = row.Variant.Sku.Trim();
            var hepsiburadaSku = row.ExternalId.Trim();
            var salePrice = OutboundSalePrice(row.Offer);
            var listPrice = OutboundListPrice(row.Offer);
            // The upload takes one line per merchantSku and hbSku, a positive TRY price and SKUs without
            // spaces (HepsiburadaHttpClient.ValidPriceInventoryPayload); other offers stay out of the batch.
            if (!string.Equals(row.Offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || salePrice <= 0
                || merchantSku.Length == 0 || merchantSku.Any(char.IsWhiteSpace)
                || !merchantSkus.Add(merchantSku) || !hepsiburadaSkus.Add(hepsiburadaSku)) continue;
            var quantity = decimal.Floor(Math.Max(0, row.Inventory!.Available - row.Offer.SafetyStock));
            var priceHash = StandardPriceHash(listPrice, salePrice, row.Offer.PriceVersion);
            var barcode = string.IsNullOrWhiteSpace(row.Variant.Barcode) ? merchantSku : row.Variant.Barcode.Trim();
            lines.Add(new(row.Variant.Id, row.Offer.Id, barcode, quantity, listPrice, salePrice, "TRY", row.Inventory.ProjectionVersion, row.Offer.PriceVersion, priceHash, merchantSku, hepsiburadaSku));
        }
        if (lines.Count == 0) return ServiceResult<PriceInventoryDraft>.Fail("CHANNEL_OFFER_INVALID", "SIT fiyat-stok yüklemesine uygun teklif yok: TRY, sıfırdan büyük fiyat ve boşluksuz, tekil merchantSku/hbSku gerekir.", 422);
        lines = lines.Take(HepsiburadaHttpClient.MaxInventoryUploadItems).ToList();
        var payload = JsonSerializer.Serialize(new
        {
            merchantId = connection.ExternalStoreId,
            items = lines.Select(x => new { hepsiburadaSku = x.HepsiburadaSku, merchantSku = x.MerchantSku, price = x.SalePrice, availableStock = checked((int)Math.Min((decimal)int.MaxValue, x.Quantity)) }).ToArray()
        });
        return ServiceResult<PriceInventoryDraft>.Ok(new(Hash(payload), payload, lines) { MissingInventorySkus = missingInventory });
    }

    // n11 has no listing profile in the panel: an n11 offer is sent when its
    // variant is linked to an n11 stock code. Prices go with two decimals and
    // listPrice >= salePrice, stock is capped at n11's 999,999.
    private async Task<ServiceResult<PriceInventoryDraft>> BuildN11Async(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, Guid? variantId, Guid? productId)
    {
        var rows = await (from offer in db.ChannelOffers.AsNoTracking()
                          join variant in db.ProductVariants.AsNoTracking() on new { offer.TenantId, offer.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                          join remoteLink in db.MarketplaceVariantLinks.AsNoTracking() on new { offer.TenantId, offer.ConnectionId, offer.VariantId } equals new { remoteLink.TenantId, remoteLink.ConnectionId, remoteLink.VariantId }
                          join inventory in db.InventoryItems.AsNoTracking().Where(x => x.LocationCode == "MAIN") on new { offer.TenantId, offer.VariantId } equals new { inventory.TenantId, inventory.VariantId } into inventories
                          from inventory in inventories.DefaultIfEmpty()
                          where offer.TenantId == tenantId && offer.ConnectionId == connectionId && offer.Status == "ACTIVE"
                               && (variantId == null || offer.VariantId == variantId)
                               && (productId == null || variant.ProductId == productId)
                          orderby variant.Id
                          select new { Offer = offer, Variant = variant, Inventory = inventory, StockCode = remoteLink.ExternalId })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return ServiceResult<PriceInventoryDraft>.Fail("LIVE_OFFER_REQUIRED", "N11 fiyat-stok gönderimi için N11 stok koduna bağlı varyant ve ACTIVE N11 teklifi gerekir.", 422);
        var missingInventory = rows.Where(row => row.Inventory is null).Select(row => row.Variant.Sku).ToList();
        if (missingInventory.Count == rows.Count) return MissingInventory(missingInventory);

        var lines = new List<PriceInventoryPushLine>();
        foreach (var row in rows.Where(row => row.Inventory is not null))
        {
            var salePrice = N11PriceInventoryRules.Price(OutboundSalePrice(row.Offer));
            var listPrice = Math.Max(N11PriceInventoryRules.Price(OutboundListPrice(row.Offer)), salePrice);
            if (!string.Equals(row.Offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || salePrice <= 0)
                return ServiceResult<PriceInventoryDraft>.Fail("CHANNEL_OFFER_INVALID", $"'{row.Variant.Sku}' için N11 teklifinde TRY para birimi ve sıfırdan büyük satış fiyatı gerekir.", 422);
            var quantity = N11PriceInventoryRules.Quantity(row.Inventory!.Available - row.Offer.SafetyStock);
            var priceHash = Hash($"{listPrice:0.00}|{salePrice:0.00}|TRY|{row.Offer.PriceVersion}");
            if (row.Offer.LastPriceHash == priceHash && row.Offer.LastStockProjectionVersion == row.Inventory.ProjectionVersion) continue;
            lines.Add(new(row.Variant.Id, row.Offer.Id, row.StockCode.Trim(), quantity, listPrice, salePrice, "TRY", row.Inventory.ProjectionVersion, row.Offer.PriceVersion, priceHash));
        }
        if (lines.Count == 0) return NoChanges(missingInventory);
        lines = lines.Take(N11PriceInventoryRules.MaxSkusPerRequest).ToList();
        var payload = N11PriceInventoryRules.Payload(lines);
        return ServiceResult<PriceInventoryDraft>.Ok(new(Hash(payload), payload, lines) { MissingInventorySkus = missingInventory });
    }

    // Pazarama has no listing profile in the panel either: an offer is sent when its variant is linked
    // to a Pazarama barcode (the variant link carries it). Price and stock go as two requests built
    // from the same lines; prices keep two decimals with listPrice >= salePrice.
    private async Task<ServiceResult<PriceInventoryDraft>> BuildPazaramaAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, Guid? variantId, Guid? productId)
    {
        var rows = await (from offer in db.ChannelOffers.AsNoTracking()
                          join variant in db.ProductVariants.AsNoTracking() on new { offer.TenantId, offer.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                          join remoteLink in db.MarketplaceVariantLinks.AsNoTracking() on new { offer.TenantId, offer.ConnectionId, offer.VariantId } equals new { remoteLink.TenantId, remoteLink.ConnectionId, remoteLink.VariantId }
                          join inventory in db.InventoryItems.AsNoTracking().Where(x => x.LocationCode == "MAIN") on new { offer.TenantId, offer.VariantId } equals new { inventory.TenantId, inventory.VariantId } into inventories
                          from inventory in inventories.DefaultIfEmpty()
                          where offer.TenantId == tenantId && offer.ConnectionId == connectionId && offer.Status == "ACTIVE"
                               && (variantId == null || offer.VariantId == variantId)
                               && (productId == null || variant.ProductId == productId)
                          orderby variant.Id
                          select new { Offer = offer, Variant = variant, Inventory = inventory, Barcode = remoteLink.ExternalId })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return ServiceResult<PriceInventoryDraft>.Fail("LIVE_OFFER_REQUIRED", "Pazarama fiyat-stok gönderimi için Pazarama barkoduna bağlı varyant ve ACTIVE Pazarama teklifi gerekir.", 422);
        var missingInventory = rows.Where(row => row.Inventory is null).Select(row => row.Variant.Sku).ToList();
        if (missingInventory.Count == rows.Count) return MissingInventory(missingInventory);

        var lines = new List<(PriceInventoryPushLine Line, bool Price, bool Stock)>();
        foreach (var row in rows.Where(row => row.Inventory is not null))
        {
            var salePrice = PazaramaPriceInventoryRules.Price(OutboundSalePrice(row.Offer));
            var listPrice = Math.Max(PazaramaPriceInventoryRules.Price(OutboundListPrice(row.Offer)), salePrice);
            if (!string.Equals(row.Offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || salePrice <= 0)
                return ServiceResult<PriceInventoryDraft>.Fail("CHANNEL_OFFER_INVALID", $"'{row.Variant.Sku}' için Pazarama teklifinde TRY para birimi ve sıfırdan büyük satış fiyatı gerekir.", 422);
            var barcode = row.Barcode.Trim();
            if (barcode.Length == 0 || barcode.Length > PazaramaPriceInventoryRules.MaxCodeLength)
                return ServiceResult<PriceInventoryDraft>.Fail("PAZARAMA_BARCODE_INVALID", $"'{row.Variant.Sku}' için Pazarama barkodu boş veya {PazaramaPriceInventoryRules.MaxCodeLength} karakterden uzun.", 422);
            var quantity = PazaramaPriceInventoryRules.Quantity(row.Inventory!.Available - row.Offer.SafetyStock);
            var priceHash = PazaramaPriceInventoryRules.PriceHash(listPrice, salePrice, row.Offer.PriceVersion);
            var priceChanged = row.Offer.LastPriceHash != priceHash;
            var stockChanged = row.Offer.LastStockProjectionVersion != row.Inventory.ProjectionVersion;
            if (!priceChanged && !stockChanged) continue;
            lines.Add((new(row.Variant.Id, row.Offer.Id, barcode, quantity, listPrice, salePrice, "TRY", row.Inventory.ProjectionVersion, row.Offer.PriceVersion, priceHash), priceChanged, stockChanged));
        }
        if (lines.Count == 0) return NoChanges(missingInventory);
        // A barcode may be linked to two variants only by mistake; one request must not carry it twice.
        lines = lines.GroupBy(x => x.Line.Barcode, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Take(PazaramaPriceInventoryRules.MaxItemsPerRequest).ToList();
        var payload = PazaramaPriceInventoryRules.Payload(lines);
        return ServiceResult<PriceInventoryDraft>.Ok(new(Hash(payload), payload, lines.Select(x => x.Line).ToList()) { MissingInventorySkus = missingInventory });
    }

    // PTT AVM has no listing profile in the panel either: an offer is sent when its variant is linked to a PTT AVM
    // barcode. Price (VAT inclusive, with the offer's VAT rate when PTT AVM accepts it) and stock go in one request,
    // so a line is sent when either changed; PTT AVM has no separate list price.
    private async Task<ServiceResult<PriceInventoryDraft>> BuildPttAvmAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, Guid? variantId, Guid? productId)
    {
        var rows = await (from offer in db.ChannelOffers.AsNoTracking()
                          join variant in db.ProductVariants.AsNoTracking() on new { offer.TenantId, offer.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                          join remoteLink in db.MarketplaceVariantLinks.AsNoTracking() on new { offer.TenantId, offer.ConnectionId, offer.VariantId } equals new { remoteLink.TenantId, remoteLink.ConnectionId, remoteLink.VariantId }
                          join inventory in db.InventoryItems.AsNoTracking().Where(x => x.LocationCode == "MAIN") on new { offer.TenantId, offer.VariantId } equals new { inventory.TenantId, inventory.VariantId } into inventories
                          from inventory in inventories.DefaultIfEmpty()
                          where offer.TenantId == tenantId && offer.ConnectionId == connectionId && offer.Status == "ACTIVE"
                               && (variantId == null || offer.VariantId == variantId)
                               && (productId == null || variant.ProductId == productId)
                          orderby variant.Id
                          select new { Offer = offer, Variant = variant, Inventory = inventory, Barcode = remoteLink.ExternalId })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return ServiceResult<PriceInventoryDraft>.Fail("LIVE_OFFER_REQUIRED", "PTT AVM fiyat-stok gönderimi için PTT AVM barkoduna bağlı varyant ve ACTIVE PTT AVM teklifi gerekir.", 422);
        var missingInventory = rows.Where(row => row.Inventory is null).Select(row => row.Variant.Sku).ToList();
        if (missingInventory.Count == rows.Count) return MissingInventory(missingInventory);

        var lines = new List<(PriceInventoryPushLine Line, decimal? VatRate)>();
        foreach (var row in rows.Where(row => row.Inventory is not null))
        {
            var salePrice = PttAvmPriceInventoryRules.Price(OutboundSalePrice(row.Offer));
            // PTT AVM refuses a price of 1 or less.
            if (!string.Equals(row.Offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || salePrice <= 1)
                return ServiceResult<PriceInventoryDraft>.Fail("CHANNEL_OFFER_INVALID", $"'{row.Variant.Sku}' için PTT AVM teklifinde TRY para birimi ve 1'den büyük satış fiyatı gerekir.", 422);
            // A model variant read without a barcode is linked by its PTT AVM id ("FK4530/7101"); PTT AVM keys price and
            // stock by the variant barcode, which its orders show to be the panel barcode (VariantBarkod).
            var barcode = PttAvmVariantMapper.IsModelVariantId(row.Barcode.Trim()) ? row.Variant.Barcode?.Trim() ?? "" : row.Barcode.Trim();
            if (barcode.Length == 0 || barcode.Length > PttAvmPriceInventoryRules.MaxBarcodeLength)
                return ServiceResult<PriceInventoryDraft>.Fail("CHANNEL_OFFER_INVALID", $"'{row.Variant.Sku}' için PTT AVM barkodu boş veya {PttAvmPriceInventoryRules.MaxBarcodeLength} karakterden uzun.", 422);
            var vatRate = PttAvmPriceInventoryRules.VatRate(row.Offer.VatRate);
            var quantity = PttAvmPriceInventoryRules.Quantity(row.Inventory!.Available - row.Offer.SafetyStock);
            var priceHash = PttAvmPriceInventoryRules.PriceHash(salePrice, vatRate, row.Offer.PriceVersion);
            if (row.Offer.LastPriceHash == priceHash && row.Offer.LastStockProjectionVersion == row.Inventory.ProjectionVersion) continue;
            lines.Add((new(row.Variant.Id, row.Offer.Id, barcode, quantity, salePrice, salePrice, "TRY", row.Inventory.ProjectionVersion, row.Offer.PriceVersion, priceHash), vatRate));
        }
        if (lines.Count == 0) return NoChanges(missingInventory);
        // A barcode may be linked to two variants only by mistake; one request must not carry it twice.
        lines = lines.GroupBy(x => x.Line.Barcode, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Take(PttAvmPriceInventoryRules.MaxItemsPerRequest).ToList();
        var payload = PttAvmPriceInventoryRules.Payload(lines);
        return ServiceResult<PriceInventoryDraft>.Ok(new(Hash(payload), payload, lines.Select(x => x.Line).ToList()) { MissingInventorySkus = missingInventory });
    }

    // The prices that leave the panel: VAT added for an offer kept without VAT, two decimals.
    internal static decimal OutboundSalePrice(ChannelOffer offer) =>
        ChannelOfferPricePolicy.Outbound(offer.SalePrice, offer.VatRate, offer.VatInclusion, offer.RoundingMode);

    internal static decimal OutboundListPrice(ChannelOffer offer) =>
        Math.Max(ChannelOfferPricePolicy.Outbound(offer.ListPrice, offer.VatRate, offer.VatInclusion, offer.RoundingMode), OutboundSalePrice(offer));

    // The sent-price marker of the Trendyol, Hepsiburada and Shopify requests, over the outbound prices.
    // A two-decimal price keeps the marker it had before outbound rounding, so no offer is resent for it.
    internal static string StandardPriceHash(decimal listPrice, decimal salePrice, long priceVersion) =>
        Hash($"{listPrice:0.####}|{salePrice:0.####}|TRY|{priceVersion}");

    internal static string StandardPriceHash(ChannelOffer offer) =>
        StandardPriceHash(OutboundListPrice(offer), OutboundSalePrice(offer), offer.PriceVersion);

    internal static string MissingInventoryMessage(IReadOnlyList<string> skus) =>
        $"MAIN stok kaydı olmayan {skus.Count} aktif teklif fiyat-stok gönderimine alınmadı: {string.Join(", ", skus.Take(10))}{(skus.Count > 10 ? ", …" : "")}. Bu varyantlar için stok kaydı oluşturun.";

    private static ServiceResult<PriceInventoryDraft> MissingInventory(IReadOnlyList<string> skus) =>
        ServiceResult<PriceInventoryDraft>.Fail("INVENTORY_REQUIRED", MissingInventoryMessage(skus), 422);

    private static ServiceResult<PriceInventoryDraft> NoChanges(IReadOnlyList<string> missingInventory) =>
        ServiceResult<PriceInventoryDraft>.Fail("NO_EXTERNAL_CHANGES", "Pazaryerine gönderilecek yeni fiyat veya stok değişikliği yok." + (missingInventory.Count > 0 ? " " + MissingInventoryMessage(missingInventory) : ""), 409);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal static class N11PriceInventoryRules
{
    public const int MaxSkusPerRequest = 1000;
    public const int MaxQuantity = 999_999;
    public const string Integrator = "Ravencia";

    public static decimal Price(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal Quantity(decimal available) => Math.Clamp(decimal.Floor(Math.Max(0, available)), 0, MaxQuantity);

    // n11 rejects comma decimals and more than two decimal places, so prices
    // are written as invariant numbers carrying exactly two decimals.
    public static string Payload(IReadOnlyList<PriceInventoryPushLine> lines)
    {
        var skus = new JsonArray();
        foreach (var line in lines)
            skus.Add(new JsonObject
            {
                ["stockCode"] = line.Barcode,
                ["listPrice"] = TwoDecimals(line.ListPrice),
                ["salePrice"] = TwoDecimals(line.SalePrice),
                ["quantity"] = (int)Quantity(line.Quantity),
                ["currencyType"] = "TL"
            });
        return new JsonObject { ["payload"] = new JsonObject { ["integrator"] = Integrator, ["skus"] = skus } }.ToJsonString();
    }

    internal static decimal TwoDecimals(decimal value) =>
        decimal.Parse(Price(value).ToString("0.00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
