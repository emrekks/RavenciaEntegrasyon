using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed class ShopifyProductPublicationComposer(AppDbContext db, IConfiguration configuration)
{
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

        var title = (profile.TitleOverride ?? product.Title).Trim();
        var description = (profile.DescriptionOverride ?? product.Description).Trim();
        if (title.Length is < 1 or > 255) return Fail("PRODUCT_TITLE_INVALID", "Shopify ürün adı 1-255 karakter olmalıdır.", field: "title");
        if (description.Length is < 1 or > 30000) return Fail("PRODUCT_DESCRIPTION_INVALID", "Shopify ürün açıklaması 1-30000 karakter olmalıdır.", field: "description");

        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == productId && x.Status != ProductStatus.Archived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (variants.Count == 0) return Fail("PRODUCT_VARIANT_REQUIRED", "Shopify yayını için en az bir etkin varyant gerekir.");
        if (variants.Count > 250) return Fail("PRODUCT_VARIANT_LIMIT_EXCEEDED", "Shopify bağlantısı için tek yayın isteğinde en fazla 250 varyant gönderilebilir.");
        if (variants.Any(variant => string.IsNullOrWhiteSpace(variant.Sku) || variant.Sku.Trim().Length > 100)) return Fail("SKU_INVALID", "Tüm Shopify varyantlarda en fazla 100 karakterlik SKU bulunmalıdır.", field: "variants");
        if (variants.Select(variant => variant.Sku.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variants.Count) return Fail("SKU_DUPLICATE", "Shopify ürünündeki varyant SKU değerleri benzersiz olmalıdır.", field: "variants");
        if (variants.Any(variant => !string.IsNullOrWhiteSpace(variant.Barcode)
                && (variant.Barcode.Trim().Length > 40 || !variant.Barcode.Trim().All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'))))
            return Fail("BARCODE_INVALID", "Shopify varyant barkodu en fazla 40 karakter olabilir ve yalnızca harf, rakam, nokta, tire veya alt çizgi içerebilir.", field: "variants");

        var variantIds = variants.Select(variant => variant.Id).ToArray();
        var options = await db.ProductOptions.AsNoTracking()
            .Where(option => option.TenantId == tenantId && option.ProductId == productId)
            .OrderBy(option => option.SortOrder).ThenBy(option => option.Id)
            .Select(option => new { option.Id, option.Label, option.SortOrder })
            .ToListAsync(cancellationToken);
        if (options.Count > 3) return Fail("SHOPIFY_OPTION_LIMIT_EXCEEDED", "Shopify bir üründe en fazla üç varyant seçeneği destekler.", field: "options");

        var optionRows = await (from variantOption in db.VariantOptionValues.AsNoTracking()
                                join option in db.ProductOptions.AsNoTracking() on new { variantOption.TenantId, variantOption.OptionId } equals new { option.TenantId, OptionId = option.Id }
                                join optionValue in db.ProductOptionValues.AsNoTracking() on new { variantOption.TenantId, variantOption.OptionValueId } equals new { optionValue.TenantId, OptionValueId = optionValue.Id }
                                where variantOption.TenantId == tenantId && variantIds.Contains(variantOption.VariantId) && option.ProductId == productId
                                select new { variantOption.VariantId, option.Id, option.Label, OptionSort = option.SortOrder, ValueLabel = optionValue.Label, ValueSort = optionValue.SortOrder, optionValue.OptionId })
            .ToListAsync(cancellationToken);
        if (options.Count > 0)
        {
            foreach (var variant in variants)
            {
                var values = optionRows.Where(row => row.VariantId == variant.Id).ToList();
                if (values.Count != options.Count || values.Select(row => row.Id).Distinct().Count() != options.Count || values.Any(row => string.IsNullOrWhiteSpace(row.ValueLabel)))
                    return Fail("SHOPIFY_VARIANT_OPTIONS_REQUIRED", $"'{variant.Sku}' için her ürün seçeneğinde tek bir değer bulunmalıdır.", field: "variants");
            }
        }

        var optionSpecs = new List<Dictionary<string, object?>>();
        if (options.Count == 0)
        {
            optionSpecs.Add(new() { ["name"] = "Title", ["position"] = 1, ["values"] = new[] { new { name = "Default Title" } } });
        }
        else
        {
            foreach (var option in options)
            {
                var valueLabels = optionRows.Where(row => row.Id == option.Id)
                    .OrderBy(row => row.ValueSort).ThenBy(row => row.ValueLabel)
                    .Select(row => row.ValueLabel.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(value => new { name = value })
                    .ToArray();
                if (string.IsNullOrWhiteSpace(option.Label) || valueLabels.Length == 0)
                    return Fail("SHOPIFY_OPTION_VALUE_REQUIRED", "Her Shopify ürün seçeneğinde en az bir değer bulunmalıdır.", field: "options");
                optionSpecs.Add(new() { ["name"] = option.Label.Trim(), ["position"] = optionSpecs.Count + 1, ["values"] = valueLabels });
            }
        }

        var offers = await db.ChannelOffers.AsNoTracking()
            .Where(offer => offer.TenantId == tenantId && offer.ConnectionId == connectionId && variantIds.Contains(offer.VariantId) && offer.Status == "ACTIVE")
            .ToDictionaryAsync(offer => offer.VariantId, cancellationToken);
        var inventories = await db.InventoryItems.AsNoTracking()
            .Where(inventory => inventory.TenantId == tenantId && variantIds.Contains(inventory.VariantId) && inventory.LocationCode == "MAIN")
            .ToDictionaryAsync(inventory => inventory.VariantId, cancellationToken);
        if (offers.Count != variants.Count) return Fail("CHANNEL_OFFER_REQUIRED", "Shopify için her varyantta etkin fiyat teklifi tanımlanmalıdır.");
        if (inventories.Count != variants.Count) return Fail("INVENTORY_REQUIRED", "Shopify için her varyantta MAIN stok kaydı gerekir.");

        var brand = product.BrandId is Guid brandId
            ? await db.Brands.AsNoTracking().Where(item => item.TenantId == tenantId && item.Id == brandId).Select(item => item.Name).SingleOrDefaultAsync(cancellationToken)
            : null;
        var category = product.CategoryId is Guid categoryId
            ? await db.Categories.AsNoTracking().Where(item => item.TenantId == tenantId && item.Id == categoryId).Select(item => item.Name).SingleOrDefaultAsync(cancellationToken)
            : null;

        var optionValuesByVariant = optionRows
            .GroupBy(row => row.VariantId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.OptionSort).ThenBy(row => row.Id).ToList());
        var productVariants = new List<Dictionary<string, object?>>(variants.Count);
        var draftVariants = new List<PublicationVariantDraft>(variants.Count);
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

            var variantOptionValues = options.Count == 0
                ? new[] { new { optionName = "Title", name = "Default Title" } }
                : optionValuesByVariant[variant.Id].Select(value => new { optionName = value.Label.Trim(), name = value.ValueLabel.Trim() }).ToArray();
            var sku = variant.Sku.Trim();
            var barcode = variant.Barcode?.Trim();
            productVariants.Add(new()
            {
                ["sku"] = sku,
                ["barcode"] = string.IsNullOrWhiteSpace(barcode) ? null : barcode,
                ["price"] = offer.SalePrice.ToString("0.00##", CultureInfo.InvariantCulture),
                ["compareAtPrice"] = offer.ListPrice > offer.SalePrice ? offer.ListPrice.ToString("0.00##", CultureInfo.InvariantCulture) : null,
                ["inventoryItem"] = new { tracked = true, requiresShipping = true },
                ["inventoryQuantity"] = decimal.ToInt32(decimal.Truncate(publishable)),
                ["optionValues"] = variantOptionValues
            });
            draftVariants.Add(new(variant.Id, sku, barcode ?? string.Empty));
        }

        var mediaRows = await (from media in db.ProductMedia.AsNoTracking()
                               join asset in db.FileAssets.AsNoTracking() on media.FileAssetId equals asset.Id
                               where media.TenantId == tenantId && media.ProductId == productId && media.Status == "ACTIVE"
                                   && asset.TenantId == tenantId && asset.Status == "ACTIVE" && asset.ArchivedAt == null
                                   && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                               orderby media.SortOrder, media.Id
                               select new { media.AltText, asset.Id, asset.Classification, asset.RelativePath })
            .ToListAsync(cancellationToken);
        var sourceUrls = mediaRows.Select(media => media.Classification == "PRODUCT_MEDIA_URL"
            ? media.RelativePath.Trim()
            : ProductPublicationComposer.BuildPublicProductMediaUrl(configuration["Marketplace:PublicBaseUrl"], media.Id))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .ToList();
        if (mediaRows.Where(media => media.Classification == "PRODUCT_MEDIA_URL").Any(media => !ProductPublicationComposer.IsPublicHttpsUrl(media.RelativePath.Trim())))
            return Fail("PRODUCT_MEDIA_PUBLIC_URL_INVALID", "Shopify görsellerinin tamamı güvenli ve herkese açık HTTPS adresi olmalıdır.", field: "images");
        var imageUrls = ProductPublicationComposer.SelectPublicationImageUrls(sourceUrls);
        if (imageUrls.Count == 0) return Fail("PRODUCT_MEDIA_PUBLIC_URL_REQUIRED", "Shopify yayını için en az bir HTTPS ürün görseli gerekir. Yerel görseller için Marketplace:PublicBaseUrl ayarını yapılandırın.", field: "images");
        var files = imageUrls.Select(url => (object)new { originalSource = url, contentType = "IMAGE", alt = title }).ToArray();

        var descriptionHtml = "<p>" + WebUtility.HtmlEncode(description).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br />", StringComparison.Ordinal) + "</p>";
        var input = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["descriptionHtml"] = descriptionHtml,
            ["handle"] = $"ravencia-{productId:N}",
            ["productType"] = string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            ["vendor"] = string.IsNullOrWhiteSpace(brand) ? "Ravencia" : brand.Trim(),
            ["status"] = "ACTIVE",
            ["metafields"] = new[] { new { @namespace = "marketplacehub", key = "ravencia_product_id", type = "single_line_text_field", value = productId.ToString("D") } },
            ["productOptions"] = optionSpecs,
            ["variants"] = productVariants,
            ["files"] = files
        };
        var payloadJson = JsonSerializer.Serialize(input, JsonOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return ServiceResult<ProductPublicationDraft>.Ok(new(profile.Id, string.Empty, string.Empty, payloadHash, payloadJson, draftVariants));
    }

    private static bool IsCurrencyCode(string? value) => value is { Length: 3 } && value.All(character => character is >= 'A' and <= 'Z');

    private static ServiceResult<ProductPublicationDraft> Fail(string code, string message, int status = 422, string? field = null)
    {
        IReadOnlyDictionary<string, string[]>? fieldErrors = field is null ? null : new Dictionary<string, string[]> { [field] = [message] };
        return ServiceResult<ProductPublicationDraft>.Fail(code, message, status, fieldErrors);
    }
}
