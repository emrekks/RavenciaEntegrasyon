using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Product reads (phase 3). The seller product query has no modification
// filter, so every read walks all pages (at most 250 per page). Each n11 item
// is one SKU; items sharing a productMainId form one product. Product writes
// (create, update, price/stock) come with their own phases.
public sealed partial class N11HttpClient : IProductPort
{
    internal const int ProductPageSize = 250;
    private const string ProductWritesLater = "N11 ürün yazma işlemleri (açma, güncelleme, fiyat/stok) sonraki entegrasyon aşamalarında açılacak.";

    internal static string ProductPage(int page, int size, ProductReadFilter filter)
    {
        var query = $"ms/product-query?page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(filter.Barcode)) query += $"&stockCode={Uri.EscapeDataString(filter.Barcode.Trim())}";
        if (!string.IsNullOrWhiteSpace(filter.ProductMainId)) query += $"&productMainId={Uri.EscapeDataString(filter.ProductMainId.Trim())}";
        if (!string.IsNullOrWhiteSpace(filter.ContentId)) query += $"&id={Uri.EscapeDataString(filter.ContentId.Trim())}";
        return query;
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var pageNumber = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<AdapterPageResult<RemoteCatalogProduct>>();
        var result = await SendAsync(account, HttpMethod.Get, ProductPage(pageNumber, Math.Clamp(page.Limit, 1, ProductPageSize), filter), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try { return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(N11ProductMapper.Page(document.RootElement), result.RateLimit); }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "N11_PRODUCT_CONTRACT_INVALID", "N11 ürün sorgulama yanıtı beklenen sayfa yapısında değil.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var catalog = await ListCatalogAsync(context, page, filter, cancellationToken);
        if (!catalog.IsSuccess) return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(catalog.Error!, catalog.RateLimit);
        var items = catalog.Value!.Items
            .SelectMany(product => product.Variants.Select(variant => new RemoteProduct(product.ExternalProductId, variant.ExternalVariantId, variant.Barcode, variant.Sku, variant.RawJson)))
            .ToList();
        return AdapterResult<AdapterPageResult<RemoteProduct>>.Success(new(items, catalog.Value.NextCursor, catalog.Value.HasMore, catalog.Value.TotalCount, catalog.Value.Issues), catalog.RateLimit);
    }

    public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => ProductWriteUnsupported();

    public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<RemotePublicationStatus>("N11_PRODUCT_WRITE_UNSUPPORTED", ProductWritesLater));

    private static Task<AdapterResult<RemoteOperationRef>> ProductWriteUnsupported() =>
        Task.FromResult(NotSupported<RemoteOperationRef>("N11_PRODUCT_WRITE_UNSUPPORTED", ProductWritesLater));
}

public static class N11ProductMapper
{
    private const string BrandAttributeId = "1";

    public static AdapterPageResult<RemoteCatalogProduct> Page(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new JsonException("n11 product page has no content array.");
        var items = new List<RemoteCatalogProduct>();
        var issues = new List<AdapterPageIssue>();
        foreach (var item in content.EnumerateArray())
        {
            var product = Product(item);
            if (product is null) issues.Add(new("PRODUCT_INVALID", Text(item, "n11ProductId") ?? "unknown", "N11 ürünü içeri alınmadı: ürün kodu veya stok kodu yok."));
            else items.Add(product);
        }
        var page = Long(root, "number") ?? Long(root, "page") ?? 0;
        var totalPages = Long(root, "totalPages") ?? 0;
        var hasMore = content.GetArrayLength() > 0 && page + 1 < totalPages;
        var total = Long(root, "totalElements");
        return new(items, hasMore ? (page + 1).ToString(CultureInfo.InvariantCulture) : null, hasMore, total is null ? null : (int)Math.Min(total.Value, int.MaxValue), issues);
    }

    public static RemoteCatalogProduct? Product(JsonElement item)
    {
        var n11ProductId = Text(item, "n11ProductId");
        var stockCode = Text(item, "stockCode");
        if (n11ProductId is null && stockCode is null) return null;
        var productMainId = Text(item, "productMainId");
        var externalProductId = productMainId is null ? $"n11:{n11ProductId ?? stockCode}" : $"main:{productMainId}";
        var status = Text(item, "status") ?? "";
        var attributes = Attributes(item);
        var brand = attributes.FirstOrDefault(x => x.Id == BrandAttributeId || string.Equals(x.Name, "Marka", StringComparison.OrdinalIgnoreCase));
        var options = attributes
            .Where(x => x != brand)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        var images = item.TryGetProperty("imageUrls", out var urls) && urls.ValueKind == JsonValueKind.Array
            ? urls.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
            : [];
        var currency = Text(item, "currencyType") is { } code && !string.Equals(code, "TL", StringComparison.OrdinalIgnoreCase) ? code.ToUpperInvariant() : "TRY";
        var variant = new RemoteCatalogVariant(
            stockCode ?? $"n11:{n11ProductId}",
            stockCode ?? n11ProductId!,
            Text(item, "barcode"),
            productMainId,
            options,
            // Suspended, unlisted, prohibited or rejected products are not on sale.
            status is "Suspended" or "Unlisted" or "Prohibited" or "CatalogRejected",
            Decimal(item, "salePrice"),
            Decimal(item, "listPrice"),
            Decimal(item, "vatRate"),
            Decimal(item, "quantity"),
            currency,
            item.GetRawText(),
            images);
        return new(
            externalProductId,
            productMainId,
            Text(item, "title") ?? stockCode ?? n11ProductId!,
            Text(item, "description") ?? "",
            null,
            brand?.Value,
            Text(item, "categoryId"),
            null,
            images,
            [variant],
            item.GetRawText(),
            IsPendingApproval: status is "InApproval" or "InCatalogApproval");
    }

    private sealed record Attribute(string? Id, string Name, string Value);

    private static List<Attribute> Attributes(JsonElement item)
    {
        if (!item.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array) return [];
        return attributes.EnumerateArray()
            .Select(x => (id: Text(x, "attributeId"), name: Text(x, "attributeName"), value: Text(x, "attributeValue")))
            .Where(x => x.name is not null && x.value is not null)
            .Select(x => new Attribute(x.id, x.name!, x.value!))
            .ToList();
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static decimal? Decimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static long? Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
