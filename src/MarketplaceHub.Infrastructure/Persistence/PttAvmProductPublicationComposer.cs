using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// PTT AVM product send (UpdateProductsV3, an upsert keyed by the product barcode). A product sent without variants
// loses every variant it has on PTT AVM, and the panel does not keep the parent barcode of an imported product with
// variants. So only a product with a single variant is sent, as a product without variants under that variant's
// barcode, and only when PTT AVM cannot be holding it as a product with variants: the product's links on this
// connection must be that one barcode and nothing else (no second variant, no archived variant still linked).
// The category must be a leaf; PTT AVM has no brand or attribute service, so neither is needed.
internal sealed class PttAvmProductPublicationComposer(AppDbContext db, IConfiguration configuration)
{
    public async Task<ServiceResult<ProductPublicationDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null) return NotFound();
        if (product.Status == ProductStatus.Archived) return Fail("PRODUCT_ARCHIVED", "Arşivlenmiş ürün yayınlanamaz.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PTTAVM", cancellationToken);
        if (connection is null) return NotFound();
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return Fail("LISTING_PROFILE_REQUIRED", "Yayın öncesi PTT AVM bağlantısına ait listing profile oluşturulmalıdır.");
        if (!profile.Enabled) return Fail("LISTING_PROFILE_DISABLED", "Listing profile etkinleştirilmeden yayın işi oluşturulamaz.");

        var categoryMapping = product.CategoryId is Guid categoryId
            ? await db.CategoryMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == categoryId && x.Status == "VERIFIED")
                .Join(db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.IsCurrent), mapping => mapping.SnapshotId, snapshot => snapshot.Id, (mapping, _) => mapping)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (categoryMapping is null) return Fail("CATEGORY_MAPPING_REQUIRED", "Yayın öncesi güncel PTT AVM kategori eşlemesi gerekir.", "categoryId");
        if (!long.TryParse(categoryMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var remoteCategoryId) || remoteCategoryId <= 0)
            return Fail("CATEGORY_MAPPING_INVALID", "PTT AVM kategori kimliği sayı olmalıdır.", "categoryId");
        if (!string.IsNullOrWhiteSpace(profile.ExternalCategoryId) && profile.ExternalCategoryId != categoryMapping.ExternalId)
            return Fail("LISTING_MAPPING_CONFLICT", "Listing profile kategorisi güncel doğrulanmış PTT AVM eşlemesiyle çelişiyor.", status: 409);
        if (!await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SnapshotId == categoryMapping.SnapshotId && x.ResourceType == "CATEGORIES" && x.ExternalId == categoryMapping.ExternalId && x.IsActive && x.IsLeaf, cancellationToken))
            return Fail("CATEGORY_LEAF_REQUIRED", "PTT AVM'de ürün yalnız en alt kategoriye açılabilir; kategori eşlemesini bir alt kategoriye güncelleyin.", "categoryId");

        var allVariants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variants = allVariants.Where(x => x.Status != ProductStatus.Archived).ToList();
        if (variants.Count == 0) return Fail("PRODUCT_VARIANT_REQUIRED", "Yayın için en az bir etkin varyant gerekir.");
        if (variants.Count > 1)
            return Fail("PTTAVM_VARIANT_PRODUCT_UNSUPPORTED", "PTT AVM'ye şimdilik yalnız tek varyantlı ürün gönderilir: varyantlı bir ürün eksik gönderilirse PTT AVM gönderilmeyen varyantları siler.");
        var variant = variants[0];
        var barcode = variant.Barcode?.Trim();
        if (string.IsNullOrEmpty(barcode) || barcode.Length > PttAvmPriceInventoryRules.MaxBarcodeLength)
            return Fail("BARCODE_REQUIRED", $"PTT AVM ürünü barkodla tanır; varyantın barkodu (en fazla {PttAvmPriceInventoryRules.MaxBarcodeLength} karakter) olmalıdır.");
        if (string.IsNullOrWhiteSpace(variant.Sku)) return Fail("STOCK_CODE_INVALID", "PTT AVM ürün kodu olarak kullanılan SKU boş olamaz.");
        if (await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenantId, connectionId, [variant.Id], [barcode], [barcode], cancellationToken) is { } takenBarcode)
            return Fail("BARCODE_DUPLICATE", ProductPublicationComposer.ConnectionDuplicateMessage(takenBarcode), status: 409);

        // The variant-deletion guard: every barcode this product is linked to on PTT AVM must be the one being sent.
        var variantIds = allVariants.Select(item => item.Id).ToArray();
        var linkedBarcodes = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId))
            .Select(x => x.ExternalId)
            .ToListAsync(cancellationToken);
        if (PttAvmProductPayloadRules.VariantDeletionRisk(barcode, linkedBarcodes) is { } risk)
            return Fail("PTTAVM_VARIANT_DELETION_RISK", risk, status: 409);

        var offer = await db.ChannelOffers.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id && x.Status == "ACTIVE", cancellationToken);
        var inventory = await db.InventoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.LocationCode == "MAIN", cancellationToken);
        if (offer is null) return Fail("CHANNEL_OFFER_REQUIRED", "Varyant için etkin PTT AVM fiyat teklifi gerekir.");
        if (inventory is null) return Fail("INVENTORY_REQUIRED", "Varyant için MAIN stok kaydı gerekir.");
        var salePrice = PttAvmPriceInventoryRules.Price(PriceInventoryComposer.OutboundSalePrice(offer));
        if (!string.Equals(offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || salePrice <= 1)
            return Fail("CHANNEL_OFFER_INVALID", $"'{variant.Sku}' için TRY para birimi ve 1'den büyük satış fiyatı gerekir.");
        if (PttAvmPriceInventoryRules.VatRate(offer.VatRate) is not { } vatRate)
            return Fail("VAT_RATE_INVALID", $"'{variant.Sku}' için KDV oranı PTT AVM'de 0, 1, 10 veya 20 olmalıdır.");
        if (variant.Desi is not > 0) return Fail("DESI_REQUIRED", $"'{variant.Sku}' için PTT AVM'nin istediği desi değeri girilmemiş.");

        var title = (profile.TitleOverride ?? product.Title).Trim();
        var description = (profile.DescriptionOverride ?? product.Description).Trim();
        if (title.Length is < 1 or > PttAvmProductPayloadRules.MaxNameLength) return Fail("PRODUCT_TITLE_INVALID", $"PTT AVM ürün adı 1-{PttAvmProductPayloadRules.MaxNameLength} karakter olmalıdır; listing profilinde kısa bir başlık girin.", "title");
        if (description.Length == 0) return Fail("PRODUCT_DESCRIPTION_INVALID", "PTT AVM ürün açıklaması boş olamaz.");

        var media = await (from productMedia in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on productMedia.FileAssetId equals asset.Id
                           where productMedia.TenantId == tenantId && productMedia.ProductId == productId && productMedia.Status == "ACTIVE" && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null
                           orderby productMedia.SortOrder, productMedia.Id
                           select new { productMedia.VariantId, asset.Id, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        var assignedMedia = media.Where(x => x.VariantId == variant.Id).ToList();
        var relevantMedia = assignedMedia.Count > 0 ? assignedMedia : media.Where(x => x.VariantId is null).ToList();
        if (relevantMedia.Where(x => x.Classification == "PRODUCT_MEDIA_URL").Any(x => !ProductPublicationComposer.IsPublicHttpsUrl(x.RelativePath.Trim())))
            return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", $"'{variant.Sku}' için görsel adresleri geçerli herkese açık HTTPS adresi olmalıdır.");
        var imageUrls = ProductPublicationComposer.SelectPublicationImageUrls(relevantMedia
            .Select(item => item.Classification == "PRODUCT_MEDIA_URL" ? item.RelativePath.Trim() : ProductPublicationComposer.BuildPublicProductMediaUrl(configuration["Marketplace:PublicBaseUrl"], item.Id))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!));
        // PTT AVM needs at least one image for a new product.
        if (imageUrls.Count == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", $"'{variant.Sku}' için en az bir herkese açık HTTPS ürün görseli gerekir.");

        var draft = new PttAvmProductPayloadRules.ProductDraft(barcode, title, description, categoryMapping.ExternalId, variant.Sku.Trim(), salePrice, vatRate,
            PttAvmPriceInventoryRules.Quantity(inventory.Available - offer.SafetyStock), Math.Ceiling(variant.Desi.Value), imageUrls);
        var payloadJson = PttAvmProductPayloadRules.Payload(draft);
        if (PttAvmProductPayloadRules.Read(payloadJson, out var invalid) is null) return Fail("PRODUCT_PAYLOAD_INVALID", invalid ?? "PTT AVM ürün içeriği geçersiz.");
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return ServiceResult<ProductPublicationDraft>.Ok(new(profile.Id, categoryMapping.ExternalId, string.Empty, payloadHash, payloadJson, [new(variant.Id, variant.Sku.Trim(), barcode)]));
    }

    private static ServiceResult<ProductPublicationDraft> NotFound() => ServiceResult<ProductPublicationDraft>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<ProductPublicationDraft> Fail(string code, string message, string? field = null, int status = 422) => ServiceResult<ProductPublicationDraft>.Fail(code, message, status, field is null ? null : new Dictionary<string, string[]> { [field] = [message] });
}
