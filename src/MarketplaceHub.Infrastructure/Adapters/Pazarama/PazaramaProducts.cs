using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Product reads (phase 3). Pazarama keeps approved and unapproved products on two lists. The
// approved list pages by cursor (at most 100 a request); the unapproved list pages by pageIndex
// (or by cursor in its newer shape). Neither has a modification filter, so every read walks all
// pages. One catalog scan walks the approved list first and then, when pending products are
// wanted, the unapproved list. The scan position travels in the page cursor:
//   "A"/"A:{cursor}"                approved list, by cursor
//   "U:{pageIndex}:{firstCode}"     unapproved list, by page index (firstCode guards against a
//                                   server that ignores the index and repeats page one)
//   "C:{cursor}"                    unapproved list, by cursor
// Product create, update and sale open/close come later (phase 9).
public sealed partial class PazaramaHttpClient : IProductPort
{
    internal const int ProductPageSize = 100;
    internal const string UnapprovedProducts = "product/products/unapproved";
    private const string ProductWritesLater = "Pazarama ürün açma ve güncelleme sonraki entegrasyon aşamasında açılacak.";

    // A single-product lookup reaches the adapter as a barcode or, from the import screen, as the text
    // typed into the lookup box; either way Pazarama finds a product by its barcode (Code).
    private static string? LookupCode(ProductReadFilter filter) =>
        !string.IsNullOrWhiteSpace(filter.Barcode) ? filter.Barcode.Trim() : !string.IsNullOrWhiteSpace(filter.ProductMainId) ? filter.ProductMainId.Trim() : null;

    internal static string ApprovedPage(string? cursor, int size, ProductReadFilter filter)
    {
        var query = $"{ApprovedProducts}?Size={size.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(cursor)) query += $"&Cursor={Uri.EscapeDataString(cursor)}";
        if (LookupCode(filter) is { } code) query += $"&Code={Uri.EscapeDataString(code)}";
        return query;
    }

    internal static string UnapprovedPage(int pageIndex, string? cursor, int size, ProductReadFilter filter)
    {
        var count = size.ToString(CultureInfo.InvariantCulture);
        var query = cursor is null
            ? $"{UnapprovedProducts}?pageIndex={pageIndex.ToString(CultureInfo.InvariantCulture)}&pageSize={count}&Size={count}"
            : $"{UnapprovedProducts}?Size={count}&Cursor={Uri.EscapeDataString(cursor)}";
        if (LookupCode(filter) is { } code) query += $"&Code={Uri.EscapeDataString(code)}";
        return query;
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<AdapterPageResult<RemoteCatalogProduct>>();
        var size = Math.Clamp(page.Limit, 1, ProductPageSize);
        var position = PazaramaProductMapper.Position(page.Cursor);
        return position.Phase == 'A'
            ? await ListApprovedAsync(account, position, size, filter, cancellationToken)
            : await ListUnapprovedAsync(account, position, size, filter, cancellationToken);
    }

    private async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListApprovedAsync(PazaramaRequestContext account, PazaramaProductMapper.ScanPosition position, int size, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var result = await SendAsync(account, HttpMethod.Get, ApprovedPage(position.Cursor, size, filter), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try
        {
            var parsed = PazaramaProductMapper.Page(document.RootElement, pending: false);
            var more = parsed.NextCursor is not null && parsed.Items.Count + parsed.Issues.Count > 0;
            // The approved list is done: continue with the unapproved one when pending products are wanted.
            var next = more ? "A:" + parsed.NextCursor : filter.IncludePendingApproval ? "U:1:" : null;
            return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(parsed.Items, next, next is not null, null, parsed.Issues), result.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "PAZARAMA_PRODUCT_CONTRACT_INVALID", "Pazarama onaylı ürün yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }
    }

    private async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListUnapprovedAsync(PazaramaRequestContext account, PazaramaProductMapper.ScanPosition position, int size, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var index = position.PageIndex;
        var byCursor = position.Phase == 'C';
        var result = await SendAsync(account, HttpMethod.Get, UnapprovedPage(index, byCursor ? position.Cursor : null, size, filter), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(result.Error!, result.RateLimit);
        PazaramaProductMapper.ProductPage parsed;
        try
        {
            using var document = result.Value!;
            parsed = PazaramaProductMapper.Page(document.RootElement, pending: true);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "PAZARAMA_PRODUCT_CONTRACT_INVALID", "Pazarama onaysız ürün yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }

        // Index paging: the first request asks for page 1. When the server counts from zero it
        // answers that as the second page (hasPreviousPage); then page 0 is read instead and the
        // scan carries on from page 1.
        if (!byCursor && index == 1 && position.FirstCode is null && parsed.HasPreviousPage == true)
        {
            var zero = await SendAsync(account, HttpMethod.Get, UnapprovedPage(0, null, size, filter), cancellationToken);
            if (!zero.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(zero.Error!, zero.RateLimit);
            try
            {
                using var document = zero.Value!;
                parsed = PazaramaProductMapper.Page(document.RootElement, pending: true);
            }
            catch (JsonException)
            {
                return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "PAZARAMA_PRODUCT_CONTRACT_INVALID", "Pazarama onaysız ürün yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
            }
            index = 0;
        }

        var next = PazaramaProductMapper.NextUnapprovedCursor(parsed, byCursor, index, position.FirstCode, size);
        return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(
            new(next is null && !byCursor && position.FirstCode is not null && parsed.FirstCode == position.FirstCode ? [] : parsed.Items, next, next is not null, parsed.TotalCount, parsed.Issues),
            result.RateLimit);
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
    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<RemoteOperationStatus>("PAZARAMA_OPERATION_UNSUPPORTED", ProductWritesLater));
    public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<RemotePublicationStatus>("PAZARAMA_PUBLICATION_STATUS_UNSUPPORTED", ProductWritesLater));

    private static Task<AdapterResult<RemoteOperationRef>> ProductWriteUnsupported() =>
        Task.FromResult(NotSupported<RemoteOperationRef>("PAZARAMA_PRODUCT_WRITE_UNSUPPORTED", ProductWritesLater));
}

public static class PazaramaProductMapper
{
    // Product status 10 closes a product for sale; every other value (0 unset, 1 open) is on sale.
    private const int ClosedProductStatus = 10;
    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    public sealed record ScanPosition(char Phase, string? Cursor, int PageIndex, string? FirstCode);

    public sealed record ProductPage(
        IReadOnlyList<RemoteCatalogProduct> Items,
        IReadOnlyList<AdapterPageIssue> Issues,
        string? NextCursor,
        bool? HasNextPage,
        bool? HasPreviousPage,
        int? TotalCount,
        string? FirstCode);

    public static ScanPosition Position(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor == "A") return new('A', null, 0, null);
        if (cursor.StartsWith("A:", StringComparison.Ordinal)) return new('A', cursor[2..], 0, null);
        if (cursor.StartsWith("C:", StringComparison.Ordinal)) return new('C', cursor[2..], 0, null);
        if (cursor.StartsWith("U:", StringComparison.Ordinal))
        {
            var parts = cursor[2..].Split(':', 2);
            var index = int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
            return new('U', null, index, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
        }
        // An unknown cursor restarts from the beginning rather than guessing.
        return new('A', null, 0, null);
    }

    // The position after an unapproved page, or null when the scan is over. By cursor: while the
    // server hands one out. By index: while the server says there is a next page (or the page was
    // full), stopping when a page starts with the code the previous one started with.
    public static string? NextUnapprovedCursor(ProductPage page, bool byCursor, int index, string? previousFirstCode, int size)
    {
        if (page.Items.Count + page.Issues.Count == 0) return null;
        if (byCursor || page.NextCursor is not null && !page.HasNextPage.HasValue) return page.NextCursor is null ? null : "C:" + page.NextCursor;
        if (previousFirstCode is not null && page.FirstCode == previousFirstCode) return null;
        var more = page.HasNextPage ?? page.Items.Count + page.Issues.Count >= size;
        return more ? $"U:{(index + 1).ToString(CultureInfo.InvariantCulture)}:{page.FirstCode}" : null;
    }

    public static ProductPage Page(JsonElement root, bool pending)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
            throw new JsonException("Pazarama product response has no data.");
        JsonElement rows = default;
        string? nextCursor = null;
        if (data.ValueKind == JsonValueKind.Array) rows = data;
        else if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "sellerProducts", "items", "products", "data" })
                if (data.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array) { rows = array; break; }
            nextCursor = Text(data, "nextCursor");
        }
        else if (data.ValueKind == JsonValueKind.Null) rows = default;
        if (rows.ValueKind != JsonValueKind.Array && data.ValueKind != JsonValueKind.Null)
            throw new JsonException("Pazarama product response has no product list.");
        nextCursor ??= Text(root, "nextCursor");

        var items = new List<RemoteCatalogProduct>();
        var issues = new List<AdapterPageIssue>();
        string? firstCode = null;
        if (rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                firstCode ??= Text(row, "code");
                var product = Product(row, pending);
                if (product is null) issues.Add(new("PRODUCT_INVALID", Text(row, "stockCode") ?? Text(row, "name") ?? "unknown", "Pazarama ürünü içeri alınmadı: barkod (code) yok."));
                else items.Add(product);
            }
        return new(items, issues, nextCursor, Flag(root, "hasNextPage"), Flag(root, "hasPreviousPage"), Number(root, "totalCount") is { } total ? (int)Math.Min(total, int.MaxValue) : null, firstCode);
    }

    // One Pazarama item is one sellable variant keyed by its barcode (code). Items that share a
    // groupCode form one product; without one the barcode stands for the product too.
    public static RemoteCatalogProduct? Product(JsonElement item, bool pending)
    {
        var code = Text(item, "code");
        if (code is null) return null;
        var groupCode = Text(item, "groupCode");
        var stockCode = Text(item, "stockCode");
        var title = Text(item, "displayName") ?? Text(item, "name") ?? stockCode ?? code;
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
            foreach (var attribute in attributes.EnumerateArray())
                if (Text(attribute, "attributeName") is { } name && Text(attribute, "attributeValue") is { } value) options[name] = value;
        var images = new List<(int Order, string Url)>();
        if (item.TryGetProperty("images", out var imageList) && imageList.ValueKind == JsonValueKind.Array)
            foreach (var image in imageList.EnumerateArray())
                if (Text(image, "imageUrl") is { } url) images.Add(((int?)Number(image, "sortOrder") ?? int.MaxValue, url));
        var imageUrls = images.OrderBy(x => x.Order).Select(x => x.Url).Distinct(StringComparer.Ordinal).ToList();
        var categoryId = Text(item, "categoryId");
        var variant = new RemoteCatalogVariant(
            code,
            stockCode ?? code,
            code,
            // The group code is a GUID, not a model code the panel knows; it only groups the variants.
            null,
            options,
            (int?)Number(item, "productStatus") == ClosedProductStatus,
            Amount(item, "salePrice"),
            Amount(item, "listPrice"),
            Amount(item, "vatRate"),
            Amount(item, "stockCount"),
            "TRY",
            item.GetRawText(),
            imageUrls);
        return new(
            groupCode is null ? $"code:{code}" : $"group:{groupCode}",
            groupCode,
            title,
            Text(item, "description") ?? "",
            null,
            Text(item, "brandName"),
            categoryId is null or EmptyGuid ? null : categoryId,
            Text(item, "categoryName"),
            imageUrls,
            [variant],
            item.GetRawText(),
            IsPendingApproval: pending);
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

    // Plain number, numeric string or Pazarama's money object {value, ...}.
    private static decimal? Amount(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner)) value = inner;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static long? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static bool? Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
