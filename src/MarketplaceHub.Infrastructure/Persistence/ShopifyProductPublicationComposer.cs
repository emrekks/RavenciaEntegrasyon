using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// A model-code family (the colour cards of one brand and model code) is one Shopify product: every card's variants
// go under one handle with the cards' options merged (Renk + Beden). productSet replaces the variant list, so every
// publication of a card carries the whole family; a card's own job then records only its own variants.
internal sealed class ShopifyProductPublicationComposer(AppDbContext db, IConfiguration configuration)
{
    internal const string MetafieldNamespace = "marketplacehub";
    internal const string ProductIdMetafieldKey = "ravencia_product_id";
    internal const string FamilyMetafieldKey = "ravencia_family_key";
    internal const string PayloadHashMetafieldKey = "payload_hash";
    private const int MaximumVariantCount = 250;
    private const int MaximumFileCount = 250;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ServiceResult<ProductPublicationDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null) return Fail("PRODUCT_NOT_FOUND", "Ürün kaydı bulunamadı.", 404);
        if (product.Status == ProductStatus.Archived) return Fail("PRODUCT_ARCHIVED", "Arşivlenmiş ürün yayınlanamaz.");

        var profile = await db.ChannelListingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null) return Fail("LISTING_PROFILE_REQUIRED", "Shopify yayınından önce bu bağlantı için listing profile oluşturulmalıdır.");
        if (!profile.Enabled) return Fail("LISTING_PROFILE_DISABLED", "Shopify listing profile etkinleştirilmeden yayın işi oluşturulamaz.");

        var modelCode = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == productId && x.ModelCode != null)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.ModelCode)
            .FirstOrDefaultAsync(cancellationToken);
        var familyKey = ProductFamilyKeyPolicy.FamilyKey(productId, product.BrandId, modelCode);
        var familyIds = await ProductFamilyScope.ProductIdsAsync(db, tenantId, product.BrandId, modelCode, db.ProductVariants.AsNoTracking(), cancellationToken);
        if (!familyIds.Contains(productId)) familyIds.Add(productId);
        // Card order is creation order (the id breaks a tie), so the colours keep one order on every publication.
        var members = await db.Products.AsNoTracking()
            .Where(x => x.TenantId == tenantId && familyIds.Contains(x.Id) && x.Status != ProductStatus.Archived)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var lead = members[0];
        var memberIds = members.Select(x => x.Id).ToArray();
        var leadProfile = await db.ChannelListingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == lead.Id, cancellationToken);

        var title = (leadProfile?.TitleOverride ?? lead.Title).Trim();
        var description = (leadProfile?.DescriptionOverride ?? lead.Description).Trim();
        if (title.Length is < 1 or > 255) return Fail("PRODUCT_TITLE_INVALID", "Shopify ürün adı 1-255 karakter olmalıdır.", field: "title");
        if (description.Length is < 1 or > 30000) return Fail("PRODUCT_DESCRIPTION_INVALID", "Shopify ürün açıklaması 1-30000 karakter olmalıdır.", field: "description");

        var memberIndex = members.Select((member, index) => (member.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var variants = (await db.ProductVariants.AsNoTracking()
                .Where(x => x.TenantId == tenantId && memberIds.Contains(x.ProductId) && x.Status != ProductStatus.Archived)
                .ToListAsync(cancellationToken))
            .OrderBy(x => memberIndex[x.ProductId]).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToList();
        foreach (var member in members)
            if (!variants.Any(variant => variant.ProductId == member.Id))
                return Fail("PRODUCT_VARIANT_REQUIRED", $"Shopify yayını için '{member.Title}' kartında en az bir etkin varyant gerekir.");
        if (variants.Count > MaximumVariantCount) return Fail("PRODUCT_VARIANT_LIMIT_EXCEEDED", $"Shopify bağlantısı için tek yayın isteğinde en fazla {MaximumVariantCount} varyant gönderilebilir; model ailesinin kartlarında {variants.Count} varyant var.");
        if (variants.Any(variant => string.IsNullOrWhiteSpace(variant.Sku) || variant.Sku.Trim().Length > 100)) return Fail("SKU_INVALID", "Tüm Shopify varyantlarda en fazla 100 karakterlik SKU bulunmalıdır.", field: "variants");
        if (variants.Select(variant => variant.Sku.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variants.Count) return Fail("SKU_DUPLICATE", "Shopify ürünündeki varyant SKU değerleri model ailesinin tüm kartlarında benzersiz olmalıdır.", field: "variants");
        if (variants.Any(variant => !string.IsNullOrWhiteSpace(variant.Barcode)
                && (variant.Barcode.Trim().Length > 40 || !variant.Barcode.Trim().All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))))
            return Fail("BARCODE_INVALID", "Shopify varyant barkodu en fazla 40 karakter olabilir ve yalnızca harf, rakam, nokta, tire veya alt çizgi içerebilir.", field: "variants");

        var variantIds = variants.Select(variant => variant.Id).ToArray();
        var optionRows = await (from variantOption in db.VariantOptionValues.AsNoTracking()
                                join option in db.ProductOptions.AsNoTracking() on new { variantOption.TenantId, variantOption.OptionId } equals new { option.TenantId, OptionId = option.Id }
                                join optionValue in db.ProductOptionValues.AsNoTracking() on new { variantOption.TenantId, variantOption.OptionValueId } equals new { optionValue.TenantId, OptionValueId = optionValue.Id }
                                where variantOption.TenantId == tenantId && variantIds.Contains(variantOption.VariantId) && memberIds.Contains(option.ProductId)
                                select new { variantOption.VariantId, option.ProductId, option.Label, OptionSort = option.SortOrder, ValueLabel = optionValue.Label, ValueSort = optionValue.SortOrder })
            .ToListAsync(cancellationToken);
        var optionPlan = PlanOptions(variants.Select(variant => new ShopifyVariantOptionRow(
            variant.Id,
            variant.Sku.Trim(),
            memberIndex[variant.ProductId],
            optionRows.Where(row => row.VariantId == variant.Id && row.ProductId == variant.ProductId)
                .Select(row => new ShopifyOptionValue(row.Label, row.OptionSort, row.ValueLabel, row.ValueSort))
                .ToList())).ToList());
        if (optionPlan.Error is { } optionError) return Fail(optionError.Code, optionError.Message, field: "variants");
        var plan = optionPlan.Plan!;

        var offers = await db.ChannelOffers.AsNoTracking()
            .Where(offer => offer.TenantId == tenantId && offer.ConnectionId == connectionId && variantIds.Contains(offer.VariantId) && offer.Status == "ACTIVE")
            .ToDictionaryAsync(offer => offer.VariantId, cancellationToken);
        var inventories = await db.InventoryItems.AsNoTracking()
            .Where(inventory => inventory.TenantId == tenantId && variantIds.Contains(inventory.VariantId) && inventory.LocationCode == "MAIN")
            .ToDictionaryAsync(inventory => inventory.VariantId, cancellationToken);
        if (variants.FirstOrDefault(variant => !offers.ContainsKey(variant.Id)) is { } unpriced)
            return Fail("CHANNEL_OFFER_REQUIRED", $"Shopify için her varyantta etkin fiyat teklifi tanımlanmalıdır; '{unpriced.Sku}' ({MemberTitle(members, unpriced.ProductId)}) için yok.");
        if (variants.FirstOrDefault(variant => !inventories.ContainsKey(variant.Id)) is { } unstocked)
            return Fail("INVENTORY_REQUIRED", $"Shopify için her varyantta MAIN stok kaydı gerekir; '{unstocked.Sku}' ({MemberTitle(members, unstocked.ProductId)}) için yok.");

        var brand = lead.BrandId is Guid brandId
            ? await db.Brands.AsNoTracking().Where(item => item.TenantId == tenantId && item.Id == brandId).Select(item => item.Name).SingleOrDefaultAsync(cancellationToken)
            : null;
        var category = lead.CategoryId is Guid categoryId
            ? await db.Categories.AsNoTracking().Where(item => item.TenantId == tenantId && item.Id == categoryId).Select(item => item.Name).SingleOrDefaultAsync(cancellationToken)
            : null;
        // The category mapped for this store is one of its own product types (a flat entry of the current
        // CATEGORIES snapshot); it is sent exactly as the store holds it. Without a mapping the panel
        // category name is sent, as before.
        var mappedProductType = lead.CategoryId is Guid mappedCategoryId
            ? await (from mapping in db.CategoryMappings.AsNoTracking()
                     join snapshot in db.ReferenceSnapshots.AsNoTracking() on mapping.SnapshotId equals snapshot.Id
                     join item in db.ReferenceItems.AsNoTracking() on new { mapping.SnapshotId, mapping.ExternalId } equals new { item.SnapshotId, item.ExternalId }
                     where mapping.TenantId == tenantId && mapping.ConnectionId == connectionId && mapping.LocalId == mappedCategoryId && mapping.Status == "VERIFIED"
                         && snapshot.TenantId == tenantId && snapshot.ConnectionId == connectionId && snapshot.ResourceType == "CATEGORIES" && snapshot.IsCurrent
                         && item.TenantId == tenantId && item.ResourceType == "CATEGORIES" && item.IsActive && item.ParentExternalId == null
                     select item.ExternalId)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var productType = ShopifyProductType(mappedProductType, category);

        var mediaRows = await (from media in db.ProductMedia.AsNoTracking()
                               join asset in db.FileAssets.AsNoTracking() on media.FileAssetId equals asset.Id
                               where media.TenantId == tenantId && memberIds.Contains(media.ProductId) && media.Status == "ACTIVE"
                                   && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null
                                   && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                               orderby media.SortOrder, media.Id
                               select new { media.ProductId, asset.Id, asset.Classification, asset.RelativePath })
            .ToListAsync(cancellationToken);
        if (mediaRows.Where(media => media.Classification == "PRODUCT_MEDIA_URL").Any(media => !ProductPublicationComposer.IsPublicHttpsUrl(media.RelativePath.Trim())))
            return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", "Shopify görsellerinin tamamı güvenli ve herkese açık HTTPS adresi olmalıdır.", field: "images");
        // Each card keeps its own first images; the first one is the picture of the card's variants.
        var imagesByMember = members.ToDictionary(member => member.Id, member => ProductPublicationComposer.SelectPublicationImageUrls(mediaRows
            .Where(media => media.ProductId == member.Id)
            .Select(media => media.Classification == "PRODUCT_MEDIA_URL"
                ? media.RelativePath.Trim()
                : ProductPublicationComposer.BuildPublicProductMediaUrl(configuration["Marketplace:PublicBaseUrl"], media.Id))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)));
        var imageUrls = members.SelectMany(member => imagesByMember[member.Id]).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumFileCount).ToList();
        if (imageUrls.Count == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", "Shopify yayını için en az bir HTTPS ürün görseli gerekir. Yerel görseller için Marketplace:PublicBaseUrl ayarını yapılandırın.", field: "images");
        var files = imageUrls.Select(url => FileInput(url, title)).ToArray();

        var productVariants = new List<Dictionary<string, object?>>(variants.Count);
        var draftVariants = new List<PublicationVariantDraft>();
        foreach (var variant in variants)
        {
            var offer = offers[variant.Id];
            if (!IsCurrencyCode(offer.Currency) || offer.SalePrice <= 0 || offer.ListPrice < offer.SalePrice)
                return Fail("CHANNEL_OFFER_INVALID", $"'{variant.Sku}' için geçerli para birimi ve listPrice >= salePrice > 0 kuralı gerekir.");
            if (offer.SalePrice > 999999999999m || offer.ListPrice > 999999999999m)
                return Fail("CHANNEL_OFFER_INVALID", $"'{variant.Sku}' fiyatı Shopify sınırını aşıyor.");
            var publishable = InventoryProjection.ChannelPublishable(inventories[variant.Id].Available, offer.SafetyStock);
            if (publishable < 0 || publishable > int.MaxValue)
                return Fail("INVENTORY_QUANTITY_INVALID", $"'{variant.Sku}' için yayınlanabilir stok Shopify'ın tam sayı sınırında değil.");

            var sku = variant.Sku.Trim();
            var barcode = variant.Barcode?.Trim();
            var salePrice = PriceInventoryComposer.OutboundSalePrice(offer);
            var listPrice = PriceInventoryComposer.OutboundListPrice(offer);
            var memberImage = imagesByMember[variant.ProductId].FirstOrDefault(url => imageUrls.Contains(url, StringComparer.OrdinalIgnoreCase));
            productVariants.Add(new()
            {
                ["sku"] = sku,
                // ProductVariantSetInput.barcode is deprecated (2026-10); barcodes replaces the variant's whole barcode list.
                ["barcodes"] = string.IsNullOrWhiteSpace(barcode) ? null : new[] { new { value = barcode } },
                ["price"] = salePrice.ToString("0.00", CultureInfo.InvariantCulture),
                ["compareAtPrice"] = listPrice > salePrice ? listPrice.ToString("0.00", CultureInfo.InvariantCulture) : null,
                ["inventoryItem"] = new { tracked = true, requiresShipping = true },
                ["inventoryQuantity"] = decimal.ToInt32(decimal.Truncate(publishable)),
                ["optionValues"] = plan.VariantValues[variant.Id].Select(value => new { optionName = value.OptionName, name = value.Value }).ToArray(),
                // A family variant shows its card's first image; the product files carry the same entry, as Shopify requires.
                ["file"] = members.Count > 1 && memberImage is not null ? FileInput(memberImage, title) : null
            });
            // This card's job records only this card's variants; the family's other cards have jobs of their own.
            if (variant.ProductId == productId) draftVariants.Add(new(variant.Id, sku, barcode ?? string.Empty));
        }

        var descriptionHtml = DescriptionHtml(description);
        var metafields = new List<ShopifyMetafieldInput> { Metafield(ProductIdMetafieldKey, lead.Id.ToString("D")) };
        if (IsModelFamily(familyKey)) metafields.Add(Metafield(FamilyMetafieldKey, familyKey));
        var input = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["descriptionHtml"] = descriptionHtml,
            ["handle"] = ShopifyHandle(productId, familyKey),
            ["productType"] = productType,
            ["vendor"] = string.IsNullOrWhiteSpace(brand) ? "Ravencia" : brand.Trim(),
            ["status"] = "ACTIVE",
            ["metafields"] = metafields,
            ["productOptions"] = plan.Options.Select((option, index) => new Dictionary<string, object?>
            {
                ["name"] = option.Name,
                ["position"] = index + 1,
                ["values"] = option.Values.Select(value => new { name = value }).ToArray()
            }).ToList(),
            ["variants"] = productVariants,
            ["files"] = files
        };
        // The hash of everything else rides along as a metafield: another card of the family publishing the same
        // content is recognised in Shopify and not written again.
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input, JsonOptions))));
        metafields.Add(Metafield(PayloadHashMetafieldKey, payloadHash));
        var payloadJson = JsonSerializer.Serialize(input, JsonOptions);
        return ServiceResult<ProductPublicationDraft>.Ok(new(profile.Id, string.Empty, string.Empty, payloadHash, payloadJson, draftVariants));
    }

    internal static string? ShopifyProductType(string? mappedProductType, string? panelCategoryName) =>
        !string.IsNullOrWhiteSpace(mappedProductType) ? mappedProductType
        : string.IsNullOrWhiteSpace(panelCategoryName) ? null : panelCategoryName.Trim();

    // The same handle and the same variant SKUs: prices and stock may have moved since, the variant list has not.
    internal static bool SameVariantSkus(string queuedPayloadJson, string currentPayloadJson)
    {
        static (string? Handle, string[] Skus) Read(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var handle = root.TryGetProperty("handle", out var value) ? value.GetString() : null;
            var skus = root.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array
                ? variants.EnumerateArray().Select(variant => variant.TryGetProperty("sku", out var sku) ? sku.GetString()?.Trim().ToUpperInvariant() ?? "" : "").Order(StringComparer.Ordinal).ToArray()
                : [];
            return (handle, skus);
        }
        try
        {
            var queued = Read(queuedPayloadJson);
            var current = Read(currentPayloadJson);
            return string.Equals(queued.Handle, current.Handle, StringComparison.Ordinal) && queued.Skus.SequenceEqual(current.Skus, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsModelFamily(string familyKey) => familyKey.StartsWith("model:", StringComparison.Ordinal);

    // The panel keeps descriptions as rich-text HTML: that goes to Shopify as HTML, through the import allowlist. A
    // description without tags is plain text, written as one paragraph with its line breaks.
    internal static string DescriptionHtml(string description) => Regex.IsMatch(description, "<[a-zA-Z/!]")
        ? ImportedHtmlSanitizer.Sanitize(description)
        : "<p>" + WebUtility.HtmlEncode(description).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br />", StringComparison.Ordinal) + "</p>";

    // A product without a model code keeps its own handle; a model family has one handle for all its cards.
    internal static string ShopifyHandle(Guid productId, string familyKey) => IsModelFamily(familyKey)
        ? $"ravencia-model-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(familyKey)))[..20].ToLowerInvariant()}"
        : $"ravencia-{productId:N}";

    internal static ShopifyOptionPlanResult PlanOptions(IReadOnlyList<ShopifyVariantOptionRow> rows)
    {
        var names = rows.SelectMany(row => row.Values)
            .Where(value => !string.IsNullOrWhiteSpace(value.Option))
            .GroupBy(value => value.Option.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.First().Option.Trim(), Sort: group.Min(value => value.OptionSort)))
            .OrderBy(item => item.Sort).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Name)
            .ToList();
        if (names.Count > 3)
            return new(null, new("SHOPIFY_OPTION_LIMIT_EXCEEDED", $"Shopify bir üründe en fazla üç varyant seçeneği destekler; kartlarda {names.Count} seçenek var ({string.Join(", ", names)})."));
        if (names.Count == 0)
        {
            if (rows.Count > 1)
                return new(null, new("SHOPIFY_VARIANT_OPTIONS_REQUIRED", "Birden fazla varyant Shopify'da ancak renk, beden gibi bir seçenekle ayrılabilir; varyantlara seçenek değeri verin."));
            return new(new([("Title", ["Default Title"])], rows.ToDictionary(row => row.VariantId, _ => (IReadOnlyList<(string, string)>)[("Title", "Default Title")])), null);
        }

        var variantValues = new Dictionary<Guid, IReadOnlyList<(string OptionName, string Value)>>();
        var combinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var values = new List<(string OptionName, string Value)>(names.Count);
            foreach (var name in names)
            {
                var matches = row.Values.Where(value => string.Equals(value.Option.Trim(), name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value.Value)).ToList();
                if (matches.Count != 1)
                    return new(null, new("SHOPIFY_VARIANT_OPTIONS_REQUIRED", $"'{row.Sku}' için '{name}' seçeneğinde tek bir değer bulunmalıdır; model ailesinin tüm kartları aynı seçenekleri taşımalıdır."));
                values.Add((name, matches[0].Value.Trim()));
            }
            var combination = string.Join(" / ", values.Select(value => value.Value));
            if (!combinations.TryAdd(combination, row.Sku))
                return new(null, new("SHOPIFY_VARIANT_OPTIONS_DUPLICATE", $"'{combinations[combination]}' ve '{row.Sku}' aynı seçenek değerlerini taşıyor ({combination}); Shopify bunları ayrı varyant olarak açamaz."));
            variantValues[row.VariantId] = values;
        }

        var options = names.Select(name => (Name: name, Values: (IReadOnlyList<string>)rows
            .SelectMany(row => row.Values
                .Where(value => string.Equals(value.Option.Trim(), name, StringComparison.OrdinalIgnoreCase))
                .Select(value => (row.MemberIndex, value.ValueSort, Value: value.Value.Trim())))
            .OrderBy(item => item.MemberIndex).ThenBy(item => item.ValueSort).ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList())).ToList();
        return new(new(options, variantValues), null);
    }

    private static ShopifyFileInput FileInput(string url, string alt) => new(url, "IMAGE", alt);

    private static ShopifyMetafieldInput Metafield(string key, string value) => new(MetafieldNamespace, key, "single_line_text_field", value);

    private static string MemberTitle(IReadOnlyList<Product> members, Guid productId) => members.FirstOrDefault(member => member.Id == productId)?.Title ?? productId.ToString("D");

    private static bool IsCurrencyCode(string? value) => value is { Length: 3 } && value.All(character => character is >= 'A' and <= 'Z');

    private static ServiceResult<ProductPublicationDraft> Fail(string code, string message, int status = 422, string? field = null)
    {
        IReadOnlyDictionary<string, string[]>? fieldErrors = field is null ? null : new Dictionary<string, string[]> { [field] = [message] };
        return ServiceResult<ProductPublicationDraft>.Fail(code, message, status, fieldErrors);
    }
}

internal sealed record ShopifyFileInput(string OriginalSource, string ContentType, string Alt);
internal sealed record ShopifyMetafieldInput(string Namespace, string Key, string Type, string Value);
internal sealed record ShopifyOptionValue(string Option, int OptionSort, string Value, int ValueSort);
internal sealed record ShopifyVariantOptionRow(Guid VariantId, string Sku, int MemberIndex, IReadOnlyList<ShopifyOptionValue> Values);
internal sealed record ShopifyOptionPlan(IReadOnlyList<(string Name, IReadOnlyList<string> Values)> Options, IReadOnlyDictionary<Guid, IReadOnlyList<(string OptionName, string Value)>> VariantValues);
internal sealed record ShopifyOptionPlanError(string Code, string Message);
internal sealed record ShopifyOptionPlanResult(ShopifyOptionPlan? Plan, ShopifyOptionPlanError? Error);
