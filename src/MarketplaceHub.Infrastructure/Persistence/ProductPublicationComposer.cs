using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record PublicationVariantDraft(Guid VariantId, string Sku, string Barcode);
internal sealed record ProductPublicationDraft(Guid ProfileId, string ExternalCategoryId, string ExternalBrandId, string PayloadHash, string PayloadJson, IReadOnlyList<PublicationVariantDraft> Variants);

internal sealed class ProductPublicationComposer(AppDbContext db, IConfiguration configuration)
{
    internal const int MaximumPublicationImageCount = 8;
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
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platformCode == "HEPSIBURADA")
            return await new HepsiburadaProductPublicationComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (platformCode == "N11")
            return await new N11ProductPublicationComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (platformCode == "PAZARAMA")
            return await new PazaramaProductPublicationComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (platformCode == "PTTAVM")
            return await new PttAvmProductPublicationComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (ProductApprovalReconciliationPolicy.RequiresContentSplitReview(profile.LastRejectionCode))
            return Fail("PRODUCT_APPROVAL_CONTENT_SPLIT", "Trendyol varyantları birden fazla içerik kimliğine ayırdı. Yanlış ilana yazmamak için ürün içeriği güncellemesi durduruldu; varyant durumları ayrı ayrı izlenebilir.", status: 409);

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
        var mappingByLocalId = attributeMappings
            .GroupBy(x => x.LocalId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AttributeMapping>)group.OrderBy(x => x.ExternalId).ToList());
        var requiredExternalIds = new HashSet<string>(remoteAttributes.Where(x => x.IsRequired == true).Select(x => x.ExternalId), StringComparer.Ordinal);
        var missingRequiredExternalIds = RequiredAttributeMappingPolicy.FindMissingExternalIds(
            attributeSnapshot.Id,
            requiredExternalIds.Select(externalId => new RequiredAttributeMappingReference(attributeSnapshot.Id, externalId)),
            attributeMappings.Select(x => new RequiredAttributeMappingReference(x.SnapshotId, x.ExternalId)));
        // Connection defaults (e.g. Menşei = TR) cover the attributes a product leaves empty, so a
        // required attribute with a usable default needs no panel attribute mapping.
        var attributeDefaults = await ResolveAttributeDefaultsAsync(tenantId, connectionId, categoryMapping.ExternalId, remoteAttributes, cancellationToken);
        var fieldDefaults = await ProductFieldSources.LoadAsync(db, tenantId, connectionId, remoteAttributes, cancellationToken);
        foreach (var remote in remoteAttributes.Where(x => missingRequiredExternalIds.Contains(x.ExternalId) && !attributeDefaults.ContainsKey(x.ExternalId) && fieldDefaults.All(field => field.ExternalAttributeId != x.ExternalId)))
        {
            return Fail("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", $"Zorunlu Trendyol özelliği '{remote.Name}' eşlenmemiş.");
        }

        // A panel attribute the marketplace category has no field for (Cep on Hepsiburada Bluz) is not sent: only mapped
        // attributes travel. Required marketplace fields are still checked above and per variant.
        var assignments = (await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken))
            .Where(x => mappingByLocalId.ContainsKey(x.AttributeId)).ToList();
        var mappedLocalAttributes = await db.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenantId && mappingByLocalId.Keys.Contains(x.Id)).ToListAsync(cancellationToken);
        var mappedLocalValues = await db.AttributeValues.AsNoTracking().Where(x => x.TenantId == tenantId && mappingByLocalId.Keys.Contains(x.AttributeId) && x.IsActive).ToListAsync(cancellationToken);
        foreach (var assignment in assignments)
        {
            if (!mappingByLocalId.TryGetValue(assignment.AttributeId, out var localMappings)) return Fail("ATTRIBUTE_MAPPING_REQUIRED", "Üründe kullanılan her özellik seçili Trendyol kategorisinin güncel snapshot'ında eşlenmelidir.");
            var attributeMapping = SelectMappingForLocal(localMappings, remoteAttributes);
            var remote = remoteAttributes.SingleOrDefault(x => x.ExternalId == attributeMapping.ExternalId);
            if (remote is null) return Fail("ATTRIBUTE_MAPPING_REQUIRED", "Özellik eşlemesi güncel kategori snapshot'ında bulunamadı.");
            if (TrendyolColorValuePolicy.UsesCustomPanelColorValue(remote.Name))
            {
                if (assignment.ValueId is Guid colorValueId && !mappedLocalValues.Any(value => value.AttributeId == assignment.AttributeId && value.Id == colorValueId))
                    return Fail("ATTRIBUTE_VALUE_INVALID", $"'{remote.Name}' için seçilen panel renk değeri bulunamadı.");
                if (assignment.ValueId is null && string.IsNullOrWhiteSpace(CustomValue(assignment)))
                    return Fail("ATTRIBUTE_CUSTOM_VALUE_REQUIRED", $"'{remote.Name}' için boş olmayan renk değeri gerekir.");
                continue;
            }
            if (assignment.ValueId is not Guid valueId)
            {
                if (remote.AllowsCustomValue != true) return Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{remote.Name}' serbest değer kabul etmiyor; doğrulanmış değer eşlemesi gereklidir.");
                continue;
            }
            var valueScope = $"{categoryMapping.ExternalId}/{attributeMapping.ExternalId}";
            var valueMappings = await VerifiedValueMappingsAsync(tenantId, connectionId, valueScope, valueId, cancellationToken);
            if (valueMappings.InCurrentSnapshot == 0) return Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{remote.Name}' değeri güncel Trendyol değer snapshot'ında eşlenmemiş.");
            if (valueMappings.Usable.Count == 0) return Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{remote.Name}' değer eşlemesi snapshot içinde bulunamadı.");
        }

        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId && x.Status != ProductStatus.Archived).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (variants.Count == 0) return Fail("PRODUCT_VARIANT_REQUIRED", "Yayın için en az bir etkin varyant gerekir.");
        if (variants.Count > 1000) return Fail("PRODUCT_BATCH_LIMIT_EXCEEDED", "Tek yayın isteğinde en fazla 1000 varyant gönderilebilir.");
        if (variants.Any(x => string.IsNullOrWhiteSpace(x.Barcode) || x.Barcode.Trim().Length > 40 || !IsValidBarcode(x.Barcode.Trim()))) return Fail("BARCODE_INVALID", "Tüm varyantlarda en fazla 40 karakterlik; yalnız Türkçe karakter içermeyen harf, rakam, nokta, tire veya alt çizgiden oluşan barkod bulunmalıdır.");
        if (variants.Select(x => x.Barcode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variants.Count) return Fail("BARCODE_DUPLICATE", "Yayınlanan varyant barkodları benzersiz olmalıdır.");
        if (await ConnectionDuplicateAsync(db, tenantId, connectionId, variants.Select(x => x.Id).ToArray(), variants.Select(x => x.Barcode!.Trim()).ToArray(), [], cancellationToken) is { } takenBarcode)
            return Fail("BARCODE_DUPLICATE", ConnectionDuplicateMessage(takenBarcode), status: 409);
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
                           where productMedia.TenantId == tenantId && productMedia.ProductId == productId && productMedia.Status == "ACTIVE" && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby productMedia.SortOrder, productMedia.Id
                           select new { productMedia.VariantId, productMedia.SortOrder, asset.Id, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);

        var title = (profile.TitleOverride ?? product.Title).Trim();
        var description = (profile.DescriptionOverride ?? product.Description).Trim();
        if (title.Length is < 1 or > 100) return Fail("PRODUCT_TITLE_INVALID", "Trendyol yayın başlığı 1-100 karakter olmalıdır.");
        if (description.Length is < 1 or > 30000) return Fail("PRODUCT_DESCRIPTION_INVALID", "Trendyol açıklaması 1-30000 karakter olmalıdır.");
        if (!string.IsNullOrWhiteSpace(profile.Origin) && (profile.Origin.Trim().Length != 2 || !profile.Origin.Trim().All(char.IsLetter))) return Fail("ORIGIN_INVALID", "Menşei iki harfli ülke kodu olmalıdır.");
        // Teslimat Süresi: a listing's own override, else the product card's days (Trendyol deliveryOption.deliveryDuration).
        var deliveryDays = DeliveryDays(profile.DeliveryTimeDays, product.DeliveryDays);

        var items = new List<Dictionary<string, object?>>(variants.Count);
        var draftVariants = new List<PublicationVariantDraft>(variants.Count);
        foreach (var variant in variants)
        {
            var offer = offers[variant.Id];
            if (!string.Equals(offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || offer.SalePrice <= 0 || offer.ListPrice < offer.SalePrice) return Fail("CHANNEL_OFFER_INVALID", $"'{variant.Sku}' için TRY para birimi ve listPrice >= salePrice > 0 kuralı sağlanmalıdır.");
            // D11: Trendyol accepts only the Turkish VAT rates.
            if (!IsTrendyolVatRate(offer.VatRate)) return Fail("VAT_RATE_INVALID", $"'{variant.Sku}' için KDV oranı {offer.VatRate.ToString("0.##", CultureInfo.InvariantCulture)}; Trendyol için 0, 1, 10 veya 20 olmalıdır.");
            // D7 (K20): Trendyol prices the cargo by dimensionalWeight; a guessed 1 desi bills the wrong shipping cost.
            if (!HasDesi(variant.Desi)) return Fail("DESI_REQUIRED", $"'{variant.Sku}' için Trendyol'un zorunlu desi değeri girilmemiş.");

            // Explicit variant media replaces the product gallery for that
            // variant. This keeps marketplace colors from receiving every
            // product image while preserving the old product-level fallback.
            var assignedMedia = media.Where(x => x.VariantId == variant.Id).ToList();
            var relevantMedia = assignedMedia.Count > 0
                ? assignedMedia
                : media.Where(x => x.VariantId is null).ToList();
            var relevantUrls = relevantMedia.Where(x => x.Classification == "PRODUCT_MEDIA_URL").Select(x => x.RelativePath.Trim()).ToList();
            if (relevantUrls.Any(url => !IsPublicHttpsUrl(url))) return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", $"'{variant.Sku}' için kayıtlı tüm PRODUCT_MEDIA_URL değerleri geçerli HTTPS adresi olmalıdır.");
            var imageUrls = SelectPublicationImageUrls(relevantMedia
                .Select(item => item.Classification == "PRODUCT_MEDIA_URL"
                    ? item.RelativePath.Trim()
                    : BuildPublicProductMediaUrl(configuration["Marketplace:PublicBaseUrl"], item.Id))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!));
            if (imageUrls.Count == 0 && relevantMedia.Count > 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", $"'{variant.Sku}' için yerel görseller HTTPS üzerinden sunulamıyor. Marketplace:PublicBaseUrl ayarını yapılandırın.");
            if (imageUrls.Count == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", $"'{variant.Sku}' için en az bir geçerli HTTPS görsel adresi gerekir.");

            var effectiveAssignments = VariantAttributeAssignments.ForVariant(assignments, variant.Id).ToList();
            var payloadAttributes = new List<Dictionary<string, object?>>();
            var emittedRemoteAttributeIds = new HashSet<long>();
            // A Web Color chosen in the editor (manual) goes to every variant instead of the one each variant's Renk gives.
            if (product.TrendyolWebColorValueId is Guid manualWebColorId)
            {
                var manualWebColor = mappedLocalValues.FirstOrDefault(value => value.Id == manualWebColorId);
                var manualWebColorMapping = manualWebColor is null ? null : mappingByLocalId[manualWebColor.AttributeId]
                    .FirstOrDefault(item => IsWebColorOptionName(remoteAttributes.SingleOrDefault(remoteItem => remoteItem.ExternalId == item.ExternalId)?.Name));
                if (manualWebColor is null || manualWebColorMapping is null || !long.TryParse(manualWebColorMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var manualWebColorRemoteId))
                    return Fail("WEB_COLOR_MAPPING_REQUIRED", "Seçilen Web Color için Trendyol Web Color eşlemesi bulunamadı.");
                var manualWebColorValue = await ExternalValueAsync(tenantId, connectionId, categoryMapping.ExternalId, manualWebColorMapping.ExternalId, manualWebColor.Id, $"Web Color: {manualWebColor.Value}", cancellationToken);
                if (manualWebColorValue.Error is not null) return ServiceResult<ProductPublicationDraft>.Fail(manualWebColorValue.Error.Code, manualWebColorValue.Error.Message, manualWebColorValue.Error.Status, manualWebColorValue.Error.FieldErrors);
                payloadAttributes.Add(new Dictionary<string, object?> { ["attributeId"] = manualWebColorRemoteId, ["attributeValueId"] = manualWebColorValue.Value });
                emittedRemoteAttributeIds.Add(manualWebColorRemoteId);
            }
            foreach (var group in effectiveAssignments)
            {
                var localMappings = mappingByLocalId[group.Key];
                var mapping = SelectMappingForLocal(localMappings, remoteAttributes);
                var remote = remoteAttributes.Single(x => x.ExternalId == mapping.ExternalId);
                if (!long.TryParse(mapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var remoteAttributeId)) return Fail("MAPPING_IDENTIFIER_INVALID", "Trendyol özellik kimlikleri sayısal olmalıdır.");
                var values = group.ToList();
                var customColorValue = TrendyolColorValuePolicy.UsesCustomPanelColorValue(remote.Name);
                if (customColorValue && values.Count > 1) return Fail("ATTRIBUTE_ASSIGNMENT_AMBIGUOUS", $"'{remote.Name}' için aynı varyantta tek renk değeri gönderilebilir.");
                if (values.Count > 1 && remote.AllowsMultipleValues != true) return Fail("ATTRIBUTE_ASSIGNMENT_AMBIGUOUS", $"'{remote.Name}' birden fazla değer kabul etmiyor.");
                if (values.Count > 1 && values.Any(x => x.ValueId is null)) return Fail("ATTRIBUTE_ASSIGNMENT_AMBIGUOUS", $"'{remote.Name}' çoklu kullanımında yalnız eşlenmiş seçim değerleri kullanılabilir.");

                var attribute = new Dictionary<string, object?> { ["attributeId"] = remoteAttributeId };
                if (values.Count > 1)
                {
                    var remoteValues = new List<long>();
                    foreach (var value in values)
                    {
                        var externalValue = await ExternalValueAsync(tenantId, connectionId, categoryMapping.ExternalId, mapping.ExternalId, value.ValueId!.Value, $"{remote.Name}: {mappedLocalValues.FirstOrDefault(item => item.Id == value.ValueId!.Value)?.Value ?? "seçili değer"}", cancellationToken);
                        if (externalValue.Error is not null) return ServiceResult<ProductPublicationDraft>.Fail(externalValue.Error.Code, externalValue.Error.Message, externalValue.Error.Status, externalValue.Error.FieldErrors);
                        remoteValues.Add(externalValue.Value);
                    }
                    attribute["attributeValueIds"] = remoteValues;
                }
                else if (values[0].ValueId is Guid valueId)
                {
                    if (customColorValue)
                    {
                        var colorValue = mappedLocalValues.SingleOrDefault(value => value.AttributeId == group.Key && value.Id == valueId);
                        if (colorValue is null) return Fail("ATTRIBUTE_VALUE_INVALID", $"'{remote.Name}' için seçilen panel renk değeri bulunamadı.");
                        if (!TrendyolColorValuePolicy.TrySetCustomPanelColorValue(attribute, remote.Name, colorValue.Value))
                            return Fail("ATTRIBUTE_VALUE_INVALID", $"'{remote.Name}' için renk değeri özel alana dönüştürülemedi.");
                    }
                    else
                    {
                        var externalValue = await ExternalValueAsync(tenantId, connectionId, categoryMapping.ExternalId, mapping.ExternalId, valueId, $"{remote.Name}: {mappedLocalValues.FirstOrDefault(item => item.Id == valueId)?.Value ?? "seçili değer"}", cancellationToken);
                        if (externalValue.Error is not null) return ServiceResult<ProductPublicationDraft>.Fail(externalValue.Error.Code, externalValue.Error.Message, externalValue.Error.Status, externalValue.Error.FieldErrors);
                        attribute["attributeValueId"] = externalValue.Value;
                    }
                }
                else
                {
                    var customValue = CustomValue(values[0]);
                    if (string.IsNullOrWhiteSpace(customValue)) return Fail("ATTRIBUTE_CUSTOM_VALUE_REQUIRED", $"'{remote.Name}' için boş olmayan serbest değer gerekir.");
                    if (!TrendyolColorValuePolicy.TrySetCustomPanelColorValue(attribute, remote.Name, customValue))
                        attribute["customAttributeValue"] = customValue;
                }
                payloadAttributes.Add(attribute);
                emittedRemoteAttributeIds.Add(remoteAttributeId);
            }

            foreach (var option in optionRows.Where(x => x.VariantId == variant.Id).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            {
                var localAttribute = mappedLocalAttributes.FirstOrDefault(x => NormalizeLabel(x.Name) == NormalizeLabel(option.Label));
                if (localAttribute is null || !mappingByLocalId.TryGetValue(localAttribute.Id, out var optionMappings))
                    return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' seçeneği için güncel Trendyol özellik eşlemesi bulunamadı.");
                var optionMapping = SelectMappingForLocal(optionMappings, remoteAttributes);
                if (!long.TryParse(optionMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var optionRemoteId)) return Fail("MAPPING_IDENTIFIER_INVALID", "Trendyol seçenek kimlikleri sayısal olmalıdır.");
                var optionRemote = remoteAttributes.SingleOrDefault(x => x.ExternalId == optionMapping.ExternalId);
                if (optionRemote is null) return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' seçeneği güncel kategori snapshot'ında bulunamadı.");
                var localValue = mappedLocalValues.FirstOrDefault(x => x.AttributeId == localAttribute.Id && NormalizeLabel(x.Value) == NormalizeLabel(option.ValueLabel));
                var optionPayload = new Dictionary<string, object?> { ["attributeId"] = optionRemoteId };
                var hasCustomColorValue = TrendyolColorValuePolicy.TrySetCustomPanelColorValue(optionPayload, optionRemote.Name, option.ValueLabel);
                if (!hasCustomColorValue && localValue is not null)
                {
                    var externalValue = await ExternalValueAsync(tenantId, connectionId, categoryMapping.ExternalId, optionMapping.ExternalId, localValue.Id, $"{option.Label}: {option.ValueLabel}", cancellationToken);
                    if (externalValue.Error is not null) return ServiceResult<ProductPublicationDraft>.Fail(externalValue.Error.Code, externalValue.Error.Message, externalValue.Error.Status, externalValue.Error.FieldErrors);
                    optionPayload["attributeValueId"] = externalValue.Value;
                }
                else if (!hasCustomColorValue && optionRemote.AllowsCustomValue == true)
                {
                    optionPayload["customAttributeValue"] = option.ValueLabel.Trim();
                }
                else if (!hasCustomColorValue)
                {
                    return Fail("OPTION_VALUE_MAPPING_REQUIRED", $"'{option.Label}: {option.ValueLabel}' seçeneği için doğrulanmış Trendyol değer eşlemesi bulunamadı.");
                }
                if (emittedRemoteAttributeIds.Add(optionRemoteId)) payloadAttributes.Add(optionPayload);
                var webColorMapping = optionMappings.FirstOrDefault(item => IsWebColorOptionName(remoteAttributes.SingleOrDefault(remoteItem => remoteItem.ExternalId == item.ExternalId)?.Name));
                if (localValue is not null && webColorMapping is not null && webColorMapping.ExternalId != optionMapping.ExternalId && long.TryParse(webColorMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var webColorRemoteId) && emittedRemoteAttributeIds.Add(webColorRemoteId))
                {
                    var webColorValue = await ExternalValueAsync(tenantId, connectionId, categoryMapping.ExternalId, webColorMapping.ExternalId, localValue.Id, $"Web Color: {option.ValueLabel}", cancellationToken);
                    if (webColorValue.Error is not null) return ServiceResult<ProductPublicationDraft>.Fail(webColorValue.Error.Code, webColorValue.Error.Message, webColorValue.Error.Status, webColorValue.Error.FieldErrors);
                    payloadAttributes.Add(new Dictionary<string, object?> { ["attributeId"] = webColorRemoteId, ["attributeValueId"] = webColorValue.Value });
                }
            }
            // Defaults only fill attributes the product and its variant options left empty.
            foreach (var (_, defaultAttribute) in attributeDefaults.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (emittedRemoteAttributeIds.Add((long)defaultAttribute["attributeId"]!)) payloadAttributes.Add(new Dictionary<string, object?>(defaultAttribute));
            }
            // Product fields (the variant's own model code, stock code, barcode or title) fill what the product left empty.
            foreach (var field in fieldDefaults)
            {
                if (!long.TryParse(field.ExternalAttributeId, NumberStyles.None, CultureInfo.InvariantCulture, out var fieldAttributeId) || emittedRemoteAttributeIds.Contains(fieldAttributeId)) continue;
                if (ProductFieldSources.Value(field.Field, variant, title) is not { } fieldValue) return Fail("PRODUCT_FIELD_EMPTY", ProductFieldSources.MissingValueMessage(variant.Sku, field));
                emittedRemoteAttributeIds.Add(fieldAttributeId);
                payloadAttributes.Add(new Dictionary<string, object?> { ["attributeId"] = fieldAttributeId, ["customAttributeValue"] = fieldValue });
            }
            if (requiredExternalIds.Any(id => !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var requiredRemoteId) || !emittedRemoteAttributeIds.Contains(requiredRemoteId)))
            {
                var missingRequired = requiredExternalIds.Where(id => !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var requiredRemoteId) || !emittedRemoteAttributeIds.Contains(requiredRemoteId)).Select(id => remoteAttributes.FirstOrDefault(item => item.ExternalId == id)?.Name ?? id).Distinct().ToList();
                return Fail("REQUIRED_ATTRIBUTE_MISSING", $"'{variant.Sku}' için Trendyol'un zorunlu özellikleri eksik: {string.Join(", ", missingRequired)}. Ürünün kategori özelliklerinde bu alanlara değer seçin ve değerlerini eşleyin.");
            }

            var publishable = Math.Max(0, inventories[variant.Id].Available - offer.SafetyStock);
            var item = new Dictionary<string, object?>
            {
                ["barcode"] = variant.Barcode!.Trim(),
                ["title"] = title,
                ["description"] = description,
                ["productMainId"] = modelCodes[0],
                ["brandId"] = externalBrandId,
                ["categoryId"] = externalCategoryId,
                ["quantity"] = TrendyolStockRules.Quantity(publishable),
                ["stockCode"] = variant.Sku.Trim(),
                ["dimensionalWeight"] = variant.Desi.GetValueOrDefault(),
                ["origin"] = string.IsNullOrWhiteSpace(profile.Origin) ? null : profile.Origin.Trim().ToUpperInvariant(),
                ["listPrice"] = PriceInventoryComposer.OutboundListPrice(offer),
                ["salePrice"] = PriceInventoryComposer.OutboundSalePrice(offer),
                ["vatRate"] = (int)offer.VatRate,
                ["deliveryOption"] = deliveryDays is > 0 ? new Dictionary<string, object?> { ["deliveryDuration"] = deliveryDays } : null,
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

    // Connection defaults this category can use, as ready payload entries keyed by attribute id. A
    // list default counts only while its value is active in the category's current value snapshot;
    // a free-text default only where the attribute accepts free text.
    private async Task<Dictionary<string, Dictionary<string, object?>>> ResolveAttributeDefaultsAsync(Guid tenantId, Guid connectionId, string categoryExternalId, IReadOnlyList<ReferenceItem> remoteAttributes, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var defaults = await db.ConnectionAttributeDefaults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .ToListAsync(cancellationToken);
        foreach (var attributeDefault in defaults)
        {
            var remote = remoteAttributes.FirstOrDefault(x => x.ExternalId == attributeDefault.ExternalAttributeId);
            if (remote is null || !long.TryParse(attributeDefault.ExternalAttributeId, NumberStyles.None, CultureInfo.InvariantCulture, out var attributeId)) continue;
            if (!string.IsNullOrWhiteSpace(attributeDefault.ExternalValueId))
            {
                if (!long.TryParse(attributeDefault.ExternalValueId, NumberStyles.None, CultureInfo.InvariantCulture, out var valueId)) continue;
                var scope = $"{categoryExternalId}/{attributeDefault.ExternalAttributeId}";
                var offered = await (from snapshot in db.ReferenceSnapshots.AsNoTracking()
                                     join item in db.ReferenceItems.AsNoTracking() on snapshot.Id equals item.SnapshotId
                                     where snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId && snapshot.ResourceType == "ATTRIBUTE_VALUES"
                                         && snapshot.ScopeExternalId == scope && snapshot.IsCurrent
                                         && item.TenantId == tenantId && item.ResourceType == "ATTRIBUTE_VALUES" && item.ExternalId == attributeDefault.ExternalValueId && item.IsActive
                                     select item.Id).AnyAsync(cancellationToken);
                if (offered) resolved[attributeDefault.ExternalAttributeId] = new() { ["attributeId"] = attributeId, ["attributeValueId"] = valueId };
            }
            else if (!string.IsNullOrWhiteSpace(attributeDefault.CustomValue) && remote.AllowsCustomValue == true)
            {
                resolved[attributeDefault.ExternalAttributeId] = new() { ["attributeId"] = attributeId, ["customAttributeValue"] = attributeDefault.CustomValue.Trim() };
            }
        }
        return resolved;
    }

    private async Task<ServiceResult<long>> ExternalValueAsync(Guid tenantId, Guid connectionId, string categoryExternalId, string attributeExternalId, Guid localValueId, string label, CancellationToken cancellationToken)
    {
        var scope = $"{categoryExternalId}/{attributeExternalId}";
        var mappings = await VerifiedValueMappingsAsync(tenantId, connectionId, scope, localValueId, cancellationToken);
        return ChooseUsableExternalValue(mappings.Usable) is { } value
            ? ServiceResult<long>.Ok(value)
            : ServiceResult<long>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", mappings.All.Count > 0
                ? $"'{label}' için Trendyol değer eşlemesi güncel değer listesinde yok veya değer artık etkin değil. Eşleştirme Ayarları'ndan bu panel değerini güncel bir Trendyol değeriyle yeniden eşleyin."
                : $"'{label}' için doğrulanmış Trendyol değer eşlemesi yok. Eşleştirme Ayarları'ndan bu panel değerini bir Trendyol değeriyle eşleyin.", 422);
    }

    // Only a mapping in the current value snapshot that points at an active Trendyol value may be sent,
    // the same rule the readiness check applies. An older mapping outside the snapshot is not a fallback:
    // Trendyol rejects a value id it no longer offers for the category.
    internal static long? ChooseUsableExternalValue(IReadOnlyList<AttributeValueMapping> usable) => ChooseExternalValue(usable);

    // A barcode (or stock code) another product's variant already uses on the same connection: the
    // marketplace keeps one listing per code in a store, so creating a second one would collide.
    internal static async Task<string?> ConnectionDuplicateAsync(AppDbContext db, Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, IReadOnlyCollection<string> barcodes, IReadOnlyCollection<string> linkKeys, CancellationToken cancellationToken)
    {
        if (barcodes.Count > 0)
        {
            var listed = await (from listing in db.ChannelListingVariants.AsNoTracking()
                                join profile in db.ChannelListingProfiles.AsNoTracking() on new { listing.TenantId, Id = listing.ProfileId } equals new { profile.TenantId, profile.Id }
                                where listing.TenantId == tenantId && profile.ConnectionId == connectionId
                                    && listing.ExternalBarcode != null && barcodes.Contains(listing.ExternalBarcode) && !variantIds.Contains(listing.VariantId)
                                orderby listing.ExternalBarcode
                                select listing.ExternalBarcode).FirstOrDefaultAsync(cancellationToken);
            if (listed is not null) return listed;
        }
        if (linkKeys.Count == 0) return null;
        return await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(link => link.TenantId == tenantId && link.ConnectionId == connectionId && linkKeys.Contains(link.ExternalId) && !variantIds.Contains(link.VariantId))
            .OrderBy(link => link.ExternalId)
            .Select(link => link.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    internal static string ConnectionDuplicateMessage(string code) =>
        $"'{code}' kodu bu bağlantıda başka bir ürünün varyantında kullanılıyor; aynı mağazada iki ürün aynı barkodu veya stok kodunu kullanamaz.";

    private sealed record ValueMappingCandidates(IReadOnlyList<AttributeValueMapping> All, int InCurrentSnapshot, IReadOnlyList<AttributeValueMapping> Usable);

    // Verified mappings of one panel value in one scope, plus the ones that are in the current
    // value snapshot and point at an active Trendyol value.
    private async Task<ValueMappingCandidates> VerifiedValueMappingsAsync(Guid tenantId, Guid connectionId, string scope, Guid localValueId, CancellationToken cancellationToken)
    {
        var all = await db.AttributeValueMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localValueId && x.ScopeExternalId == scope && x.Status == "VERIFIED")
            .ToListAsync(cancellationToken);
        if (all.Count == 0) return new(all, 0, []);
        var currentSnapshotIds = await db.ReferenceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId && snapshot.ResourceType == "ATTRIBUTE_VALUES" && snapshot.ScopeExternalId == scope && snapshot.IsCurrent)
            .Select(snapshot => snapshot.Id)
            .ToListAsync(cancellationToken);
        var current = all.Where(mapping => currentSnapshotIds.Contains(mapping.SnapshotId)).ToList();
        if (current.Count == 0) return new(all, 0, []);
        var snapshotIds = current.Select(mapping => mapping.SnapshotId).Distinct().ToArray();
        var externalIds = current.Select(mapping => mapping.ExternalId).Distinct().ToArray();
        var active = (await db.ReferenceItems.AsNoTracking()
                .Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.SnapshotId) && item.ResourceType == "ATTRIBUTE_VALUES" && externalIds.Contains(item.ExternalId) && item.IsActive)
                .Select(item => new { item.SnapshotId, item.ExternalId })
                .ToListAsync(cancellationToken))
            .Select(item => (item.SnapshotId, item.ExternalId))
            .ToHashSet();
        return new(all, current.Count, current.Where(mapping => active.Contains((mapping.SnapshotId, mapping.ExternalId))).ToList());
    }

    // One panel value may be mapped to several Trendyol values in the same scope (for example a
    // value name Trendyol lists twice). Only one can be sent, so the most recently verified mapping
    // — the user's latest choice — wins; ids are time-ordered (UUIDv7) and break ties. Before, the
    // single-row lookup threw and the publish job ended in manual review.
    internal static long? ChooseExternalValue(IEnumerable<AttributeValueMapping> mappings) => mappings
        .OrderByDescending(mapping => mapping.VerifiedAt ?? DateTimeOffset.MinValue)
        .ThenByDescending(mapping => mapping.Id)
        .Select(mapping => long.TryParse(mapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : (long?)null)
        .FirstOrDefault(value => value is not null);

    private static AttributeMapping SelectMappingForLocal(IReadOnlyList<AttributeMapping> mappings, IReadOnlyList<ReferenceItem> remoteAttributes) => mappings
        .OrderBy(mapping => IsWebColorOptionName(remoteAttributes.FirstOrDefault(item => item.ExternalId == mapping.ExternalId)?.Name) ? 1 : 0)
        .ThenBy(mapping => mapping.ExternalId, StringComparer.Ordinal)
        .First();

    private static bool IsWebColorOptionName(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return normalized is "WEBCOLOR" or "WEBCOLOUR" or "WEBRENK";
    }

    private static string? CustomValue(ProductAttributeAssignment value) => value.TextValue?.Trim()
        ?? value.NumberValue?.ToString(CultureInfo.InvariantCulture)
        ?? value.BooleanValue?.ToString().ToLowerInvariant();

    private static string NormalizeLabel(string value) => value.Trim().ToUpperInvariant();

    internal static bool HasDesi(decimal? desi) => desi is > 0;

    internal static bool IsTrendyolVatRate(decimal rate) => rate is 0m or 1m or 10m or 20m;

    // The delivery days a marketplace gets: a listing's own override, else the product card's Teslimat Süresi; none of
    // them (a card that predates the field) sends no delivery time, as before.
    internal static int? DeliveryDays(int? listingDays, int? productDays) => listingDays is > 0 ? listingDays : productDays is > 0 ? productDays : null;

    private static bool IsValidBarcode(string value) => value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    public static bool IsPublicHttpsUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback || !IsPublicHost(uri.Host)) return false;
        return !IPAddress.TryParse(uri.Host, out var address) || IsPublicAddress(address);
    }

    internal static string? BuildPublicProductMediaUrl(string? publicBaseUrl, Guid assetId)
    {
        if (!IsPublicHttpsUrl(publicBaseUrl ?? "") || !Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.AbsolutePath != "/" || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment)) return null;
        var url = new Uri(baseUri, $"/api/v1/public/product-media/{assetId:D}/content").AbsoluteUri;
        return url.Length <= 512 ? url : null;
    }

    internal static IReadOnlyList<string> SelectPublicationImageUrls(IEnumerable<string> imageUrls) => imageUrls
        .Where(url => !string.IsNullOrWhiteSpace(url))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaximumPublicationImageCount)
        .ToArray();

    private static bool IsPublicHost(string host)
    {
        var normalized = host.TrimEnd('.');
        return !string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
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
