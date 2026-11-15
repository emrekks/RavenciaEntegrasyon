using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient
{
    public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (account.CatalogBaseAddress is null) return await Unsupported<AdapterPageResult<RemoteReferenceItem>>("Hepsiburada production katalog base URL'si resmi hesap yapılandırmasında doğrulanana kadar referans okuma kapalıdır.");

        var type = resource.ResourceType.Trim().ToUpperInvariant();
        string endpoint;
        var (pageNumber, limit) = ReferencePage(page, type == "CATEGORIES" ? 2000 : 1000);
        switch (type)
        {
            case "CATEGORIES":
                if (resource.ParentExternalId is not null) return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "HEPSIBURADA_REFERENCE_SCOPE_INVALID", "Kategori listesi parent kapsamı kabul etmez.", HttpStatusCode.BadRequest);
                endpoint = Categories(pageNumber, limit);
                break;
            case "CATEGORY_ATTRIBUTES":
                if (string.IsNullOrWhiteSpace(resource.ParentExternalId) || resource.ParentExternalId.Contains("/", StringComparison.Ordinal))
                    return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "HEPSIBURADA_REFERENCE_SCOPE_INVALID", "Kategori özellikleri tek bir categoryId ister.", HttpStatusCode.BadRequest);
                endpoint = CategoryAttributes(resource.ParentExternalId);
                break;
            case "ATTRIBUTE_VALUES":
                if (!TryReferenceParts(resource.ParentExternalId, out var categoryId, out var attributeId))
                    return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "HEPSIBURADA_REFERENCE_SCOPE_INVALID", "Enum değerleri categoryId/attributeId kapsamı ister.", HttpStatusCode.BadRequest);
                endpoint = AttributeValues(categoryId, attributeId, pageNumber, limit);
                break;
            default:
                return await Unsupported<AdapterPageResult<RemoteReferenceItem>>("Hepsiburada referans türü desteklenmiyor.");
        }

        var response = await SendAsync(account, account.CatalogBaseAddress, HttpMethod.Get, endpoint, cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(response.Error!, response.RateLimit);
        if (HepsiburadaJsonMapper.ReferenceApiErrorCode(response.Value!.RootElement) is { } apiErrorCode)
        {
            var (code, message) = apiErrorCode switch
            {
                1001 => ("HEPSIBURADA_REFERENCE_CATEGORY_NOT_LEAF", "Hepsiburada bu kategori için özellik değerlerini vermedi; kategori uç kategori değil."),
                1002 => ("HEPSIBURADA_REFERENCE_CATEGORY_INACTIVE", "Hepsiburada bu kategori için özellik değerlerini vermedi; kategori aktif değil."),
                1003 => ("HEPSIBURADA_REFERENCE_CATEGORY_UNAVAILABLE", "Hepsiburada bu kategori için özellik değerlerini vermedi; kategori uç ve aktif değil."),
                1004 or 1006 => ("HEPSIBURADA_REFERENCE_CATEGORY_NOT_FOUND", "Hepsiburada kategori kimliğini bulamadı."),
                1005 => ("HEPSIBURADA_REFERENCE_CATEGORY_RELATION_MISSING", "Hepsiburada kategori için özellik ilişkisini bulamadı."),
                2001 => ("HEPSIBURADA_REFERENCE_ATTRIBUTE_NOT_ENUM", "Hepsiburada bu özelliğin enum türünde olmadığını bildirdi; bu özellik için değer listesi bulunmuyor."),
                2002 => ("HEPSIBURADA_REFERENCE_ATTRIBUTE_NOT_FOUND", "Hepsiburada bu kategori özelliği kimliğini bulamadı."),
                _ => ($"HEPSIBURADA_REFERENCE_API_REJECTED_{apiErrorCode}", $"Hepsiburada referans isteğini {apiErrorCode} koduyla reddetti.")
            };
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(new(AdapterErrorClass.Validation, code, message, null, null, null), response.RateLimit);
        }
        try
        {
            var result = HepsiburadaJsonMapper.References(type, response.Value!.RootElement, resource.ParentExternalId, pageNumber, limit);
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(result, response.RateLimit);
        }
        catch (JsonException exception)
        {
            var message = exception.Message switch
            {
                "Hepsiburada referans yanıtında veri listesi yok." => "Hepsiburada başarılı yanıtında kategori, özellik veya değer listesi bulunamadı.",
                _ when exception.Message.StartsWith("Hepsiburada enum değerinde ", StringComparison.Ordinal) => $"Hepsiburada değer listesindeki bir seçeneğin alanları beklenen biçimde değil. {exception.Message[("Hepsiburada enum değerinde ".Length)..]}",
                _ => "Hepsiburada referans yanıtı beklenen veri sözleşmesiyle eşleşmiyor."
            };
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_REFERENCE_CONTRACT_INVALID", message, HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var result = await ListCatalogAsync(context, page, filter, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(result.Error!, result.RateLimit);
        var products = result.Value!.Items.SelectMany(product => product.Variants.Select(variant => new RemoteProduct(product.ExternalProductId, variant.ExternalVariantId, variant.Barcode, variant.Sku, variant.RawJson))).ToArray();
        return AdapterResult<AdapterPageResult<RemoteProduct>>.Success(new(products, result.Value.NextCursor, result.Value.HasMore, result.Value.TotalCount, result.Value.Issues), result.RateLimit);
    }

    private async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogStructureAsync(HepsiburadaRequestContext account, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (account.CatalogBaseAddress is null) return await Unsupported<AdapterPageResult<RemoteCatalogProduct>>("Hepsiburada katalog base URL'si yapılandırılmamış.");
        var (pageNumber, limit) = ReferencePage(page, 500);
        var response = await SendAsync(account, account.CatalogBaseAddress, HttpMethod.Get, MerchantProducts(account, pageNumber, limit), cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(response.Error!, response.RateLimit);
        try
        {
            return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(HepsiburadaJsonMapper.CatalogStructurePage(response.Value!.RootElement, pageNumber, limit), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_MERCHANT_PRODUCTS_CONTRACT_INVALID", "Hepsiburada mağaza ürün yanıtı beklenen sözleşmeyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    internal static string MerchantProducts(HepsiburadaRequestContext context, int page, int size) => $"api/products/all-products-of-merchant/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}?page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";

    public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (filter.CatalogStructure) return await ListCatalogStructureAsync(account, page, cancellationToken);
        var (offset, limit) = Page(page, settings.PageSize);
        var productLookup = !string.IsNullOrWhiteSpace(filter.ProductMainId) ? filter.ProductMainId : filter.Barcode;
        var lookupQueries = ListingLookupQueries(productLookup);
        var attempts = lookupQueries.Count == 0 ? new string?[] { null } : lookupQueries.Cast<string?>().ToArray();
        for (var attemptIndex = 0; attemptIndex < attempts.Length; attemptIndex++)
        {
            var query = new List<string> { $"offset={offset.ToString(CultureInfo.InvariantCulture)}", $"limit={limit.ToString(CultureInfo.InvariantCulture)}" };
            if (filter.ModifiedAfter is { } modifiedAfter) query.Add("updateStartDate=" + Uri.EscapeDataString(modifiedAfter.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            if (attempts[attemptIndex] is { } lookupQuery) query.Add(lookupQuery);
            var response = await SendAsync(account, account.ListingBaseAddress, HttpMethod.Get, Listings(account, string.Join('&', query)), cancellationToken);
            using var responseDocument = response.Value;
            if (!response.IsSuccess)
            {
                if (ShouldTryNextListingLookup(attemptIndex, attempts.Length, page.Cursor, 0, response.Error?.Class)) continue;
                return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(response.Error!, response.RateLimit);
            }
            try
            {
                var pageResult = HepsiburadaJsonMapper.ListingPage(response.Value!.RootElement);
                var items = pageResult.Items.Select(HepsiburadaJsonMapper.CatalogProduct).ToArray();
                if (!string.IsNullOrWhiteSpace(productLookup))
                    items = items.Where(item => ListingProductMatchesLookup(item, productLookup)).ToArray();
                var nextOffset = offset + pageResult.Items.Count;
                var hasMore = pageResult.TotalCount is { } total ? nextOffset < total : pageResult.Items.Count == limit;
                var mapped = new AdapterPageResult<RemoteCatalogProduct>(items, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, pageResult.TotalCount);

                // Order lines can expose either hbSku or merchantSku. Try the
                // documented productId, hbSkuList, and merchantSkuList filters,
                // returning only a listing that exactly matches the requested key.
                if (ShouldTryNextListingLookup(attemptIndex, attempts.Length, page.Cursor, items.Length, null)) continue;
                return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(mapped, response.RateLimit);
            }
            catch (JsonException)
            {
                return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_LISTING_CONTRACT_INVALID", "Hepsiburada listing yanıtı beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
            }
        }

        return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new([], null, false, 0), null);
    }

    internal static IReadOnlyList<string> ListingLookupQueries(string? productLookup)
    {
        var value = productLookup?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return [];
        var escaped = Uri.EscapeDataString(value);
        return [$"productId={escaped}", $"hbSkuList={escaped}", $"merchantSkuList={escaped}"];
    }

    internal static bool ListingProductMatchesLookup(RemoteCatalogProduct product, string lookup)
    {
        var normalized = lookup.Trim();
        return normalized.Length > 0
            && (string.Equals(product.ExternalProductId, normalized, StringComparison.OrdinalIgnoreCase)
                || product.Variants.Any(variant =>
                    string.Equals(variant.ExternalVariantId, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(variant.Sku, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(variant.Barcode, normalized, StringComparison.OrdinalIgnoreCase)));
    }

    internal static bool ShouldTryNextListingLookup(int attemptIndex, int attemptCount, string? cursor, int itemCount, AdapterErrorClass? errorClass) =>
        attemptIndex + 1 < attemptCount
        && cursor is null
        && (errorClass == AdapterErrorClass.NotFound || errorClass is null && itemCount == 0);
    public async Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var page = await ListAsync(context, new(null, Math.Clamp(settings.PageSize, 1, 10)), new(null, Barcode: barcode), cancellationToken);
        if (!page.IsSuccess) return AdapterResult<RemoteProduct?>.Failure(page.Error!, page.RateLimit);
        var lookup = barcode.Trim();
        var product = page.Value!.Items.FirstOrDefault(item =>
            string.Equals(item.Barcode, lookup, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Sku, lookup, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.ExternalVariantId, lookup, StringComparison.OrdinalIgnoreCase));
        return AdapterResult<RemoteProduct?>.Success(product, page.RateLimit);
    }
    internal static string Listings(HepsiburadaRequestContext context, string query) => $"listings/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}?{query}";
    internal static string Categories(int page, int limit) => $"api/categories/get-all-categories?leaf=true&status=ACTIVE&available=true&version=1&page={page.ToString(CultureInfo.InvariantCulture)}&size={limit.ToString(CultureInfo.InvariantCulture)}";
    internal static string CategoryAttributes(string categoryId) => $"api/categories/{Uri.EscapeDataString(categoryId)}/attributes?version=2";
    internal static string AttributeValues(string categoryId, string attributeId, int page, int limit) => $"api/categories/{Uri.EscapeDataString(categoryId)}/attribute/{Uri.EscapeDataString(attributeId)}/values?version=5&page={page.ToString(CultureInfo.InvariantCulture)}&size={limit.ToString(CultureInfo.InvariantCulture)}";

    private static (int Page, int Limit) ReferencePage(AdapterPageRequest page, int maximumLimit)
    {
        var pageNumber = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPage) ? Math.Clamp(parsedPage, 0, 1_000_000) : 0;
        return (pageNumber, Math.Clamp(page.Limit, 1, maximumLimit));
    }

    private static bool TryReferenceParts(string? value, out string categoryId, out string attributeId)
    {
        var parts = value?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        categoryId = parts.Length == 2 ? parts[0] : "";
        attributeId = parts.Length == 2 ? parts[1] : "";
        return parts.Length == 2;
    }
}
