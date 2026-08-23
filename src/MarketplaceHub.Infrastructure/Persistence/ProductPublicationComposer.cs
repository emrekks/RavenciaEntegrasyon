using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record PublicationVariantDraft(Guid VariantId, string Sku, string Barcode);
internal sealed record ProductPublicationDraft(Guid ProfileId, string ExternalCategoryId, string ExternalBrandId, string PayloadHash, string PayloadJson, IReadOnlyList<PublicationVariantDraft> Variants);

internal sealed class ProductPublicationComposer(AppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ServiceResult<ProductPublicationDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null) return NotFound<ProductPublicationDraft>();
        if (product.Status == ProductStatus.Archived) return Fail("PRODUCT_ARCHIVED", "Arşivlenmiş ürün yayınlanamaz.");

        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return Fail("LISTING_PROFILE_REQUIRED", "Yayın öncesi bağlantıya ait listing profile oluşturulmalıdır.");
        if (!profile.Enabled) return Fail("LISTING_PROFILE_DISABLED", "Listing profile etkinleştirilmeden yayın işi oluşturulamaz.");

        var categoryMapping = product.CategoryId is Guid categoryId
            ? await db.CategoryMappings.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == categoryId && x.Status == "VERIFIED")
                .Join(db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.IsCurrent), mapping => mapping.SnapshotId, snapshot => snapshot.Id, (mapping, _) => mapping).SingleOrDefaultAsync(cancellationToken)
            : null;
        if (categoryMapping is null) return Fail("CATEGORY_MAPPING_REQUIRED", "Yayın öncesi güncel kategori eşlemesi gereklidir.", "categoryId", "Seçilen bağlantının güncel kategori snapshot'ı için doğrulanmış eşleme yok.");

        var brandMapping = product.BrandId is Guid brandId
            ? await db.BrandMappings.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == brandId && x.Status == "VERIFIED")
                .Join(db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.IsCurrent), mapping => mapping.SnapshotId, snapshot => snapshot.Id, (mapping, _) => mapping).SingleOrDefaultAsync(cancellationToken)
            : null;
        if (brandMapping is null) return Fail("BRAND_MAPPING_REQUIRED", "Yayın öncesi güncel marka eşlemesi gereklidir.", "brandId", "Ürün markası için doğrulanmış güncel marka eşlemesi zorunludur.");
        if (!long.TryParse(categoryMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var externalCategoryId) || !long.TryParse(brandMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var externalBrandId))
            return Fail("MAPPING_IDENTIFIER_INVALID", "Trendyol kategori ve marka kimlikleri sayısal olmalıdır.");
        if ((!string.IsNullOrWhiteSpace(profile.ExternalCategoryId) && profile.ExternalCategoryId != categoryMapping.ExternalId) || (!string.IsNullOrWhiteSpace(profile.ExternalBrandId) && profile.ExternalBrandId != brandMapping.ExternalId))
            return Fail("LISTING_MAPPING_CONFLICT", "Listing profile kimlikleri güncel doğrulanmış katalog eşlemeleriyle çelişiyor.", status: 409);

        var attributeSnapshot = await db.ReferenceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.ScopeExternalId == categoryMapping.ExternalId && x.IsCurrent, cancellationToken);
        if (attributeSnapshot is null) return Fail("ATTRIBUTE_SNAPSHOT_REQUIRED", "Seçili Trendyol kategorisinin güncel özellik snapshot'ı gereklidir.");
        var remoteAttributes = await db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == attributeSnapshot.Id && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive).ToListAsync(cancellationToken);
        var attributeMappings = await db.AttributeMappings.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == categoryMapping.ExternalId && x.SnapshotId == attributeSnapshot.Id && x.Status == "VERIFIED").ToListAsync(cancellationToken);
        if (attributeMappings.GroupBy(x => x.ExternalId, StringComparer.Ordinal).Any(group => group.Count() > 1)) return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", "Aynı Trendyol özelliğine birden fazla yerel özellik eşlenmiş.", status: 409);
        var mappingByExternalId = attributeMappings.ToDictionary(x => x.ExternalId, StringComparer.Ordinal);
        var mappingByLocalId = attributeMappings.ToDictionary(x => x.LocalId);
        var requiredLocalIds = new HashSet<Guid>();
        foreach (var remote in remoteAttributes.Where(x => x.IsRequired == true))
        {
            if (!mappingByExternalId.TryGetValue(remote.ExternalId, out var requiredMapping)) return Fail("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", $"Zorunlu Trendyol özelliği '{remote.Name}' eşlenmemiş.");
            requiredLocalIds.Add(requiredMapping.LocalId);
        }

        var assignments = await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var mappedLocalAttributes = await db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && mappingByLocalId.Keys.Contains(x.Id)).ToListAsync(cancellationToken);
        var mappedLocalValues = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && mappingByLocalId.Keys.Contains(x.AttributeId) && x.IsActive).ToListAsync(cancellationToken);
        foreach (var assignment in assignments)
        {
            if (!mappingByLocalId.TryGetValue(assignment.AttributeId, out var attributeMapping)) return Fail("ATTRIBUTE_MAPPING_REQUIRED", "Üründe kullanılan her özellik seçili Trendyol kategorisinin güncel snapshot'ında eşlenmelidir.");
            var remote = remoteAttributes.SingleOrDefault(x => x.ExternalId == attributeMapping.ExternalId);
            if (remote is null) return Fail("ATTRIBUTE_MAPPING_REQUIRED", "Özellik eşlemesi güncel kategori snapshot'ında bulunamadı.");
            if (assignment.ValueId is not Guid valueId)
            {
                if (remote.AllowsCustomValue != true) return Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{remote.Name}' serbest değer kabul etmiyor; doğrulanmış değer eşlemesi gereklidir.");
                continue;
            }
            var valueScope = $"{categoryMapping.ExternalId}/{attributeMapping.ExternalId}";
            var valueMapping = await db.AttributeValueMappings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == valueId && x.ScopeExternalId == valueScope && x.Status == "VERIFIED", cancellationToken);
            if (valueMapping is null || !await db.ReferenceSnapshots.AsNoTracking().AnyAsync(snapshot => snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId && snapshot.Id == valueMapping.SnapshotId && snapshot.ResourceType == "ATTRIBUTE_VALUES" && snapshot.ScopeExternalId == valueScope && snapshot.IsCurrent, cancellationToken)) return Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{remote.Name}' değeri güncel Trendyol değer snapshot'ında eşlenmemiş.");
            if (!await db.ReferenceItems.AsNoTracking().AnyAsync(item => item.TenantId == tenantId && item.SnapshotId == valueMapping.SnapshotId && item.ResourceType == "ATTRIBUTE_VALUES" && item.ExternalId == valueMapping.ExternalId && item.IsActive, cancellationToken)) return Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{remote.Name}' değer eşlemesi snapshot içinde bulunamadı.");
        }

        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId && x.Status != ProductStatus.Archived).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (variants.Count == 0) return Fail("PRODUCT_VARIANT_REQUIRED", "Yayın için en az bir etkin varyant gerekir.");
        if (variants.Count > 1000) return Fail("PRODUCT_BATCH_LIMIT_EXCEEDED", "Tek yayın isteğinde en fazla 1000 varyant gönderilebilir.");
        if (variants.Any(x => string.IsNullOrWhiteSpace(x.Barcode) || x.Barcode.Trim().Length > 40 || !IsValidBarcode(x.Barcode.Trim()))) return Fail("BARCODE_INVALID", "Tüm varyantlarda en fazla 40 karakterlik; yalnız harf, rakam, nokta, tire veya alt çizgi içeren barkod bulunmalıdır.");
        if (variants.Select(x => x.Barcode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variants.Count) return Fail("BARCODE_DUPLICATE", "Yayınlanan varyant barkodları benzersiz olmalıdır.");
        if (variants.Any(x => string.IsNullOrWhiteSpace(x.Sku) || x.Sku.Trim().Length > 100)) return Fail("SKU_INVALID", "Tüm varyantlarda en fazla 100 karakterlik SKU bulunmalıdır.");
        var modelCodes = variants.Select(x => x.ModelCode?.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (modelCodes.Count != 1 || string.IsNullOrWhiteSpace(modelCodes[0]) || modelCodes[0]!.Length > 40) return Fail("MODEL_CODE_REQUIRED", "Varyant grubu tek bir ortak ve en fazla 40 karakterlik model kodu kullanmalıdır.");

        var variantIds = variants.Select(x => x.Id).ToArray();
        var optionRows = await (from option in db.ProductOptions.AsNoTracking()
                                join optionValue in db.ProductOptionValues.AsNoTracking() on new { option.TenantId, OptionId = option.Id } equals new { optionValue.TenantId, OptionId = optionValue.OptionId }
                                join variantOption in db.VariantOptionValues.AsNoTracking() on new { optionValue.TenantId, OptionValueId = optionValue.Id } equals new { variantOption.TenantId, OptionValueId = variantOption.OptionValueId }
                                where option.TenantId == tenantId && option.ProductId == productId && variantIds.Contains(variantOption.VariantId)
                                select new { variantOption.VariantId, option.Label, ValueLabel = optionValue.Label }).ToListAsync(cancellationToken);
        var offers = await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId) && x.Status == "ACTIVE").ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var inventories = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId) && x.LocationCode == "MAIN").ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (offers.Count != variants.Count) return Fail("CHANNEL_OFFER_REQUIRED", "Her varyant için etkin Trendyol fiyat teklifi gerekir.");
        if (inventories.Count != variants.Count) return Fail("INVENTORY_REQUIRED", "Her varyant için MAIN stok kaydı gerekir.");

        var media = await (from productMedia in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on productMedia.FileAssetId equals asset.Id
                           where productMedia.TenantId == tenantId && productMedia.ProductId == productId && productMedia.Status == "ACTIVE" && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null && asset.Classification == "PRODUCT_MEDIA_URL"
                           orderby productMedia.SortOrder, productMedia.Id
                           select new { productMedia.VariantId, productMedia.SortOrder, asset.RelativePath }).ToListAsync(cancellationToken);

        var title = (profile.TitleOverride ?? product.Title).Trim();
        var description = (profile.DescriptionOverride ?? product.Description).Trim();
        if (title.Length is < 1 or > 100) return Fail("PRODUCT_TITLE_INVALID", "Trendyol yayın başlığı 1-100 karakter olmalıdır.");
        if (description.Length is < 1 or > 30000) return Fail("PRODUCT_DESCRIPTION_INVALID", "Trendyol açıklaması 1-30000 karakter olmalıdır.");
        if (!string.IsNullOrWhiteSpace(profile.Origin) && (profile.Origin.Trim().Length != 2 || !profile.Origin.Trim().All(char.IsLetter))) return Fail("ORIGIN_INVALID", "Menşei iki harfli ülke kodu olmalıdır.");

        var items = new List<Dictionary<string, object?>>(variants.Count);
        var draftVariants = new List<PublicationVariantDraft>(variants.Count);
        foreach (var variant in variants)
        {
            var offer = offers[variant.Id];
            if (!string.Equals(offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || offer.SalePrice <= 0 || offer.ListPrice < offer.SalePrice) return Fail("CHANNEL_OFFER_INVALID", $"'{variant.Sku}' için TRY para birimi ve listPrice >= salePrice > 0 kuralı sağlanmalıdır.");
            if (offer.VatRate < 0 || offer.VatRate > 100 || decimal.Truncate(offer.VatRate) != offer.VatRate) return Fail("VAT_RATE_INVALID", $"'{variant.Sku}' için KDV oranı 0-100 arasında tam sayı olmalıdır.");

            var relevantMedia = media.Where(x => x.VariantId is null || x.VariantId == variant.Id).Select(x => x.RelativePath.Trim()).ToList();
            if (relevantMedia.Any(url => !IsPublicHttpsUrl(url))) return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", $"'{variant.Sku}' için kayıtlı tüm PRODUCT_MEDIA_URL değerleri geçerli HTTPS adresi olmalıdır.");
            var imageUrls = relevantMedia.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (imageUrls.Count == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", $"'{variant.Sku}' için PRODUCT_MEDIA_URL sınıfında en az bir geçerli HTTPS görseli gerekir.");
            if (imageUrls.Count > 8) return Fail("PRODUCT_MEDIA_LIMIT_EXCEEDED", $"'{variant.Sku}' için en fazla 8 farklı görsel URL'si yayınlanabilir.");

            var effectiveAssignments = assignments.Where(x => x.VariantId is null || x.VariantId == variant.Id).GroupBy(x => x.AttributeId).ToList();
            var payloadAttributes = new List<Dictionary<string, object?>>();
            var emittedRemoteAttributeIds = new HashSet<long>();
            foreach (var group in effectiveAssignments)
            {
                var mapping = mappingByLocalId[group.Key];
                var remote = remoteAttributes.Single(x => x.ExternalId == mapping.ExternalId);
                if (!long.TryParse(mapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var remoteAttributeId)) return Fail("MAPPING_IDENTIFIER_INVALID", "Trendyol özellik kimlikleri sayısal olmalıdır.");
                var values = group.ToList();
                if (values.Count > 1 && remote.AllowsMultipleValues != true) return Fail("ATTRIBUTE_ASSIGNMENT_AMBIGUOUS", $"'{remote.Name}' birden fazla değer kabul etmiyor.");
                if (values.Count > 1 && values.Any(x => x.ValueId is null)) return Fail("ATTRIBUTE_ASSIGNMENT_AMBIGUOUS", $"'{remote.Name}' çoklu kullanımında yalnız eşlenmiş seçim değerleri kullanılabilir.");

                var attribute = new Dictionary<string, object?> { ["attributeId"] = remoteAttributeId };
                if (values.Count > 1)
                {
                    var remoteValues = new List<long>();
                    foreach (var value in values)
                    {
                        var externalValue = await ExternalValueAsync(tenantId, connectionId, categoryMapping.ExternalId, mapping.ExternalId, value.ValueId!.Value, cancellationToken);
                        if (externalValue.Error is not null) return ServiceResult<ProductPublicationDraft>.Fail(externalValue.Error.Code, externalValue.Error.Message, externalValue.Error.Status, externalValue.Error.FieldErrors);
                        remoteValues.Add(externalValue.Value);
                    }
                    attribute["attributeValueIds"] = remoteValues;
                }
                else if (values[0].ValueId is Guid valueId)
                {
                    var externalValue = await ExternalValueAsync(tenantId, connectionId, categoryMapping.ExternalId, mapping.ExternalId, valueId, cancellationToken);
                    if (externalValue.Error is not null) return ServiceResult<ProductPublicationDraft>.Fail(externalValue.Error.Code, externalValue.Error.Message, externalValue.Error.Status, externalValue.Error.FieldErrors);
                    attribute["attributeValueId"] = externalValue.Value;
                }
                else
                {
                    var customValue = CustomValue(values[0]);
                    if (string.IsNullOrWhiteSpace(customValue)) return Fail("ATTRIBUTE_CUSTOM_VALUE_REQUIRED", $"'{remote.Name}' için boş olmayan serbest değer gerekir.");
                    attribute["customAttributeValue"] = customValue;
                }
                payloadAttributes.Add(attribute);
                emittedRemoteAttributeIds.Add(remoteAttributeId);
            }

            foreach (var option in optionRows.Where(x => x.VariantId == variant.Id).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            {
                var localAttribute = mappedLocalAttributes.FirstOrDefault(x => NormalizeLabel(x.Name) == NormalizeLabel(option.Label));
                if (localAttribute is null || !mappingByLocalId.TryGetValue(localAttribute.Id, out var optionMapping))
                    return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' seçeneği için güncel Trendyol özellik eşlemesi bulunamadı.");
                if (!long.TryParse(optionMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var optionRemoteId)) return Fail("MAPPING_IDENTIFIER_INVALID", "Trendyol seçenek kimlikleri sayısal olmalıdır.");
                if (!emittedRemoteAttributeIds.Add(optionRemoteId)) continue;
                var optionRemote = remoteAttributes.SingleOrDefault(x => x.ExternalId == optionMapping.ExternalId);
                if (optionRemote is null) return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' seçeneği güncel kategori snapshot'ında bulunamadı.");
                var localValue = mappedLocalValues.FirstOrDefault(x => x.AttributeId == localAttribute.Id && NormalizeLabel(x.Value) == NormalizeLabel(option.ValueLabel));
                var optionPayload = new Dictionary<string, object?> { ["attributeId"] = optionRemoteId };
                if (localValue is not null)
                {
                    var externalValue = await ExternalValueAsync(tenantId, connectionId, categoryMapping.ExternalId, optionMapping.ExternalId, localValue.Id, cancellationToken);
                    if (externalValue.Error is not null) return ServiceResult<ProductPublicationDraft>.Fail(externalValue.Error.Code, externalValue.Error.Message, externalValue.Error.Status, externalValue.Error.FieldErrors);
                    optionPayload["attributeValueId"] = externalValue.Value;
                }
                else if (optionRemote.AllowsCustomValue == true)
                {
                    optionPayload["customAttributeValue"] = option.ValueLabel.Trim();
                }
                else
                {
                    return Fail("OPTION_VALUE_MAPPING_REQUIRED", $"'{option.Label}: {option.ValueLabel}' seçeneği için doğrulanmış Trendyol değer eşlemesi bulunamadı.");
                }
                payloadAttributes.Add(optionPayload);
            }
            if (requiredLocalIds.Any(id => !mappingByLocalId.TryGetValue(id, out var requiredMapping)
                || !long.TryParse(requiredMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var requiredRemoteId)
                || !emittedRemoteAttributeIds.Contains(requiredRemoteId)))
                return Fail("REQUIRED_ATTRIBUTE_MISSING", $"'{variant.Sku}' için Trendyol kategorisinin zorunlu özelliklerinden en az biri eksik.");

            var publishable = Math.Max(0, inventories[variant.Id].Available - offer.SafetyStock);
            var item = new Dictionary<string, object?>
            {
                ["barcode"] = variant.Barcode!.Trim(),
                ["title"] = title,
                ["description"] = description,
                ["productMainId"] = modelCodes[0],
                ["brandId"] = externalBrandId,
                ["categoryId"] = externalCategoryId,
                ["channels"] = new[] { "CORE" },
                ["quantity"] = checked((int)Math.Min(int.MaxValue, Math.Floor(publishable))),
                ["stockCode"] = variant.Sku.Trim(),
                ["origin"] = string.IsNullOrWhiteSpace(profile.Origin) ? null : profile.Origin.Trim().ToUpperInvariant(),
                ["listPrice"] = offer.ListPrice,
                ["salePrice"] = offer.SalePrice,
                ["vatRate"] = (int)offer.VatRate,
                ["deliveryOption"] = profile.DeliveryTimeDays is > 0 ? new Dictionary<string, object?> { ["deliveryDuration"] = profile.DeliveryTimeDays } : null,
                ["images"] = imageUrls.Select(url => new Dictionary<string, object?> { ["url"] = url }).ToList(),
                ["attributes"] = payloadAttributes
            };
            items.Add(item);
            draftVariants.Add(new PublicationVariantDraft(variant.Id, variant.Sku.Trim(), variant.Barcode.Trim()));
        }

        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["items"] = items }, JsonOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return ServiceResult<ProductPublicationDraft>.Ok(new(profile.Id, categoryMapping.ExternalId, brandMapping.ExternalId, payloadHash, payloadJson, draftVariants));
    }

    private async Task<ServiceResult<long>> ExternalValueAsync(Guid tenantId, Guid connectionId, string categoryExternalId, string attributeExternalId, Guid localValueId, CancellationToken cancellationToken)
    {
        var scope = $"{categoryExternalId}/{attributeExternalId}";
        var mapping = await db.AttributeValueMappings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localValueId && x.ScopeExternalId == scope && x.Status == "VERIFIED", cancellationToken);
        if (mapping is null || !long.TryParse(mapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return ServiceResult<long>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", "Özellik değeri için sayısal ve doğrulanmış Trendyol eşlemesi gerekir.", 422);
        return ServiceResult<long>.Ok(value);
    }

    private static string? CustomValue(ProductAttributeAssignment value) => value.TextValue?.Trim()
        ?? value.NumberValue?.ToString(CultureInfo.InvariantCulture)
        ?? value.BooleanValue?.ToString().ToLowerInvariant();

    private static string NormalizeLabel(string value) => value.Trim().ToUpperInvariant();

    private static bool IsValidBarcode(string value) => value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsPublicHttpsUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback) return false;
        return !IPAddress.TryParse(uri.Host, out var address) || IsPublicAddress(address);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast;
        var bytes = address.GetAddressBytes();
        return bytes[0] is not 0 and not 10 and not 127
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && bytes[0] < 224;
    }
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<ProductPublicationDraft> Fail(string code, string message, string? field = null, string? fieldMessage = null, int status = 422) => ServiceResult<ProductPublicationDraft>.Fail(code, message, status, field is null ? null : new Dictionary<string, string[]> { [field] = [fieldMessage ?? message] });
}
