using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed record PazaramaAttributeDraft(string AttributeId, string ValueId);
internal sealed record PazaramaProductDraft(
    string Name, string DisplayName, string Description, string BrandId, string CategoryId, string GroupCode, string Code, string StockCode,
    int Desi, int StockCount, int VatRate, decimal ListPrice, decimal SalePrice, IReadOnlyList<string> Images, IReadOnlyList<PazaramaAttributeDraft> Attributes);

// Pazarama product create body (isortagim.pazarama.com "Ürün Ekleme"). Every variant is one product keyed
// by its barcode (code); variants of one model share a groupCode. The category must be a leaf, the brand
// comes from the verified brand mapping, and every attribute goes by value id: Pazarama has no free text,
// so a panel value without a verified value mapping stops the publication with a clear message.
internal sealed class PazaramaProductPublicationComposer(AppDbContext db, IConfiguration configuration)
{
    internal const int MaxNameLength = 100;
    internal const int MaxGroupCodeLength = 10;
    private static readonly IReadOnlySet<int> VatRates = new HashSet<int> { 0, 1, 10, 20 };

    public async Task<ServiceResult<ProductPublicationDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null) return NotFound();
        if (product.Status == ProductStatus.Archived) return Fail("PRODUCT_ARCHIVED", "Arşivlenmiş ürün yayınlanamaz.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PAZARAMA", cancellationToken);
        if (connection is null) return NotFound();
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return Fail("LISTING_PROFILE_REQUIRED", "Yayın öncesi Pazarama bağlantısına ait listing profile oluşturulmalıdır.");
        if (!profile.Enabled) return Fail("LISTING_PROFILE_DISABLED", "Listing profile etkinleştirilmeden yayın işi oluşturulamaz.");

        var categoryMapping = product.CategoryId is Guid categoryId
            ? await db.CategoryMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == categoryId && x.Status == "VERIFIED")
                .Join(db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.IsCurrent), mapping => mapping.SnapshotId, snapshot => snapshot.Id, (mapping, _) => mapping)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (categoryMapping is null) return Fail("CATEGORY_MAPPING_REQUIRED", "Yayın öncesi güncel Pazarama kategori eşlemesi gerekir.", "categoryId");
        if (!Guid.TryParse(categoryMapping.ExternalId, out var remoteCategoryId) || remoteCategoryId == Guid.Empty)
            return Fail("CATEGORY_MAPPING_INVALID", "Pazarama kategori kimliği bir GUID olmalıdır.", "categoryId");
        if (!string.IsNullOrWhiteSpace(profile.ExternalCategoryId) && profile.ExternalCategoryId != categoryMapping.ExternalId)
            return Fail("LISTING_MAPPING_CONFLICT", "Listing profile kategorisi güncel doğrulanmış Pazarama eşlemesiyle çelişiyor.", status: 409);
        if (!await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SnapshotId == categoryMapping.SnapshotId && x.ResourceType == "CATEGORIES" && x.ExternalId == categoryMapping.ExternalId && x.IsActive && x.IsLeaf, cancellationToken))
            return Fail("CATEGORY_LEAF_REQUIRED", "Pazarama'da ürün yalnız en alt kategoriye açılabilir; kategori eşlemesini bir alt kategoriye güncelleyin.", "categoryId");

        var brandMapping = product.BrandId is Guid brandId
            ? await db.BrandMappings.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == brandId && x.Status == "VERIFIED")
                .Join(db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.IsCurrent), mapping => mapping.SnapshotId, snapshot => snapshot.Id, (mapping, _) => mapping)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (brandMapping is null) return Fail("BRAND_MAPPING_REQUIRED", "Yayın öncesi güncel Pazarama marka eşlemesi gerekir (Eşleştirme sayfasında marka eşlemesi).", "brandId");
        if (!Guid.TryParse(brandMapping.ExternalId, out var remoteBrandId) || remoteBrandId == Guid.Empty)
            return Fail("MAPPING_IDENTIFIER_INVALID", "Pazarama marka kimliği bir GUID olmalıdır.", "brandId");
        if (!string.IsNullOrWhiteSpace(profile.ExternalBrandId) && profile.ExternalBrandId != brandMapping.ExternalId)
            return Fail("LISTING_MAPPING_CONFLICT", "Listing profile markası güncel doğrulanmış Pazarama eşlemesiyle çelişiyor.", status: 409);

        var categorySnapshot = await db.ReferenceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.ScopeExternalId == categoryMapping.ExternalId && x.IsCurrent, cancellationToken);
        if (categorySnapshot is null) return Fail("ATTRIBUTE_SNAPSHOT_REQUIRED", "Seçilen Pazarama kategorisinin güncel özellik snapshot'ı gerekir.");
        var remoteAttributes = await db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == categorySnapshot.Id && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive).ToListAsync(cancellationToken);
        var attributeMappings = await db.AttributeMappings.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == categoryMapping.ExternalId && x.SnapshotId == categorySnapshot.Id && x.Status == "VERIFIED").ToListAsync(cancellationToken);
        if (attributeMappings.GroupBy(x => x.ExternalId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", "Aynı Pazarama kategori özelliğine birden fazla yerel özellik eşlenmiş.", status: 409);
        var remoteById = remoteAttributes.ToDictionary(x => x.ExternalId, StringComparer.OrdinalIgnoreCase);
        var mappingsByLocalId = attributeMappings.GroupBy(x => x.LocalId).ToDictionary(group => group.Key, group => (IReadOnlyList<AttributeMapping>)group.ToList());
        foreach (var mapping in attributeMappings)
        {
            if (!remoteById.ContainsKey(mapping.ExternalId)) return Fail("ATTRIBUTE_MAPPING_STALE", "Özellik eşlemesi güncel Pazarama kategori snapshot'ında bulunamadı.", status: 409);
            if (!Guid.TryParse(mapping.ExternalId, out _)) return Fail("MAPPING_IDENTIFIER_INVALID", "Pazarama özellik kimlikleri GUID olmalıdır.");
        }
        var missingRequiredExternalIds = RequiredAttributeMappingPolicy.FindMissingExternalIds(
            categorySnapshot.Id,
            remoteAttributes.Where(x => x.IsRequired == true).Select(x => new RequiredAttributeMappingReference(categorySnapshot.Id, x.ExternalId)),
            attributeMappings.Select(x => new RequiredAttributeMappingReference(x.SnapshotId, x.ExternalId)));
        var attributeDefaults = await ResolveAttributeDefaultsAsync(tenantId, connectionId, categoryMapping.ExternalId, remoteAttributes, cancellationToken);
        foreach (var required in remoteAttributes.Where(x => missingRequiredExternalIds.Contains(x.ExternalId) && !attributeDefaults.ContainsKey(x.ExternalId)))
            return Fail("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", $"Zorunlu Pazarama özelliği '{required.Name}' eşlenmemiş.");

        // A panel attribute the marketplace category has no field for (Cep on Hepsiburada Bluz) is not sent: only mapped
        // attributes travel. Required marketplace fields are still checked above and per variant.
        var assignments = (await db.ProductAttributeAssignments.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken))
            .Where(x => mappingsByLocalId.ContainsKey(x.AttributeId)).ToList();
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId && x.Status != ProductStatus.Archived).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (variants.Count == 0) return Fail("PRODUCT_VARIANT_REQUIRED", "Yayın için en az bir etkin varyant gerekir.");
        if (variants.Count > PazaramaProductPayloadRules.MaxProductsPerRequest) return Fail("PRODUCT_BATCH_LIMIT_EXCEEDED", $"Tek Pazarama ürün açma isteğinde en fazla {PazaramaProductPayloadRules.MaxProductsPerRequest} varyant gönderilebilir.");
        if (variants.Any(x => string.IsNullOrWhiteSpace(x.Barcode) || x.Barcode.Trim().Length > 500)) return Fail("BARCODE_REQUIRED", "Pazarama ürün açma için her varyantın barkodu (en fazla 500 karakter) olmalıdır.");
        if (variants.Any(x => string.IsNullOrWhiteSpace(x.Sku) || x.Sku.Trim().Length > 100)) return Fail("STOCK_CODE_INVALID", "Pazarama stok kodu olarak kullanılan SKU boş olmamalı ve en fazla 100 karakter olmalıdır.");
        if (variants.Select(x => x.Barcode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variants.Count) return Fail("BARCODE_DUPLICATE", "Pazarama ürün açma isteğindeki barkodlar benzersiz olmalıdır.");
        // Pazarama keys a listing by its barcode (the variant link carries it).
        var publishedBarcodes = variants.Select(x => x.Barcode!.Trim()).ToArray();
        if (await ProductPublicationComposer.ConnectionDuplicateAsync(db, tenantId, connectionId, variants.Select(x => x.Id).ToArray(), publishedBarcodes, publishedBarcodes, cancellationToken) is { } takenBarcode)
            return Fail("BARCODE_DUPLICATE", ProductPublicationComposer.ConnectionDuplicateMessage(takenBarcode), status: 409);
        var modelCodes = variants.Select(x => x.ModelCode?.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (modelCodes.Count != 1 || string.IsNullOrWhiteSpace(modelCodes[0])) return Fail("MODEL_CODE_REQUIRED", "Pazarama varyant grubu tek bir ortak model kodu kullanmalıdır.");
        var groupCode = GroupCode(modelCodes[0]!);

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
        if (offers.Count != variants.Count) return Fail("CHANNEL_OFFER_REQUIRED", "Her varyant için etkin Pazarama fiyat teklifi gerekir.");
        if (inventories.Count != variants.Count) return Fail("INVENTORY_REQUIRED", "Her varyant için MAIN stok kaydı gerekir.");

        var media = await (from productMedia in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on productMedia.FileAssetId equals asset.Id
                           where productMedia.TenantId == tenantId && productMedia.ProductId == productId && productMedia.Status == "ACTIVE" && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null
                           orderby productMedia.SortOrder, productMedia.Id
                           select new { productMedia.VariantId, asset.Id, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        var title = (profile.TitleOverride ?? product.Title).Trim();
        var description = (profile.DescriptionOverride ?? product.Description).Trim();
        if (title.Length is < 1 or > MaxNameLength) return Fail("PRODUCT_TITLE_INVALID", $"Pazarama ürün adı 1-{MaxNameLength} karakter olmalıdır; listing profilinde kısa bir başlık girin.", "title");
        if (description.Length == 0) return Fail("PRODUCT_DESCRIPTION_INVALID", "Pazarama ürün açıklaması boş olamaz.");

        var drafts = new List<PazaramaProductDraft>(variants.Count);
        var draftVariants = new List<PublicationVariantDraft>(variants.Count);
        foreach (var variant in variants)
        {
            var barcode = variant.Barcode!.Trim();
            var offer = offers[variant.Id];
            var salePrice = PazaramaPriceInventoryRules.Price(PriceInventoryComposer.OutboundSalePrice(offer));
            var listPrice = PazaramaPriceInventoryRules.Price(PriceInventoryComposer.OutboundListPrice(offer));
            if (!string.Equals(offer.Currency, "TRY", StringComparison.OrdinalIgnoreCase) || salePrice <= 0 || listPrice < salePrice)
                return Fail("CHANNEL_OFFER_INVALID", $"'{variant.Sku}' için TRY para birimi ve listPrice >= salePrice > 0 kuralı sağlanmalıdır.");
            if (offer.VatRate != decimal.Truncate(offer.VatRate) || !VatRates.Contains((int)offer.VatRate))
                return Fail("VAT_RATE_INVALID", $"'{variant.Sku}' için KDV oranı {offer.VatRate.ToString("0.##", CultureInfo.InvariantCulture)}; Pazarama için 0, 1, 10 veya 20 olmalıdır.");
            if (variant.Desi is not > 0) return Fail("DESI_REQUIRED", $"'{variant.Sku}' için Pazarama'nın zorunlu desi değeri girilmemiş.");
            var desi = (int)Math.Ceiling(variant.Desi.Value);

            var assignedMedia = media.Where(x => x.VariantId == variant.Id).ToList();
            var relevantMedia = assignedMedia.Count > 0 ? assignedMedia : media.Where(x => x.VariantId is null).ToList();
            if (relevantMedia.Where(x => x.Classification == "PRODUCT_MEDIA_URL").Any(x => !ProductPublicationComposer.IsPublicHttpsUrl(x.RelativePath.Trim())))
                return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", $"'{variant.Sku}' için görsel adresleri geçerli herkese açık HTTPS adresi olmalıdır.");
            var imageUrls = ProductPublicationComposer.SelectPublicationImageUrls(relevantMedia
                .Select(item => item.Classification == "PRODUCT_MEDIA_URL" ? item.RelativePath.Trim() : ProductPublicationComposer.BuildPublicProductMediaUrl(configuration["Marketplace:PublicBaseUrl"], item.Id))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!));
            if (imageUrls.Count == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", $"'{variant.Sku}' için en az bir herkese açık HTTPS ürün görseli gerekir.");
            if (imageUrls.Any(url => url.Length > 500)) return Fail("PRODUCT_MEDIA_URL_TOO_LONG", $"'{variant.Sku}' için görsel adresleri en fazla 500 karakter olabilir.");

            var attributes = new List<PazaramaAttributeDraft>();
            var emittedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in VariantAttributeAssignments.ForVariant(assignments, variant.Id))
            {
                if (!mappingsByLocalId.TryGetValue(group.Key, out var mappings) || mappings.Count == 0) return Fail("ATTRIBUTE_MAPPING_REQUIRED", "Üründe kullanılan özellik için doğrulanmış Pazarama eşlemesi bulunamadı.");
                if (mappings.Count != 1) return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", "Üründe kullanılan panel özelliği birden fazla Pazarama özelliğine eşlenmiş; tek bir özellik seçilmelidir.", status: 409);
                var mapping = mappings[0];
                var remote = remoteById[mapping.ExternalId];
                var values = group.ToList();
                if (values.Count > 1) return Fail("ATTRIBUTE_MULTIVALUE_UNSUPPORTED", $"Pazarama '{remote.Name}' özelliği için tek değer kabul eder.");
                if (values[0].ValueId is not Guid valueId) return Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"Pazarama '{remote.Name}' serbest değer kabul etmiyor; listeden bir değer seçilmeli ve doğrulanmış değer eşlemesi olmalı.");
                var localValue = localValues.FirstOrDefault(value => value.AttributeId == values[0].AttributeId && value.Id == valueId);
                if (localValue is null) return Fail("ATTRIBUTE_VALUE_INVALID", $"'{remote.Name}' için seçilen yerel değer bulunamadı.");
                var mapped = await ResolveMappedValueIdAsync(tenantId, connectionId, categoryMapping.ExternalId, mapping.ExternalId, valueId, localValue.Value, cancellationToken);
                if (!mapped.Succeeded) return ServiceResult<ProductPublicationDraft>.Fail(mapped.Error!.Code, mapped.Error.Message, mapped.Error.Status, mapped.Error.FieldErrors);
                if (!emittedIds.Add(mapping.ExternalId)) return Fail("ATTRIBUTE_MAPPING_AMBIGUOUS", $"'{remote.Name}' özelliğine birden fazla değer eşlenmiş.", status: 409);
                attributes.Add(new(mapping.ExternalId, mapped.Value!));
            }

            foreach (var option in optionRows.Where(x => x.VariantId == variant.Id).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            {
                var localAttribute = localAttributes.FirstOrDefault(x => Normalize(option.Label) == Normalize(x.Name));
                if (localAttribute is null || !mappingsByLocalId.TryGetValue(localAttribute.Id, out var optionMappings)) return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' varyant seçeneği için Pazarama özellik eşlemesi gerekir.");
                if (optionMappings.Count != 1) return Fail("OPTION_MAPPING_AMBIGUOUS", $"'{option.Label}' varyant seçeneği birden fazla Pazarama özelliğine eşlenmiş; tek bir özellik seçilmelidir.", status: 409);
                var optionMapping = optionMappings[0];
                if (!remoteById.ContainsKey(optionMapping.ExternalId)) return Fail("OPTION_MAPPING_REQUIRED", $"'{option.Label}' eşlemesi güncel Pazarama kategori snapshot'ında bulunamadı.");
                var localValue = localValues.FirstOrDefault(x => x.AttributeId == localAttribute.Id && Normalize(x.Value) == Normalize(option.ValueLabel));
                if (localValue is null) return Fail("OPTION_VALUE_MAPPING_REQUIRED", $"'{option.Label}: {option.ValueLabel}' seçeneği için güncel Pazarama değer eşlemesi gerekir.");
                var mapped = await ResolveMappedValueIdAsync(tenantId, connectionId, categoryMapping.ExternalId, optionMapping.ExternalId, localValue.Id, localValue.Value, cancellationToken);
                if (!mapped.Succeeded) return ServiceResult<ProductPublicationDraft>.Fail(mapped.Error!.Code, mapped.Error.Message, mapped.Error.Status, mapped.Error.FieldErrors);
                // The variant option is the field's truth: an attribute assignment of the same field (Beden held on each
                // variant as well, live 27 Oct 2026) is replaced by it instead of stopping the publication.
                if (!emittedIds.Add(optionMapping.ExternalId)) attributes.RemoveAll(attribute => string.Equals(attribute.AttributeId, optionMapping.ExternalId, StringComparison.OrdinalIgnoreCase));
                attributes.Add(new(optionMapping.ExternalId, mapped.Value!));
            }

            foreach (var (attributeId, defaultAttribute) in attributeDefaults.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                if (emittedIds.Add(attributeId)) attributes.Add(defaultAttribute);

            var missingRequired = remoteAttributes.Where(x => x.IsRequired == true && !emittedIds.Contains(x.ExternalId)).Select(x => x.Name).ToList();
            if (missingRequired.Count > 0) return Fail("REQUIRED_ATTRIBUTE_MISSING", $"'{variant.Sku}' için Pazarama'nın zorunlu özellikleri eksik: {string.Join(", ", missingRequired)}. Ürünün kategori özelliklerinde bu alanlara değer seçin ve değer eşlemelerini doğrulayın.");

            var quantity = PazaramaPriceInventoryRules.Quantity(inventories[variant.Id].Available - offer.SafetyStock);
            drafts.Add(new(title, title, description, remoteBrandId.ToString(), remoteCategoryId.ToString(), groupCode, barcode, variant.Sku.Trim(), desi, quantity, (int)offer.VatRate, listPrice, salePrice, imageUrls, attributes));
            draftVariants.Add(new(variant.Id, variant.Sku.Trim(), barcode));
        }

        var payloadJson = Payload(drafts);
        if (PazaramaProductPayloadRules.Validate(payloadJson, out var invalid) is null) return Fail("PRODUCT_PAYLOAD_INVALID", invalid ?? "Pazarama ürün açma içeriği geçersiz.");
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return ServiceResult<ProductPublicationDraft>.Ok(new(profile.Id, categoryMapping.ExternalId, brandMapping.ExternalId, payloadHash, payloadJson, draftVariants));
    }

    // The Pazarama body: {"products":[...]}; prices carry two decimals and the currency is TRY.
    internal static string Payload(IReadOnlyList<PazaramaProductDraft> products)
    {
        var items = new JsonArray();
        foreach (var product in products)
        {
            items.Add(new JsonObject
            {
                ["name"] = product.Name,
                ["displayName"] = product.DisplayName,
                ["description"] = product.Description,
                ["brandId"] = product.BrandId,
                ["desi"] = product.Desi,
                ["code"] = product.Code,
                ["groupCode"] = product.GroupCode,
                ["stockCount"] = product.StockCount,
                ["stockCode"] = product.StockCode,
                ["vatRate"] = product.VatRate,
                ["listPrice"] = product.ListPrice,
                ["salePrice"] = product.SalePrice,
                ["currencyType"] = "TRY",
                ["categoryId"] = product.CategoryId,
                ["images"] = new JsonArray(product.Images.Select(url => (JsonNode)new JsonObject { ["imageurl"] = url }).ToArray()),
                ["attributes"] = new JsonArray(product.Attributes.Select(attribute => (JsonNode)new JsonObject { ["attributeId"] = attribute.AttributeId, ["attributeValueId"] = attribute.ValueId }).ToArray()),
                ["deliveries"] = new JsonArray()
            });
        }
        return new JsonObject { ["products"] = items }.ToJsonString();
    }

    // groupCode is documented as at most 10 characters: a short model code is used as it is, a longer
    // one becomes a stable 10-character code of the model code.
    internal static string GroupCode(string modelCode)
    {
        var code = modelCode.Trim();
        if (code.Length <= MaxGroupCodeLength) return code;
        return "RV" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..8];
    }

    // One Pazarama value id for a panel value: the only mapped value, or the one whose name equals the
    // panel value when several are mapped. Null when the choice is ambiguous.
    internal static string? SelectValueId(string localValue, IReadOnlyList<(string ExternalId, string Name)> candidates)
    {
        var usable = candidates.Where(candidate => Guid.TryParse(candidate.ExternalId, out _)).ToList();
        var exact = usable.Where(candidate => Normalize(candidate.Name) == Normalize(localValue)).ToList();
        return exact.Count == 1 ? exact[0].ExternalId : exact.Count == 0 && usable.Count == 1 ? usable[0].ExternalId : null;
    }

    private async Task<ServiceResult<string>> ResolveMappedValueIdAsync(Guid tenantId, Guid connectionId, string categoryId, string attributeId, Guid localValueId, string localValue, CancellationToken cancellationToken)
    {
        var scope = $"{categoryId}/{attributeId}";
        var snapshotId = await db.ReferenceSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId && snapshot.ResourceType == "ATTRIBUTE_VALUES" && snapshot.ScopeExternalId == scope && snapshot.IsCurrent)
            .OrderByDescending(snapshot => snapshot.FetchedAt)
            .Select(snapshot => (Guid?)snapshot.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshotId is null)
            return ServiceResult<string>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{localValue}' değeri güncel Pazarama değer listesinde eşlenmemiş.", 422);
        var externalIds = await db.AttributeValueMappings.AsNoTracking()
            .Where(mapping => mapping.TenantId == tenantId && mapping.ConnectionId == connectionId && mapping.LocalId == localValueId && mapping.ScopeExternalId == scope && mapping.SnapshotId == snapshotId && mapping.Status == "VERIFIED")
            .Select(mapping => mapping.ExternalId)
            .ToListAsync(cancellationToken);
        if (externalIds.Count == 0)
            return ServiceResult<string>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{localValue}' değeri güncel Pazarama değer listesinde eşlenmemiş.", 422);
        var remoteValues = await db.ReferenceItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.SnapshotId == snapshotId && item.ResourceType == "ATTRIBUTE_VALUES" && item.IsActive && externalIds.Contains(item.ExternalId))
            .Select(item => new { item.ExternalId, item.Name })
            .ToListAsync(cancellationToken);
        if (remoteValues.Count == 0)
            return ServiceResult<string>.Fail("ATTRIBUTE_VALUE_MAPPING_REQUIRED", $"'{localValue}' için eşlenen Pazarama değeri güncel listede bulunamadı.", 422);
        return SelectValueId(localValue, remoteValues.Select(item => (item.ExternalId, item.Name)).ToList()) is { } selected
            ? ServiceResult<string>.Ok(selected)
            : ServiceResult<string>.Fail("ATTRIBUTE_VALUE_MAPPING_AMBIGUOUS", $"'{localValue}' panel değeri birden fazla Pazarama değerine eşlenmiş. Ürün gönderimi için eşlemeyi tek bir karşılığa indirin.", 422);
    }

    // Connection defaults this category can use, keyed by attribute id; a default counts only while its
    // value is active in the category's current value snapshot (Pazarama takes no free text).
    private async Task<Dictionary<string, PazaramaAttributeDraft>> ResolveAttributeDefaultsAsync(Guid tenantId, Guid connectionId, string categoryExternalId, IReadOnlyList<ReferenceItem> remoteAttributes, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, PazaramaAttributeDraft>(StringComparer.OrdinalIgnoreCase);
        var defaults = await db.ConnectionAttributeDefaults.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).ToListAsync(cancellationToken);
        foreach (var attributeDefault in defaults)
        {
            if (remoteAttributes.All(x => !string.Equals(x.ExternalId, attributeDefault.ExternalAttributeId, StringComparison.OrdinalIgnoreCase)) || string.IsNullOrWhiteSpace(attributeDefault.ExternalValueId)) continue;
            if (!Guid.TryParse(attributeDefault.ExternalAttributeId, out _) || !Guid.TryParse(attributeDefault.ExternalValueId, out _)) continue;
            var scope = $"{categoryExternalId}/{attributeDefault.ExternalAttributeId}";
            var offered = await (from snapshot in db.ReferenceSnapshots.AsNoTracking()
                                 join item in db.ReferenceItems.AsNoTracking() on snapshot.Id equals item.SnapshotId
                                 where snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId && snapshot.ResourceType == "ATTRIBUTE_VALUES"
                                     && snapshot.ScopeExternalId == scope && snapshot.IsCurrent
                                     && item.TenantId == tenantId && item.ResourceType == "ATTRIBUTE_VALUES" && item.ExternalId == attributeDefault.ExternalValueId && item.IsActive
                                 select item.Id).AnyAsync(cancellationToken);
            if (offered) resolved[attributeDefault.ExternalAttributeId] = new(attributeDefault.ExternalAttributeId, attributeDefault.ExternalValueId);
        }
        return resolved;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static ServiceResult<ProductPublicationDraft> NotFound() => ServiceResult<ProductPublicationDraft>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<ProductPublicationDraft> Fail(string code, string message, string? field = null, int status = 422) => ServiceResult<ProductPublicationDraft>.Fail(code, message, status, field is null ? null : new Dictionary<string, string[]> { [field] = [message] });
}
