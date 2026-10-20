using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Product reads (phase 3). The seller product query has no modification
// filter, so every read walks all pages (at most 250 per page). Each n11 item
// is one SKU; items sharing a productMainId form one product. Product create
// and price/stock writes are tasks; product update and archive come later.
public sealed partial class N11HttpClient : IProductPort
{
    internal const int ProductPageSize = 250;
    internal const string ProductCreateTask = "ms/product/tasks/product-create";
    internal const int MaxCreateSkus = 1000;
    private const string ProductWritesLater = "N11 ürün güncelleme ve arşivleme sonraki entegrasyon aşamasında açılacak; N11'de yalnız yeni ürün açılabilir.";

    internal static string ProductPage(int page, int size, ProductReadFilter filter)
    {
        var query = $"ms/product-query?page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(filter.Barcode)) query += $"&stockCode={Uri.EscapeDataString(filter.Barcode.Trim())}";
        if (!string.IsNullOrWhiteSpace(filter.ProductMainId)) query += $"&productMainId={Uri.EscapeDataString(filter.ProductMainId.Trim())}";
        if (!string.IsNullOrWhiteSpace(filter.ContentId)) query += $"&id={Uri.EscapeDataString(filter.ContentId.Trim())}";
        // C23: sender defaults to SELLER, which hides SKUs stocked in n11depom; ALL lists both.
        query += "&sender=ALL";
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

    // Product create (phase 5). n11 queues the SKUs as a PRODUCT_CREATE task; the per-SKU
    // result is read from the task details (GetOperationAsync) and approval from the
    // product query by stock code.
    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        var body = CreatePayload(publication.PayloadJson, settings.IntegratorName);
        if (body is null)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "N11_PRODUCT_PAYLOAD_INVALID", $"N11 ürün açma içeriği beklenen yapıda değil (1-{MaxCreateSkus} SKU).", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOperationRef>();
        if (WriteBlocked<RemoteOperationRef>(account, "ürün açma isteği") is { } blocked) return blocked;
        var result = await SendAsync(account, HttpMethod.Post, ProductCreateTask, cancellationToken, new StringContent(body, Encoding.UTF8, "application/json"));
        if (!result.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        return N11TaskMapper.Submitted(document.RootElement, timeProvider.GetUtcNow(), "ürün açma", "PRODUCT_CREATE") is { } submitted
            ? submitted.Error is null
                ? AdapterResult<RemoteOperationRef>.Success(submitted.Reference!, result.RateLimit)
                : AdapterResult<RemoteOperationRef>.Failure(submitted.Error, result.RateLimit)
            : Failure<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "N11_TASK_CONTRACT_INVALID", "N11 ürün açma yanıtında görev numarası yok.", HttpStatusCode.BadGateway);
    }

    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => ProductWriteUnsupported();
    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => ProductWriteUnsupported();

    // The shared port names the key "barcode"; on n11 a published SKU is found by its stock code.
    public async Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return Failure<RemotePublicationStatus>(AdapterErrorClass.Validation, "N11_STOCK_CODE_REQUIRED", "N11 ürün durumu için stok kodu gerekir.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemotePublicationStatus>();
        var stockCode = barcode.Trim();
        var result = await SendAsync(account, HttpMethod.Get, ProductPage(0, 20, new ProductReadFilter(null, stockCode)), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemotePublicationStatus>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try { return AdapterResult<RemotePublicationStatus>.Success(N11ProductMapper.PublicationStatus(document.RootElement, stockCode), result.RateLimit); }
        catch (JsonException)
        {
            return Failure<RemotePublicationStatus>(AdapterErrorClass.ContractViolation, "N11_PRODUCT_CONTRACT_INVALID", "N11 ürün sorgulama yanıtı beklenen sayfa yapısında değil.", HttpStatusCode.BadGateway);
        }
    }

    // The composer writes the whole n11 body; the integrator name comes from the
    // adapter settings so every write carries the same name. Null when the body is
    // not {"payload":{"skus":[1..1000 items]}}.
    internal static string? CreatePayload(string payloadJson, string integrator)
    {
        try
        {
            if (JsonNode.Parse(payloadJson) is not JsonObject root || root["payload"] is not JsonObject payload || payload["skus"] is not JsonArray skus || skus.Count is < 1 or > MaxCreateSkus) return null;
            payload["integrator"] = integrator;
            return root.ToJsonString();
        }
        catch (JsonException) { return null; }
    }

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

    // Publication state of one stock code in the shared approval vocabulary. Only an
    // item with exactly this stock code counts; no such item means n11 has not listed
    // it yet. Items of one model share productMainId, the same product id product
    // reads use, and the stock code is the variant id price/stock writes are keyed by.
    public static RemotePublicationStatus PublicationStatus(JsonElement root, string stockCode)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new JsonException("n11 product page has no content array.");
        var item = content.EnumerateArray().FirstOrDefault(x => string.Equals(Text(x, "stockCode"), stockCode, StringComparison.OrdinalIgnoreCase));
        if (item.ValueKind != JsonValueKind.Object) return new(stockCode, "NOT_FOUND", null, null, null, "{}");
        var status = (Text(item, "status") ?? Text(item, "productStatus") ?? "").ToUpperInvariant();
        var mappedStatus = status switch
        {
            "ACTIVE" => "APPROVED",
            "INAPPROVAL" or "INCATALOGAPPROVAL" => "PENDING_APPROVAL",
            "CATALOGREJECTED" or "PROHIBITED" => "REJECTED",
            "SUSPENDED" => "ARCHIVED",
            "" => "UNKNOWN",
            // Unlisted and unknown states are passed on as-is and end in manual review.
            _ => $"N11_{status}"
        };
        var rejectionCode = status switch
        {
            "CATALOGREJECTED" => "N11_PRODUCT_CATALOG_REJECTED",
            "PROHIBITED" => "N11_PRODUCT_PROHIBITED",
            _ => null
        };
        var productMainId = Text(item, "productMainId");
        var n11ProductId = Text(item, "n11ProductId");
        var externalProductId = productMainId is not null ? $"main:{productMainId}" : n11ProductId is not null ? $"n11:{n11ProductId}" : null;
        return new(stockCode, mappedStatus, externalProductId, Text(item, "stockCode"), rejectionCode, item.GetRawText());
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
