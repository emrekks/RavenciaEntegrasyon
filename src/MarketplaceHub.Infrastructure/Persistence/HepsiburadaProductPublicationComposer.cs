using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed class HepsiburadaProductPublicationComposer(AppDbContext db, IConfiguration configuration)
{
    private static readonly HashSet<string> ReservedAttributeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "merchantSku", "VaryantGroupID", "Barcode", "UrunAdi", "UrunAciklamasi", "Marka",
        "GarantiSuresi", "kg", "tax_vat_rate", "price", "stock", "Video1", "Image1", "Image2", "Image3", "Image4", "Image5"
    };

    public async Task<ServiceResult<ProductPublicationDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null) return NotFound();
        if (product.Status == ProductStatus.Archived) return Fail("PRODUCT_ARCHIVED", "Arşivlenmiş ürün yayınlanamaz.");

        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "HEPSIBURADA", cancellationToken);
        if (connection is null) return NotFound();
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return Fail("LISTING_PROFILE_REQUIRED", "Yayın öncesi Hepsiburada bağlantısına ait listing profile oluşturulmalıdır.");
        if (!profile.Enabled) return Fail("LISTING_PROFILE_DISABLED", "Listing profile etkinleştirilmeden yayın işi oluşturulamaz.");

        var categoryMapping = product.CategoryId is Guid categoryId
            ? await db.CategoryMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == categoryId && x.Status == "VERIFIED")
                .Join(db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.IsCurrent), mapping => mapping.SnapshotId, snapshot => snapshot.Id, (mapping, _) => mapping)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (categoryMapping is null) return Fail("CATEGORY_MAPPING_REQUIRED", "Yayın öncesi güncel Hepsiburada kategori eşlemesi gerekir.", "categoryId");
        if (!string.IsNullOrWhiteSpace(profile.ExternalCategoryId) && profile.ExternalCategoryId != categoryMapping.ExternalId)
            return Fail("LISTING_MAPPING_CONFLICT", "Listing profile kategorisi güncel doğrulanmış Hepsiburada eşlemesiyle çelişiyor.", status: 409);
        if (!int.TryParse(categoryMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var remoteCategoryId) || remoteCategoryId <= 0)
            return Fail("CATEGORY_MAPPING_INVALID", "Hepsiburada kategori kimliği pozitif bir tam sayı olmalıdır.", "categoryId");

        var categorySnapshot = await db.ReferenceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.ScopeExternalId == categoryMapping.ExternalId && x.IsCurrent, cancellationToken);
        if (categorySnapshot is null) return Fail("ATTRIBUTE_SNAPSHOT_REQUIRED", "Seçilen Hepsiburada kategorisinin güncel özellik snapshot'ı gerekir.");
        var remoteAttributes = await db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == categorySnapshot.Id && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive).ToListAsync(cancellationToken);
        var attributeMappings = await db.AttributeMappings.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == categoryMapping.ExternalId && x.SnapshotId == categorySnapshot.Id && x.Status == "VERIFIED").ToListAsync(cancellationToken);
        if (attributeMappings.GroupBy(x => x.ExternalId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", "Aynı Hepsiburada kategori özelliğine birden fazla yerel özellik eşlenmiş.", status: 409);
        var remoteById = remoteAttributes.ToDictionary(x => x.ExternalId, StringComparer.Ordinal);
        var mappingsByLocalId = attributeMappings.GroupBy(x => x.LocalId).ToDictionary(group => group.Key, group => (IReadOnlyList<AttributeMapping>)group.ToList());
        foreach (var mapping in attributeMappings)
        {
            if (!remoteById.ContainsKey(mapping.ExternalId)) return Fail("ATTRIBUTE_MAPPING_STALE", "Özellik eşlemesi güncel Hepsiburada kategori snapshot'ında bulunamadı.", status: 409);
            var propertyName = AttributeImportKey(mapping.ExternalId);
            if (propertyName is null || ReservedAttributeKeys.Contains(propertyName))
                return Fail("ATTRIBUTE_IMPORT_KEY_INVALID", "Hepsiburada kategori özelliğinin aktarım alanı anahtarı geçersiz veya standart alanla çakışıyor.");
        }
        foreach (var required in remoteAttributes.Where(x => x.IsRequired == true))
            if (!attributeMappings.Any(mapping => mapping.ExternalId == required.ExternalId)) return Fail("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", $"Zorunlu Hepsiburada özelliği '{required.Name}' eşlenmemiş.");

        var brand = product.BrandId is Guid brandId
            ? await db.Brands.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == brandId && x.IsActive, cancellationToken)
            : null;
        if (brand is null || string.IsNullOrWhiteSpace(brand.Name)) return Fail("BRAND_REQUIRED", "Hepsiburada ürün aktarımı için etkin bir yerel marka seçilmelidir.", "brandId");
        var brandName = brand.Name.Trim();

        var assignments = await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        if (assignments.Any(x => !mappingsByLocalId.ContainsKey(x.AttributeId))) return Fail("ATTRIBUTE_MAPPING_REQUIRED", "Üründe kullanılan her özellik güncel Hepsiburada kategori snapshot'ına eşlenmelidir.");
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId && x.Status != ProductStatus.Archived).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (variants.Count == 0) return Fail("PRODUCT_VARIANT_REQUIRED", "Yayın için en az bir etkin varyant gerekir.");
        if (variants.Count > 1000) return Fail("PRODUCT_BATCH_LIMIT_EXCEEDED", "Tek Hepsiburada yayın isteğinde en fazla 1000 varyant gönderilebilir.");
        if (variants.Any(x => !IsValidEan13(x.Barcode))) return Fail("BARCODE_INVALID", "Hepsiburada ürün aktarımında tüm varyantların geçerli 13 haneli EAN-13 barkodu olmalıdır.");
        if (variants.Select(x => x.Barcode!.Trim()).Distinct(StringComparer.Ordinal).Count() != variants.Count) return Fail("BARCODE_DUPLICATE", "Hepsiburada ürün aktarımındaki EAN-13 barkodları benzersiz olmalıdır.");
        var sellerSkus = variants.Select(x => NormalizeMerchantSku(x.Sku)).ToArray();
        if (sellerSkus.Any(x => x is null)) return Fail("MERCHANT_SKU_INVALID", "Hepsiburada merchantSku boş olmamalı ve boşluk yerine noktalama kullanılmalıdır.");
        if (sellerSkus.Distinct(StringComparer.Ordinal).Count() != sellerSkus.Length) return Fail("MERCHANT_SKU_DUPLICATE", "Büyük harf/boşluksuz dönüşüm sonrası merchantSku değerleri benzersiz olmalıdır.");
        var modelCodes = variants.Select(x => x.ModelCode?.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (modelCodes.Count != 1 || string.IsNullOrWhiteSpace(modelCodes[0])) return Fail("MODEL_CODE_REQUIRED", "Varyant grubu tek bir ortak VaryantGroupID kullanmalıdır.");

        var variantIds = variants.Select(x => x.Id).ToArray();
        var optionRows = await (from option in db.ProductOptions.AsNoTracking()
                                join optionValue in db.ProductOptionValues.AsNoTracking() on new { option.TenantId, OptionId = option.Id } equals new { optionValue.TenantId, OptionId = optionValue.OptionId }
                                join variantOption in db.VariantOptionValues.AsNoTracking() on new { optionValue.TenantId, OptionValueId = optionValue.Id } equals new { variantOption.TenantId, OptionValueId = variantOption.OptionValueId }
                                where option.TenantId == tenantId && option.ProductId == productId && variantIds.Contains(variantOption.VariantId)
                                select new { variantOption.VariantId, option.Label, ValueLabel = optionValue.Label }).ToListAsync(cancellationToken);
        var localAttributes = await db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && mappingsByLocalId.Keys.Contains(x.Id)).ToListAsync(cancellationToken);
        var localValues = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && mappingsByLocalId.Keys.Contains(x.AttributeId) && x.IsActive).ToListAsync(cancellationToken);
        var offers = await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId) && x.Status == "ACTIVE").ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var inventories = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId) && x.LocationCode == "MAIN").ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (offers.Count != variants.Count) return Fail("CHANNEL_OFFER_REQUIRED", "Her varyant için etkin Hepsiburada fiyat teklifi gerekir.");
        if (inventories.Count != variants.Count) return Fail("INVENTORY_REQUIRED", "Her varyant için MAIN stok kaydı gerekir.");

        var media = await (from productMedia in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on productMedia.FileAssetId equals asset.Id
                           where productMedia.TenantId == tenantId && productMedia.ProductId == productId && productMedia.Status == "ACTIVE" && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby productMedia.SortOrder, productMedia.Id
                           select new { productMedia.VariantId, productMedia.SortOrder, asset.Id, asset.Classification, asset.RelativePath, asset.MimeType }).ToListAsync(cancellationToken);
        var title = (profile.TitleOverride ?? product.Title).Trim();
        var description = (profile.DescriptionOverride ?? product.Description).Trim();
        if (!title.StartsWith(brandName, StringComparison.OrdinalIgnoreCase) || (title.Length > brandName.Length && char.IsLetterOrDigit(title[brandName.Length])))
            return Fail("PRODUCT_TITLE_BRAND_PREFIX_REQUIRED", "Hepsiburada ürün başlığı marka adıyla başlamalıdır.", "title");
        if (title.Length is < 1 or > 200) return Fail("PRODUCT_TITLE_INVALID", "Hepsiburada ürün başlığı 1-200 karakter olmalıdır.");
        if (description.Length is < 1 or > 30000) return Fail("PRODUCT_DESCRIPTION_INVALID", "Hepsiburada ürün açıklaması 1-30000 karakter olmalıdır.");

        var itemPayloads = new List<Dictionary<string, object?>>(variants.Count);
        var draftVariants = new List<PublicationVariantDraft>(variants.Count);
        for (var variantIndex = 0; variantIndex < variants.Count; variantIndex++)
        {
            var variant = variants[variantIndex];
            var offer = offers[variant.Id];
            var inventory = inventories[variant.Id];
            if (!string.Equals(offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || offer.SalePrice <= 0 || offer.ListPrice < offer.SalePrice)
                return Fail("CHANNEL_OFFER_INVALID", $"'{variant.Sku}' için TRY para birimi ve listPrice >= salePrice > 0 kuralı sağlanmalıdır.");
            if (offer.SalePrice != decimal.Round(offer.SalePrice, 2)) return Fail("CHANNEL_OFFER_PRECISION_INVALID", $"'{variant.Sku}' satış fiyatı en fazla iki ondalık basamak içermelidir.");
            if (offer.VatRate < 0 || offer.VatRate > 100 || decimal.Truncate(offer.VatRate) != offer.VatRate)
                return Fail("VAT_RATE_INVALID", $"'{variant.Sku}' için KDV oranı 0-100 arasında tam sayı olmalıdır.");

            var assignedMedia = media.Where(x => x.VariantId == variant.Id).ToList();
            var relevantMedia = assignedMedia.Count > 0 ? assignedMedia : media.Where(x => x.VariantId is null).ToList();
            if (relevantMedia.Any(item => !string.Equals(item.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.MimeType, "image/png", StringComparison.OrdinalIgnoreCase))) return Fail("PRODUCT_MEDIA_MIME_INVALID", $"'{variant.Sku}' ürün görselleri JPEG veya PNG olmalıdır.");
            var directUrls = relevantMedia.Where(x => x.Classification == "PRODUCT_MEDIA_URL").Select(x => x.RelativePath.Trim()).ToList();
            if (directUrls.Any(url => !ProductPublicationComposer.IsPublicHttpsUrl(url))) return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", $"'{variant.Sku}' için görsel adresleri geçerli herkese açık HTTPS adresi olmalıdır.");
            var imageUrls = relevantMedia.Select(item => item.Classification == "PRODUCT_MEDIA_URL" ? item.RelativePath.Trim() : ProductPublicationComposer.BuildPublicProductMediaUrl(configuration["Marketplace:PublicBaseUrl"], item.Id))
                .Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url!).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
            if (imageUrls.Length == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", $"'{variant.Sku}' için en az bir herkese açık HTTPS ürün görseli gerekir.");

            var importAttributes = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["merchantSku"] = sellerSkus[variantIndex],
                ["VaryantGroupID"] = modelCodes[0],
                ["Barcode"] = variant.Barcode!.Trim(),
                ["UrunAdi"] = title,
                ["UrunAciklamasi"] = description,
                ["Marka"] = brandName,
                ["tax_vat_rate"] = ((int)offer.VatRate).ToString(CultureInfo.InvariantCulture),
                ["price"] = offer.SalePrice.ToString("0.00", CultureInfo.GetCultureInfo("tr-TR")),
                ["stock"] = checked((int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, inventory.Available - offer.SafetyStock)))).ToString(CultureInfo.InvariantCulture)
            };
            if (!string.IsNullOrWhiteSpace(profile.Warranty))
            {
                if (!int.TryParse(profile.Warranty.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var warrantyMonths) || warrantyMonths < 0)
                    return Fail("WARRANTY_INVALID", "Garanti süresi ay cinsinden tam sayı olmalıdır.");
                importAttributes["GarantiSuresi"] = warrantyMonths;
            }
            if (variant.Desi is > 0)
            {
                if (variant.Desi.Value != decimal.Round(variant.Desi.Value, 2)) return Fail("DESI_PRECISION_INVALID", $"'{variant.Sku}' desi değeri en fazla iki ondalık basamak içermelidir.");
                importAttributes["kg"] = variant.Desi.Value.ToString("0.##", CultureInfo.InvariantCulture);
            }
            for (var imageIndex = 0; imageIndex < imageUrls.Length; imageIndex++) importAttributes[$"Image{imageIndex + 1}"] = imageUrls[imageIndex];

            var effectiveAssignments = assignments.Where(x => x.VariantId is null || x.VariantId == variant.Id).GroupBy(x => x.AttributeId);
            var emittedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in effectiveAssignments)
            {
                if (!mappingsByLocalId.TryGetValue(group.Key, out var mappings) || mappings.Count == 0) return Fail("ATTRIBUTE_MAPPING_REQUIRED", "Üründe kullanılan özellik için doğrulanmış Hepsiburada eşlemesi bulunamadı.");
                if (mappings.Count != 1) return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", "Üründe kullanılan panel özelliği birden fazla Hepsiburada alanına eşlenmiş; tek bir alan seçilmelidir.", status: 409);
                var mapping = mappings[0];
                var remote = remoteById[mapping.ExternalId];
                var importKey = AttributeImportKey(remote.ExternalId)!;
                var values = group.ToList();
                if (values.Count > 1) return Fail("ATTRIBUTE_MULTIVALUE_UNSUPPORTED", $"'{remote.Name}' için Hepsiburada içe aktarma dosyasında birden fazla değerin güvenli gösterimi SIT fixture'ıyla doğrulanmamış.");
                var payloadValues = new List<object?>();
                foreach (var assignment in values)
                {
                    var value = await ResolveAttributeValueAsync(tenantId, connectionId, categoryMapping.ExternalId, mapping.ExternalId, assignment, remote, localValues, cancellationToken);
                    if (!value.Succeeded) return ServiceResult<ProductPublicationDraft>.Fail(value.Error!.Code, value.Error.Message, value.Error.Status, value.Error.FieldErrors);
                    payloadValues.Add(value.Value);
                }
                if (!emittedKeys.Add(importKey)) return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", $"'{remote.Name}' aktarım alanına birden fazla değer eşlenmiş.", status: 409);
                importAttributes[importKey] = payloadValues[0];
            }

            foreach (var option in optionRows.Where(x => x.VariantId == variant.Id).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            {
                var localAttribute = localAttributes.FirstOrDefault(x => Normalize(option.Label) == Normalize(x.Name));
                if (localAttribute is null || !mappingsByLocalId.TryGetValue(localAttribute.Id, out var optionMappings)) return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' varyant seçeneği için Hepsiburada özellik eşlemesi gerekir.");
                if (optionMappings.Count != 1) return Fail("OPTION_MAPPING_AMBIGUOUS", $"'{option.Label}' varyant seçeneği birden fazla Hepsiburada alanına eşlenmiş; tek bir alan seçilmelidir.", status: 409);
                var optionMapping = optionMappings[0];
                if (!remoteById.TryGetValue(optionMapping.ExternalId, out var optionRemote)) return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' eşlemesi güncel Hepsiburada kategori snapshot'ında bulunamadı.");
                var localValue = localValues.FirstOrDefault(x => x.AttributeId == localAttribute.Id && Normalize(x.Value) == Normalize(option.ValueLabel));
                string optionValue;
                if (localValue is not null)
                {
                    var valueMapping = await ResolveMappedValueAsync(tenantId, connectionId, categoryMapping.ExternalId, optionMapping.ExternalId, localValue.Id, cancellationToken);
                    if (!valueMapping.Succeeded) return ServiceResult<ProductPublicationDraft>.Fail(valueMapping.Error!.Code, valueMapping.Error.Message, valueMapping.Error.Status, valueMapping.Error.FieldErrors);
                    optionValue = valueMapping.Value!;
                }
                else if (optionRemote.AllowsCustomValue == true) optionValue = option.ValueLabel.Trim();
                else return Fail("OPTION_VALUE_MAPPING_REQUIRED", $"'{option.Label}: {option.ValueLabel}' seçeneği için güncel Hepsiburada değer eşlemesi gerekir.");
                var optionImportKey = AttributeImportKey(optionRemote.ExternalId)!;
                if (!emittedKeys.Add(optionImportKey)) return Fail("ATTRIBUTE_ASSIGNMENT_AMBIGUOUS", $"'{optionRemote.Name}' için atama ile varyant seçeneği aynı anda tanımlanmış.");
                importAttributes[optionImportKey] = optionValue;
            }

            foreach (var required in remoteAttributes.Where(x => x.IsRequired == true))
                if (!emittedKeys.Contains(AttributeImportKey(required.ExternalId)!)) return Fail("REQUIRED_ATTRIBUTE_MISSING", $"'{variant.Sku}' için Hepsiburada zorunlu özelliği '{required.Name}' değeri eksik.");

            itemPayloads.Add(new Dictionary<string, object?>
            {
                ["categoryId"] = remoteCategoryId,
                ["merchant"] = connection.ExternalStoreId,
                ["attributes"] = importAttributes
            });
            draftVariants.Add(new(variant.Id, sellerSkus[variantIndex]!, variant.Barcode!.Trim()));
        }

        var payloadJson = JsonSerializer.Serialize(itemPayloads);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return ServiceResult<ProductPublicationDraft>.Ok(new(profile.Id, categoryMapping.ExternalId, brandName, payloadHash, payloadJson, draftVariants));
    }

    internal async Task<ServiceResult<string>> ResolveAttributeValueAsync(Guid tenantId, Guid connectionId, string categoryId, string attributeId, ProductAttributeAssignment assignment, ReferenceItem remote, IReadOnlyList<AttributeValue> localValues, CancellationToken cancellationToken)
    {
        if (assignment.ValueId is Guid valueId)
        {
            if (!localValues.Any(value => value.AttributeId == assignment.AttributeId && value.Id == valueId)) return ServiceResult<string>.Fail("ATTRIBUTE_VALUE_INVALID", $"'{remote.Name}' için seçilen yerel değer bulunamadı.", 422);
            return await ResolveMappedValueAsync(tenantId, connectionId, categoryId, attributeId, valueId, cancellationToken);
        }
        var custom = assignment.TextValue?.Trim()
            ?? assignment.NumberValue?.ToString(CultureInfo.InvariantCulture)
            ?? assignment.BooleanValue?.ToString().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(custom) || remote.AllowsCustomValue != true) return ServiceResult<string>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{remote.Name}' serbest değer kabul etmiyor; doğrulanmış değer eşlemesi gerekir.", 422);
        return ServiceResult<string>.Ok(custom);
    }

    private async Task<ServiceResult<string>> ResolveMappedValueAsync(Guid tenantId, Guid connectionId, string categoryId, string attributeId, Guid localValueId, CancellationToken cancellationToken)
    {
        var scope = $"{categoryId}/{attributeId}";
        var mapping = await db.AttributeValueMappings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localValueId && x.ScopeExternalId == scope && x.Status == "VERIFIED", cancellationToken);
        if (mapping is null || !await db.ReferenceSnapshots.AsNoTracking().AnyAsync(snapshot => snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId && snapshot.Id == mapping.SnapshotId && snapshot.ResourceType == "ATTRIBUTE_VALUES" && snapshot.ScopeExternalId == scope && snapshot.IsCurrent, cancellationToken))
            return ServiceResult<string>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", "Özellik değeri güncel Hepsiburada enum snapshot'ında eşlenmemiş.", 422);
        var remoteValue = await db.ReferenceItems.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == tenantId && item.SnapshotId == mapping.SnapshotId && item.ResourceType == "ATTRIBUTE_VALUES" && item.ExternalId == mapping.ExternalId && item.IsActive, cancellationToken);
        return remoteValue is null
            ? ServiceResult<string>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", "Hepsiburada enum değer eşlemesi snapshot içinde bulunamadı.", 422)
            : ServiceResult<string>.Ok(remoteValue.Name);
    }

    internal static string? NormalizeMerchantSku(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var normalized = string.Join('-', sku.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return normalized.Length <= 100 ? normalized : null;
    }

    internal static string? AttributeImportKey(string attributeId)
    {
        if (string.IsNullOrWhiteSpace(attributeId) || attributeId.Length > 64 || attributeId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) return null;
        return $"attribute-{attributeId}";
    }

    internal static bool IsValidEan13(string? value)
    {
        if (value is null || value.Length != 13 || value.Any(character => !char.IsAsciiDigit(character))) return false;
        var sum = 0;
        for (var index = 0; index < 12; index++) sum += (value[index] - '0') * (index % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10 == value[12] - '0';
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static ServiceResult<ProductPublicationDraft> NotFound() => ServiceResult<ProductPublicationDraft>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<ProductPublicationDraft> Fail(string code, string message, string? field = null, int status = 422) => ServiceResult<ProductPublicationDraft>.Fail(code, message, status, field is null ? null : new Dictionary<string, string[]> { [field] = [message] });
}
