using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient
{
    public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(TrendyolErrorMapper.Configuration());
        var type = resource.ResourceType.Trim().ToUpperInvariant(); string endpoint;
        if (type == "CATEGORIES") endpoint = TrendyolEndpoints.Categories;
        else if (type == "BRANDS") endpoint = TrendyolEndpoints.Brands + $"?page={Page(page.Cursor)}&size={page.Limit}";
        else if (type == "CATEGORY_ATTRIBUTES" && !string.IsNullOrWhiteSpace(resource.ParentExternalId)) endpoint = TrendyolEndpoints.CategoryAttributes(resource.ParentExternalId);
        else if (type == "ATTRIBUTE_VALUES" && TryParts(resource.ParentExternalId, out var categoryId, out var attributeId)) endpoint = TrendyolEndpoints.AttributeValues(categoryId, attributeId) + $"?page={Page(page.Cursor)}&size={page.Limit}";
        else return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(TrendyolErrorMapper.Unsupported("Reference resource için resmî V2 endpoint doğrulanmadı."));
        var response = await SendAsync(authorized, HttpMethod.Get, endpoint, null, cancellationToken); if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var items = TrendyolJsonMapper.References(type, response.Value!, resource.ParentExternalId);
            if (type == "CATEGORY_ATTRIBUTES")
            {
                var requiredResponse = await SendAsync(authorized, HttpMethod.Get, endpoint + "?required=true", null, cancellationToken);
                if (!requiredResponse.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(requiredResponse.Error!, requiredResponse.RateLimit);
                var requiredIds = TrendyolJsonMapper.References(type, requiredResponse.Value!, resource.ParentExternalId)
                    .Select(item => item.ExternalId)
                    .ToHashSet(StringComparer.Ordinal);
                items = items.Select(item => item with { IsRequired = requiredIds.Contains(item.ExternalId) }).ToList();
            }
            var hasMore = type is "BRANDS" or "ATTRIBUTE_VALUES" && items.Count >= page.Limit;
            var next = hasMore ? (Page(page.Cursor) + 1).ToString(CultureInfo.InvariantCulture) : null;
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(items, next, hasMore), response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(TrendyolErrorMapper.Configuration());
        var size = Math.Clamp(page.Limit, 1, 100);
        var query = new List<string> { $"size={size}" };
        if (TryProductCursor(page.Cursor, out var pageNumber, out var nextPageToken))
        {
            if (!string.IsNullOrWhiteSpace(nextPageToken)) query.Add("nextPageToken=" + Uri.EscapeDataString(nextPageToken));
            else query.Add($"page={pageNumber}");
        }
        else query.Add("page=0");
        if (filter.ModifiedAfter is not null) { query.Add("startDate=" + filter.ModifiedAfter.Value.ToUnixTimeMilliseconds()); query.Add("dateQueryType=VARIANT_MODIFIED_DATE"); }
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ApprovedProducts(authorized.Connection.ExternalStoreId) + "?" + string.Join('&', query), null, cancellationToken); if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<AdapterPageResult<RemoteProduct>>.Success(TrendyolJsonMapper.Products(response.Value!), response.RateLimit); } catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(TrendyolErrorMapper.Configuration());
        var catalogPage = TrendyolCatalogCursorPolicy.Resolve(page.Cursor);
        var incremental = filter.ModifiedAfter is not null;
        var pending = catalogPage.Pass == TrendyolCatalogPass.PendingApproval;
        var query = CatalogQuery(catalogPage.Pass, catalogPage.Cursor, page.Limit, filter);
        var endpoint = pending
            ? TrendyolEndpoints.UnapprovedProducts(authorized.Connection.ExternalStoreId)
            : TrendyolEndpoints.ApprovedProducts(authorized.Connection.ExternalStoreId);
        var response = await SendAsync(authorized, HttpMethod.Get, endpoint + "?" + string.Join('&', query), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var result = pending
                ? TrendyolJsonMapper.PendingApprovalCatalogProducts(response.Value!)
                : TrendyolJsonMapper.CatalogProducts(response.Value!);
            if (pending)
                logger.LogInformation("Trendyol unapproved catalog page {Cursor} for connection {ConnectionId}: {Kept} rows kept, statuses {Statuses}, total {Total}.",
                    catalogPage.Cursor ?? "p:0", context.ConnectionId, result.Items.Count, TrendyolJsonMapper.UnapprovedStatusCounts(response.Value!), result.TotalCount);
            // C15: contentId is not a documented filter of /products/approved; if Trendyol ignores it the page holds
            // other products, so the page is narrowed here to the asked content.
            if (!pending && !string.IsNullOrWhiteSpace(filter.ContentId))
                result = FilterByContentId(result, filter.ContentId);
            return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(
                TrendyolCatalogCursorPolicy.Advance(result, catalogPage.Pass, incremental, filter.IncludePendingApproval),
                response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteProduct?>.Failure(TrendyolErrorMapper.Configuration());
        var normalizedBarcode = barcode.Trim();
        // The barcode status endpoint intentionally returns only approval metadata
        // and never contains the product images. The approved-product filter does
        // return the content-level images and supports an exact barcode filter.
        var encodedBarcode = Uri.EscapeDataString(normalizedBarcode);
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ApprovedProducts(authorized.Connection.ExternalStoreId) + $"?barcode={encodedBarcode}&size=100", null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<RemoteProduct?>.Failure(response.Error!, response.RateLimit);
        try
        {
            var page = TrendyolJsonMapper.Products(response.Value!);
            var product = page.Items.FirstOrDefault(item => string.Equals(item.Barcode, normalizedBarcode, StringComparison.OrdinalIgnoreCase));
            return AdapterResult<RemoteProduct?>.Success(product, response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemoteProduct?>.Failure(TrendyolErrorMapper.Contract()); }
    }
    // The catalog read's query. The waiting-for-approval pass reads /products/unapproved WITHOUT a status filter: the
    // whole unapproved list ("onay süreci devam eden ve reddedilen ürünler") comes back and the mapper drops only the
    // rows marked rejected. The import of 14 Nov 2026 asked status=pendingApproval and got an empty page while the owner
    // had products waiting for approval; the barcode status check reads the same endpoint without a status.
    // Nor does the pass take the incremental date window: a product waits for approval for days without being changed,
    // so "since the last read" left out every product put up before it (live 14 Nov 2026 14:49Z: the "Yeni Ürünleri
    // Aktar" run read the unapproved list from that morning's watermark and got total 0 while the owner's products
    // waited in "Onay Bekleyenler" since the 13th). The unapproved list is short; it is read whole every time.
    internal static List<string> CatalogQuery(TrendyolCatalogPass pass, string? cursor, int limit, ProductReadFilter filter)
    {
        var pending = pass == TrendyolCatalogPass.PendingApproval;
        var query = new List<string> { $"size={Math.Clamp(limit, 1, 100)}" };
        if (TryProductCursor(cursor, out var pageNumber, out var nextPageToken))
        {
            if (!string.IsNullOrWhiteSpace(nextPageToken)) query.Add("nextPageToken=" + Uri.EscapeDataString(nextPageToken));
            else query.Add($"page={pageNumber}");
        }
        else query.Add("page=0");
        if (!string.IsNullOrWhiteSpace(filter.Barcode)) query.Add("barcode=" + Uri.EscapeDataString(filter.Barcode));
        if (!string.IsNullOrWhiteSpace(filter.ProductMainId)) query.Add("productMainId=" + Uri.EscapeDataString(filter.ProductMainId));
        if (!pending && !string.IsNullOrWhiteSpace(filter.ContentId)) query.Add("contentId=" + Uri.EscapeDataString(filter.ContentId));
        if (!pending && filter.ModifiedAfter is { } modifiedAfter)
        {
            // Approved products by variant or content change.
            query.Add("startDate=" + modifiedAfter.ToUnixTimeMilliseconds());
            query.Add("dateQueryType=" + (pass == TrendyolCatalogPass.ContentChanged ? "CONTENT_MODIFIED_DATE" : "VARIANT_MODIFIED_DATE"));
        }
        return query;
    }

    private static bool TryProductCursor(string? value, out int page, out string? token)
    {
        page = 0; token = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith("p:", StringComparison.Ordinal) && int.TryParse(value.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0) { page = parsed; return true; }
        if (value.StartsWith("t:", StringComparison.Ordinal) && value.Length > 2) { token = value[2..]; return true; }
        token = value;
        return true;
    }
    private static bool TryParts(string? value, out string categoryId, out string attributeId) { var parts = value?.Split('/', 2, StringSplitOptions.RemoveEmptyEntries) ?? []; categoryId = parts.ElementAtOrDefault(0) ?? ""; attributeId = parts.ElementAtOrDefault(1) ?? ""; return parts.Length == 2; }

    internal static AdapterPageResult<RemoteCatalogProduct> FilterByContentId(AdapterPageResult<RemoteCatalogProduct> page, string contentId) =>
        page with { Items = [.. page.Items.Where(product => string.Equals(product.ExternalProductId, contentId.Trim(), StringComparison.Ordinal))] };
}
