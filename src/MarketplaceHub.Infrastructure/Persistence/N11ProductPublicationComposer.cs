using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record N11SkuAttribute(long Id, long? ValueId, string? CustomValue);
internal sealed record N11SkuDraft(string Title, string Description, long CategoryId, string ProductMainId, int PreparingDay, string ShipmentTemplate, string StockCode, string? Barcode, int Quantity, IReadOnlyList<string> Images, IReadOnlyList<N11SkuAttribute> Attributes, decimal SalePrice, decimal ListPrice, int VatRate);

// n11 product create body (developer.n11.com "Ürün Yükleme"). Every variant is one SKU
// keyed by its stock code (the variant SKU, which price/stock writes use too); SKUs that
// share productMainId (the model code) appear as one product. The brand is attribute 1
// sent as free text because n11 has no brand list. Other attributes are sent by n11 value
// id, or as free text where the attribute accepts it. The shipment template and preparing
// day come from the connection settings: n11 requires both and products have no source.
internal sealed class N11ProductPublicationComposer(AppDbContext db, IConfiguration configuration)
{
    internal const string BrandAttributeId = "1";
    internal const int DefaultPreparingDay = 3;
    internal const int MaxPreparingDay = 30;
    internal const int MaxShipmentTemplateLength = 100;
    private const int MaxStockCodeLength = 255;

    public async Task<ServiceResult<ProductPublicationDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null) return NotFound();
        if (product.Status == ProductStatus.Archived) return Fail("PRODUCT_ARCHIVED", "Arşivlenmiş ürün yayınlanamaz.");

        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "N11", cancellationToken);
        if (connection is null) return NotFound();
        var (shipmentTemplate, preparingDay) = PublishSettings(connection.SettingsJson);
        if (shipmentTemplate is null) return Fail("N11_SHIPMENT_TEMPLATE_REQUIRED", "N11'de ürün açmak için Entegrasyonlar bölümündeki N11 bağlantısına kargo şablonu adı girilmelidir. Ad, N11 satıcı panelindeki kargo şablonuyla birebir aynı olmalıdır.");
        if (preparingDay is < 1 or > MaxPreparingDay) return Fail("N11_PREPARING_DAY_INVALID", $"N11 bağlantısındaki hazırlık süresi 1-{MaxPreparingDay} gün arasında olmalıdır.");
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return Fail("LISTING_PROFILE_REQUIRED", "Yayın öncesi N11 bağlantısına ait listing profile oluşturulmalıdır.");
        if (!profile.Enabled) return Fail("LISTING_PROFILE_DISABLED", "Listing profile etkinleştirilmeden yayın işi oluşturulamaz.");

        var categoryMapping = product.CategoryId is Guid categoryId
            ? await db.CategoryMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == categoryId && x.Status == "VERIFIED")
                .Join(db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.IsCurrent), mapping => mapping.SnapshotId, snapshot => snapshot.Id, (mapping, _) => mapping)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (categoryMapping is null) return Fail("CATEGORY_MAPPING_REQUIRED", "Yayın öncesi güncel N11 kategori eşlemesi gerekir.", "categoryId");
        if (!string.IsNullOrWhiteSpace(profile.ExternalCategoryId) && profile.ExternalCategoryId != categoryMapping.ExternalId)
            return Fail("LISTING_MAPPING_CONFLICT", "Listing profile kategorisi güncel doğrulanmış N11 eşlemesiyle çelişiyor.", status: 409);
        if (!long.TryParse(categoryMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var remoteCategoryId) || remoteCategoryId <= 0)
            return Fail("CATEGORY_MAPPING_INVALID", "N11 kategori kimliği pozitif bir tam sayı olmalıdır.", "categoryId");
        // n11 lists products only on leaf categories.
        if (!await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SnapshotId == categoryMapping.SnapshotId && x.ResourceType == "CATEGORIES" && x.ExternalId == categoryMapping.ExternalId && x.IsActive && x.IsLeaf, cancellationToken))
            return Fail("CATEGORY_LEAF_REQUIRED", "N11'de ürün yalnız en alt kategoriye açılabilir; kategori eşlemesini bir alt kategoriye güncelleyin.", "categoryId");

        var categorySnapshot = await db.ReferenceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.ScopeExternalId == categoryMapping.ExternalId && x.IsCurrent, cancellationToken);
        if (categorySnapshot is null) return Fail("ATTRIBUTE_SNAPSHOT_REQUIRED", "Seçilen N11 kategorisinin güncel özellik snapshot'ı gerekir.");
        var remoteAttributes = await db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == categorySnapshot.Id && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive).ToListAsync(cancellationToken);
        var attributeMappings = await db.AttributeMappings.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == categoryMapping.ExternalId && x.SnapshotId == categorySnapshot.Id && x.Status == "VERIFIED").ToListAsync(cancellationToken);
        if (attributeMappings.GroupBy(x => x.ExternalId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", "Aynı N11 kategori özelliğine birden fazla yerel özellik eşlenmiş.", status: 409);
        var remoteById = remoteAttributes.ToDictionary(x => x.ExternalId, StringComparer.Ordinal);
        var mappingsByLocalId = attributeMappings.GroupBy(x => x.LocalId).ToDictionary(group => group.Key, group => (IReadOnlyList<AttributeMapping>)group.ToList());
        foreach (var mapping in attributeMappings)
        {
            if (!remoteById.ContainsKey(mapping.ExternalId)) return Fail("ATTRIBUTE_MAPPING_STALE", "Özellik eşlemesi güncel N11 kategori snapshot'ında bulunamadı.", status: 409);
            if (!long.TryParse(mapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return Fail("MAPPING_IDENTIFIER_INVALID", "N11 özellik kimlikleri sayısal olmalıdır.");
        }
        // The brand (attribute 1) is always filled from the product brand, so it needs no mapping.
        var missingRequiredExternalIds = RequiredAttributeMappingPolicy.FindMissingExternalIds(
            categorySnapshot.Id,
            remoteAttributes.Where(x => x.IsRequired == true && x.ExternalId != BrandAttributeId).Select(x => new RequiredAttributeMappingReference(categorySnapshot.Id, x.ExternalId)),
            attributeMappings.Select(x => new RequiredAttributeMappingReference(x.SnapshotId, x.ExternalId)));
        // Connection defaults fill the attributes a product leaves empty, so a required
        // attribute with a usable default needs no panel attribute mapping.
        var attributeDefaults = await ResolveAttributeDefaultsAsync(tenantId, connectionId, categoryMapping.ExternalId, remoteAttributes, cancellationToken);
        foreach (var required in remoteAttributes.Where(x => missingRequiredExternalIds.Contains(x.ExternalId) && !attributeDefaults.ContainsKey(x.ExternalId)))
            return Fail("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", $"Zorunlu N11 özelliği '{required.Name}' eşlenmemiş.");

        var brand = product.BrandId is Guid brandId
            ? await db.Brands.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == brandId && x.IsActive, cancellationToken)
            : null;
        if (brand is null || string.IsNullOrWhiteSpace(brand.Name)) return Fail("BRAND_REQUIRED", "N11 ürün açma için etkin bir yerel marka seçilmelidir.", "brandId");
        var brandName = brand.Name.Trim();

        var assignments = await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        if (assignments.Any(x => !mappingsByLocalId.ContainsKey(x.AttributeId))) return Fail("ATTRIBUTE_MAPPING_REQUIRED", "Üründe kullanılan her özellik güncel N11 kategori snapshot'ına eşlenmelidir.");
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId && x.Status != ProductStatus.Archived).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (variants.Count == 0) return Fail("PRODUCT_VARIANT_REQUIRED", "Yayın için en az bir etkin varyant gerekir.");
        if (variants.Count > N11PriceInventoryRules.MaxSkusPerRequest) return Fail("PRODUCT_BATCH_LIMIT_EXCEEDED", $"Tek N11 ürün açma isteğinde en fazla {N11PriceInventoryRules.MaxSkusPerRequest} varyant gönderilebilir.");
        if (variants.Any(x => string.IsNullOrWhiteSpace(x.Sku) || x.Sku.Trim().Length > MaxStockCodeLength)) return Fail("STOCK_CODE_INVALID", $"N11 stok kodu olarak kullanılan SKU boş olmamalı ve en fazla {MaxStockCodeLength} karakter olmalıdır.");
        if (variants.Select(x => x.Sku.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variants.Count) return Fail("STOCK_CODE_DUPLICATE", "N11 ürün açma isteğindeki stok kodları (SKU) benzersiz olmalıdır.");
        var barcodes = variants.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).Select(x => x.Barcode!.Trim()).ToList();
        if (barcodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != barcodes.Count) return Fail("BARCODE_DUPLICATE", "N11 ürün açma isteğindeki barkodlar benzersiz olmalıdır.");
        var modelCodes = variants.Select(x => x.ModelCode?.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (modelCodes.Count != 1 || string.IsNullOrWhiteSpace(modelCodes[0])) return Fail("MODEL_CODE_REQUIRED", "N11 varyant grubu tek bir ortak model kodu (productMainId) kullanmalıdır.");
        var productMainId = modelCodes[0]!;

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
        if (offers.Count != variants.Count) return Fail("CHANNEL_OFFER_REQUIRED", "Her varyant için etkin N11 fiyat teklifi gerekir.");
        if (inventories.Count != variants.Count) return Fail("INVENTORY_REQUIRED", "Her varyant için MAIN stok kaydı gerekir.");

        var media = await (from productMedia in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on productMedia.FileAssetId equals asset.Id
                           where productMedia.TenantId == tenantId && productMedia.ProductId == productId && productMedia.Status == "ACTIVE" && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby productMedia.SortOrder, productMedia.Id
                           select new { productMedia.VariantId, asset.Id, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        var title = (profile.TitleOverride ?? product.Title).Trim();
        var description = (profile.DescriptionOverride ?? product.Description).Trim();
        if (title.Length is < 15 or > 255) return Fail("PRODUCT_TITLE_INVALID", "N11 ürün başlığı 15-255 karakter olmalıdır.", "title");
        if (description.Length == 0) return Fail("PRODUCT_DESCRIPTION_INVALID", "N11 ürün açıklaması boş olamaz.");

        var skus = new List<N11SkuDraft>(variants.Count);
        var draftVariants = new List<PublicationVariantDraft>(variants.Count);
        foreach (var variant in variants)
        {
            var stockCode = variant.Sku.Trim();
            var offer = offers[variant.Id];
            var salePrice = N11PriceInventoryRules.Price(offer.SalePrice);
            var listPrice = N11PriceInventoryRules.Price(offer.ListPrice);
            if (!string.Equals(offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || salePrice <= 0 || listPrice < salePrice)
                return Fail("CHANNEL_OFFER_INVALID", $"'{variant.Sku}' için TRY para birimi ve listPrice >= salePrice > 0 kuralı sağlanmalıdır.");
            if (VatRate(offer.VatRate) is not int vatRate)
                return Fail("VAT_RATE_INVALID", $"'{variant.Sku}' için KDV oranı {offer.VatRate.ToString("0.##", CultureInfo.InvariantCulture)}; N11 yalnız 0, 1, 10 veya 20 kabul eder.");

            // Explicit variant media replaces the product gallery for that variant.
            var assignedMedia = media.Where(x => x.VariantId == variant.Id).ToList();
            var relevantMedia = assignedMedia.Count > 0 ? assignedMedia : media.Where(x => x.VariantId is null).ToList();
            if (relevantMedia.Where(x => x.Classification == "PRODUCT_MEDIA_URL").Any(x => !ProductPublicationComposer.IsPublicHttpsUrl(x.RelativePath.Trim())))
                return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", $"'{variant.Sku}' için görsel adresleri geçerli herkese açık HTTPS adresi olmalıdır.");
            var imageUrls = ProductPublicationComposer.SelectPublicationImageUrls(relevantMedia
                .Select(item => item.Classification == "PRODUCT_MEDIA_URL" ? item.RelativePath.Trim() : ProductPublicationComposer.BuildPublicProductMediaUrl(configuration["Marketplace:PublicBaseUrl"], item.Id))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!));
            if (imageUrls.Count == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", $"'{variant.Sku}' için en az bir herkese açık HTTPS ürün görseli gerekir.");

            var attributes = new List<N11SkuAttribute> { new(long.Parse(BrandAttributeId, CultureInfo.InvariantCulture), null, brandName) };
            var emittedIds = new HashSet<long> { attributes[0].Id };
            foreach (var group in assignments.Where(x => x.VariantId is null || x.VariantId == variant.Id).GroupBy(x => x.AttributeId))
            {
                if (!mappingsByLocalId.TryGetValue(group.Key, out var mappings) || mappings.Count == 0) return Fail("ATTRIBUTE_MAPPING_REQUIRED", "Üründe kullanılan özellik için doğrulanmış N11 eşlemesi bulunamadı.");
                if (mappings.Count != 1) return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", "Üründe kullanılan panel özelliği birden fazla N11 özelliğine eşlenmiş; tek bir özellik seçilmelidir.", status: 409);
                var mapping = mappings[0];
                // The product brand is the n11 brand; a panel attribute mapped to it is not sent.
                if (mapping.ExternalId == BrandAttributeId) continue;
                var remote = remoteById[mapping.ExternalId];
                var values = group.ToList();
                if (values.Count > 1) return Fail("ATTRIBUTE_MULTIVALUE_UNSUPPORTED", $"N11 '{remote.Name}' özelliği için tek değer kabul eder.");
                var resolved = await ResolveAssignmentAsync(tenantId, connectionId, categoryMapping.ExternalId, mapping.ExternalId, values[0], remote, localValues, cancellationToken);
                if (!resolved.Succeeded) return ServiceResult<ProductPublicationDraft>.Fail(resolved.Error!.Code, resolved.Error.Message, resolved.Error.Status, resolved.Error.FieldErrors);
                var attribute = resolved.Value!;
                if (!emittedIds.Add(attribute.Id)) return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", $"'{remote.Name}' özelliğine birden fazla değer eşlenmiş.", status: 409);
                attributes.Add(attribute);
            }

            foreach (var option in optionRows.Where(x => x.VariantId == variant.Id).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            {
                var localAttribute = localAttributes.FirstOrDefault(x => Normalize(option.Label) == Normalize(x.Name));
                if (localAttribute is null || !mappingsByLocalId.TryGetValue(localAttribute.Id, out var optionMappings)) return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' varyant seçeneği için N11 özellik eşlemesi gerekir.");
                if (optionMappings.Count != 1) return Fail("OPTION_MAPPING_AMBIGUOUS", $"'{option.Label}' varyant seçeneği birden fazla N11 özelliğine eşlenmiş; tek bir özellik seçilmelidir.", status: 409);
                var optionMapping = optionMappings[0];
                if (optionMapping.ExternalId == BrandAttributeId) continue;
                if (!remoteById.TryGetValue(optionMapping.ExternalId, out var optionRemote)) return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' eşlemesi güncel N11 kategori snapshot'ında bulunamadı.");
                var optionAttributeId = long.Parse(optionMapping.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture);
                var localValue = localValues.FirstOrDefault(x => x.AttributeId == localAttribute.Id && Normalize(x.Value) == Normalize(option.ValueLabel));
                N11SkuAttribute optionAttribute;
                if (localValue is not null)
                {
                    var valueId = await ResolveMappedValueIdAsync(tenantId, connectionId, categoryMapping.ExternalId, optionMapping.ExternalId, localValue.Id, localValue.Value, cancellationToken);
                    if (!valueId.Succeeded) return ServiceResult<ProductPublicationDraft>.Fail(valueId.Error!.Code, valueId.Error.Message, valueId.Error.Status, valueId.Error.FieldErrors);
                    optionAttribute = new(optionAttributeId, valueId.Value, null);
                }
                else if (optionRemote.AllowsCustomValue == true) optionAttribute = new(optionAttributeId, null, option.ValueLabel.Trim());
                else return Fail("OPTION_VALUE_MAPPING_REQUIRED", $"'{option.Label}: {option.ValueLabel}' seçeneği için güncel N11 değer eşlemesi gerekir.");
                if (!emittedIds.Add(optionAttributeId)) return Fail("ATTRIBUTE_ASSIGNMENT_AMBIGUOUS", $"'{optionRemote.Name}' için atama ile varyant seçeneği aynı anda tanımlanmış.");
                attributes.Add(optionAttribute);
            }

            // Defaults only fill attributes the product and its variant options left empty.
            foreach (var (_, defaultAttribute) in attributeDefaults.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                if (emittedIds.Add(defaultAttribute.Id)) attributes.Add(defaultAttribute);

            var missingRequired = remoteAttributes
                .Where(x => x.IsRequired == true && (!long.TryParse(x.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var requiredId) || !emittedIds.Contains(requiredId)))
                .Select(x => x.Name)
                .ToList();
            if (missingRequired.Count > 0) return Fail("REQUIRED_ATTRIBUTE_MISSING", $"'{variant.Sku}' için N11'in zorunlu özellikleri eksik: {string.Join(", ", missingRequired)}. Ürünün kategori özelliklerinde bu alanlara değer seçin ve değerlerini eşleyin.");

            var barcode = string.IsNullOrWhiteSpace(variant.Barcode) ? null : variant.Barcode.Trim();
            var quantity = (int)N11PriceInventoryRules.Quantity(inventories[variant.Id].Available - offer.SafetyStock);
            skus.Add(new(title, description, remoteCategoryId, productMainId, preparingDay, shipmentTemplate, stockCode, barcode, quantity, imageUrls, attributes, salePrice, listPrice, vatRate));
            draftVariants.Add(new(variant.Id, stockCode, barcode ?? ""));
        }

        var payloadJson = Payload(N11PriceInventoryRules.Integrator, skus);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return ServiceResult<ProductPublicationDraft>.Ok(new(profile.Id, categoryMapping.ExternalId, brandName, payloadHash, payloadJson, draftVariants));
    }

    // The n11 body: {"payload":{"integrator":...,"skus":[...]}}. Prices carry exactly two
    // decimals; catalogId stays null (no catalog match) and a missing barcode is left out.
    internal static string Payload(string integrator, IReadOnlyList<N11SkuDraft> skus)
    {
        var items = new JsonArray();
        foreach (var sku in skus)
        {
            var images = new JsonArray();
            for (var index = 0; index < sku.Images.Count; index++) images.Add(new JsonObject { ["url"] = sku.Images[index], ["order"] = index });
            var attributes = new JsonArray();
            foreach (var attribute in sku.Attributes) attributes.Add(new JsonObject { ["id"] = attribute.Id, ["valueId"] = attribute.ValueId, ["customValue"] = attribute.CustomValue });
            var item = new JsonObject
            {
                ["title"] = sku.Title,
                ["description"] = sku.Description,
                ["categoryId"] = sku.CategoryId,
                ["currencyType"] = "TL",
                ["productMainId"] = sku.ProductMainId,
                ["preparingDay"] = sku.PreparingDay,
                ["shipmentTemplate"] = sku.ShipmentTemplate,
                ["stockCode"] = sku.StockCode,
                ["catalogId"] = null,
                ["quantity"] = Math.Clamp(sku.Quantity, 0, N11PriceInventoryRules.MaxQuantity),
                ["images"] = images,
                ["attributes"] = attributes,
                ["salePrice"] = N11PriceInventoryRules.TwoDecimals(sku.SalePrice),
                ["listPrice"] = N11PriceInventoryRules.TwoDecimals(sku.ListPrice),
                ["vatRate"] = sku.VatRate
            };
            if (sku.Barcode is not null) item["barcode"] = sku.Barcode;
            items.Add(item);
        }
        return new JsonObject { ["payload"] = new JsonObject { ["integrator"] = integrator, ["skus"] = items } }.ToJsonString();
    }

    // n11 accepts only these VAT rates.
    internal static int? VatRate(decimal value) => value is 0m or 1m or 10m or 20m ? (int)value : null;

    // Shipment template and preparing day from the n11 connection settings; the
    // preparing day falls back to the default when it was never saved.
    internal static (string? ShipmentTemplate, int PreparingDay) PublishSettings(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, DefaultPreparingDay);
            var template = root.TryGetProperty("N11ShipmentTemplate", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()) ? name.GetString()!.Trim() : null;
            var preparingDay = root.TryGetProperty("N11PreparingDay", out var day) && day.ValueKind == JsonValueKind.Number && day.TryGetInt32(out var parsed) ? parsed : DefaultPreparingDay;
            return (template, preparingDay);
        }
        catch (JsonException) { return (null, DefaultPreparingDay); }
    }

    // One n11 value for a panel value: the only mapped value, or the one whose name equals
    // the panel value when several are mapped. Null when the choice is ambiguous.
    internal static long? SelectValueId(string localValue, IReadOnlyList<(string ExternalId, string Name)> candidates)
    {
        var usable = candidates.Where(candidate => long.TryParse(candidate.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out _)).ToList();
        var exact = usable.Where(candidate => Normalize(candidate.Name) == Normalize(localValue)).ToList();
        var chosen = exact.Count == 1 ? exact[0].ExternalId : exact.Count == 0 && usable.Count == 1 ? usable[0].ExternalId : null;
        return chosen is null ? null : long.Parse(chosen, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    // A list value goes by its mapped n11 value id; free text only where the attribute takes it.
    private async Task<ServiceResult<N11SkuAttribute>> ResolveAssignmentAsync(Guid tenantId, Guid connectionId, string categoryId, string attributeId, ProductAttributeAssignment assignment, ReferenceItem remote, IReadOnlyList<AttributeValue> localValues, CancellationToken cancellationToken)
    {
        var remoteAttributeId = long.Parse(attributeId, NumberStyles.None, CultureInfo.InvariantCulture);
        if (assignment.ValueId is Guid valueId)
        {
            var localValue = localValues.FirstOrDefault(value => value.AttributeId == assignment.AttributeId && value.Id == valueId);
            if (localValue is null) return ServiceResult<N11SkuAttribute>.Fail("ATTRIBUTE_VALUE_INVALID", $"'{remote.Name}' için seçilen yerel değer bulunamadı.", 422);
            var mapped = await ResolveMappedValueIdAsync(tenantId, connectionId, categoryId, attributeId, valueId, localValue.Value, cancellationToken);
            return mapped.Succeeded
                ? ServiceResult<N11SkuAttribute>.Ok(new(remoteAttributeId, mapped.Value, null))
                : ServiceResult<N11SkuAttribute>.Fail(mapped.Error!.Code, mapped.Error.Message, mapped.Error.Status, mapped.Error.FieldErrors);
        }
        var custom = assignment.TextValue?.Trim()
            ?? assignment.NumberValue?.ToString(CultureInfo.InvariantCulture)
            ?? assignment.BooleanValue?.ToString().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(custom) || remote.AllowsCustomValue != true) return ServiceResult<N11SkuAttribute>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{remote.Name}' serbest değer kabul etmiyor; doğrulanmış N11 değer eşlemesi gerekir.", 422);
        return ServiceResult<N11SkuAttribute>.Ok(new(remoteAttributeId, null, custom));
    }

    private async Task<ServiceResult<long>> ResolveMappedValueIdAsync(Guid tenantId, Guid connectionId, string categoryId, string attributeId, Guid localValueId, string localValue, CancellationToken cancellationToken)
    {
        var scope = $"{categoryId}/{attributeId}";
        var snapshotId = await db.ReferenceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId && snapshot.ResourceType == "ATTRIBUTE_VALUES" && snapshot.ScopeExternalId == scope && snapshot.IsCurrent)
            .OrderByDescending(snapshot => snapshot.FetchedAt)
            .Select(snapshot => (Guid?)snapshot.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshotId is null)
            return ServiceResult<long>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{localValue}' değeri güncel N11 değer listesinde eşlenmemiş.", 422);
        var externalIds = await db.AttributeValueMappings.AsNoTracking()
            .Where(mapping => mapping.TenantId == tenantId && mapping.ConnectionId == connectionId && mapping.LocalId == localValueId && mapping.ScopeExternalId == scope && mapping.SnapshotId == snapshotId && mapping.Status == "VERIFIED")
            .Select(mapping => mapping.ExternalId)
            .ToListAsync(cancellationToken);
        if (externalIds.Count == 0)
            return ServiceResult<long>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{localValue}' değeri güncel N11 değer listesinde eşlenmemiş.", 422);
        var remoteValues = await db.ReferenceItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.SnapshotId == snapshotId && item.ResourceType == "ATTRIBUTE_VALUES" && item.IsActive && externalIds.Contains(item.ExternalId))
            .Select(item => new { item.ExternalId, item.Name })
            .ToListAsync(cancellationToken);
        if (remoteValues.Count == 0)
            return ServiceResult<long>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{localValue}' için eşlenen N11 değeri güncel listede bulunamadı.", 422);
        return SelectValueId(localValue, remoteValues.Select(item => (item.ExternalId, item.Name)).ToList()) is long selected
            ? ServiceResult<long>.Ok(selected)
            : ServiceResult<long>.Fail("ATTRIBUTE_VALUE_MAPPING_AMBIGUOUS", $"'{localValue}' panel değeri birden fazla N11 değerine eşlenmiş. Ürün gönderimi için eşlemeyi tek bir karşılığa indirin.", 422);
    }

    // Connection defaults this category can use, keyed by attribute id. A list default counts only
    // while its value is active in the category's current value snapshot; a free-text default only
    // where the attribute accepts free text. The brand never comes from a default.
    private async Task<Dictionary<string, N11SkuAttribute>> ResolveAttributeDefaultsAsync(Guid tenantId, Guid connectionId, string categoryExternalId, IReadOnlyList<ReferenceItem> remoteAttributes, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, N11SkuAttribute>(StringComparer.Ordinal);
        var defaults = await db.ConnectionAttributeDefaults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .ToListAsync(cancellationToken);
        foreach (var attributeDefault in defaults)
        {
            var remote = remoteAttributes.FirstOrDefault(x => x.ExternalId == attributeDefault.ExternalAttributeId);
            if (remote is null || attributeDefault.ExternalAttributeId == BrandAttributeId || !long.TryParse(attributeDefault.ExternalAttributeId, NumberStyles.None, CultureInfo.InvariantCulture, out var attributeId)) continue;
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
                if (offered) resolved[attributeDefault.ExternalAttributeId] = new(attributeId, valueId, null);
            }
            else if (!string.IsNullOrWhiteSpace(attributeDefault.CustomValue) && remote.AllowsCustomValue == true)
            {
                resolved[attributeDefault.ExternalAttributeId] = new(attributeId, null, attributeDefault.CustomValue.Trim());
            }
        }
        return resolved;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static ServiceResult<ProductPublicationDraft> NotFound() => ServiceResult<ProductPublicationDraft>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<ProductPublicationDraft> Fail(string code, string message, string? field = null, int status = 422) => ServiceResult<ProductPublicationDraft>.Fail(code, message, status, field is null ? null : new Dictionary<string, string[]> { [field] = [message] });
}
