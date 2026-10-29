using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Contracts;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient(IHttpClientFactory clients, TrendyolAuthenticationHandler authentication, IConfiguration configuration, IOptions<TrendyolOptions> trendyolOptions, TimeProvider timeProvider, ILogger<TrendyolHttpClient> logger)
    : IConnectionPort, IReferenceDataPort, IProductPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IReturnPort, IReturnAuditPort, IInvoiceMarketplacePort
{
    private bool GlobalWritesEnabled => configuration.GetValue<bool>("FeatureFlags:ExternalWrites");

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<ConnectionIdentity>.Failure(TrendyolErrorMapper.Configuration());
        var result = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.OrderStream(authorized.Connection.ExternalStoreId) + "?size=1", null, cancellationToken); if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        return AdapterResult<ConnectionIdentity>.Success(new("TRENDYOL", authorized.Connection.Environment, authorized.Connection.ExternalStoreId, "V2", authorized.Connection.ExternalStoreId), result.RateLimit);
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var test = await TestAsync(context, cancellationToken); if (!test.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(test.Error!, test.RateLimit);
        var identity = test.Value!; var now = timeProvider.GetUtcNow();
        var references = await ReadAsync(context, new("CATEGORIES", null), new(null, 1), cancellationToken);
        var products = await ListAsync(context, new(null, 1), new(null), cancellationToken);
        var returns = await PollAsync(context, new ReturnPollWindow(null, null), new(null, 1), cancellationToken);
        IReadOnlyList<CapabilityEvidence> evidence =
        [
            SupportedEvidence(MarketplaceCapabilities.ConnectionTest, identity, "https://developers.trendyol.com/v2.0/docs/authorization", "Stage/Production kimlik doğrulaması order stream read ile geçti.", now),
            SupportedEvidence(MarketplaceCapabilities.OrderRead, identity, "https://developers.trendyol.com/v2.0/docs/getshipmentpackagesstream", "Cursor order stream read yanıtı alındı.", now),
            ReadProbeEvidence(MarketplaceCapabilities.ReferenceRead, identity, "https://developers.trendyol.com/v2.0/docs/trendyol-category-list-getcategorytree", references, "Kategori ağacı", now),
            ReadProbeEvidence(MarketplaceCapabilities.ProductRead, identity, "https://developers.trendyol.com/v2.0/docs/product-filtering-approved-products-v2", products, "Onaylı ürün listesi", now),
            ReadProbeEvidence(MarketplaceCapabilities.ReturnRead, identity, "https://developers.trendyol.com/v2.0/docs/getting-returned-orders-getclaims", returns, "İade talepleri", now),
            SupportedEvidence(MarketplaceCapabilities.QuestionRead, identity, "https://developers.trendyol.com/docs/m%C3%BC%C5%9Fteri-sorular%C4%B1n%C4%B1-%C3%A7ekme", "Ürün soruları ve yazışma geçmişi için Trendyol'un resmî müşteri soru listeleme endpoint'i doğrulandı.", now),
            SupportedEvidence(MarketplaceCapabilities.QuestionWrite, identity, "https://developers.trendyol.com/docs/m%C3%BC%C5%9Fteri-sorular%C4%B1n%C4%B1-cevaplama", "Cevap gönderme yeteneği resmî endpoint dokümanına göre kaydedildi; bağlantı kontrolünde dış yazma çağrısı çalıştırılmaz.", now)
        ];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, test.RateLimit);
    }

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
        var size = Math.Clamp(page.Limit, 1, 100);
        var query = new List<string> { $"size={size}" };
        if (TryProductCursor(catalogPage.Cursor, out var pageNumber, out var nextPageToken))
        {
            if (!string.IsNullOrWhiteSpace(nextPageToken)) query.Add("nextPageToken=" + Uri.EscapeDataString(nextPageToken));
            else query.Add($"page={pageNumber}");
        }
        else query.Add("page=0");
        if (!string.IsNullOrWhiteSpace(filter.Barcode)) query.Add("barcode=" + Uri.EscapeDataString(filter.Barcode));
        if (!string.IsNullOrWhiteSpace(filter.ProductMainId)) query.Add("productMainId=" + Uri.EscapeDataString(filter.ProductMainId));
        if (!pending && !string.IsNullOrWhiteSpace(filter.ContentId)) query.Add("contentId=" + Uri.EscapeDataString(filter.ContentId));
        if (filter.ModifiedAfter is { } modifiedAfter)
        {
            // Approved products by variant or content change; waiting products by their last change.
            query.Add("startDate=" + modifiedAfter.ToUnixTimeMilliseconds());
            query.Add("dateQueryType=" + catalogPage.Pass switch
            {
                TrendyolCatalogPass.ContentChanged => "CONTENT_MODIFIED_DATE",
                TrendyolCatalogPass.PendingApproval => "LAST_MODIFIED_DATE",
                _ => "VARIANT_MODIFIED_DATE"
            });
        }
        if (pending) query.Add("status=pendingApproval");
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

    public async Task<AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>> ReturnLineAuditsAsync(AdapterContext context, string externalLineId, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ClaimItemAudit(authorized.Connection.ExternalStoreId, externalLineId.Trim()), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>.Success(TrendyolJsonMapper.ReturnLineAudits(response.Value!), response.RateLimit); }
        catch (JsonException) { return AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>.Failure(TrendyolErrorMapper.Contract()); }
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

    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        logger.LogInformation("Trendyol PRODUCT_CREATE request started for connection {ConnectionId} and product {ProductId}.", context.ConnectionId, publication.ProductId);
        var result = await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductCreate, publication.PayloadJson, "PRODUCT_CREATE_V2", cancellationToken);
        if (result.IsSuccess)
            logger.LogInformation("Trendyol PRODUCT_CREATE accepted for product {ProductId}; external operation {ExternalOperationId}.", publication.ProductId, result.Value!.ExternalOperationId);
        else
            logger.LogWarning("Trendyol PRODUCT_CREATE failed for product {ProductId}: {ErrorCode}.", publication.ProductId, result.Error?.Code);
        return result;
    }

    public async Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductUpdateUnapproved, publication.UnapprovedPayloadJson, "PRODUCT_UPDATE_UNAPPROVED_V2", cancellationToken);

    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductUpdateContent, publication.ApprovedContentPayloadJson, "PRODUCT_UPDATE_CONTENT_V2", cancellationToken);

    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductUpdateVariant, publication.ApprovedVariantPayloadJson, "PRODUCT_UPDATE_VARIANT_V2", cancellationToken);

    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductUpdateDelivery, publication.ApprovedDeliveryPayloadJson, "PRODUCT_UPDATE_DELIVERY_V2", cancellationToken);

    public async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Trendyol PRODUCT_CREATE batch poll started for operation {ExternalOperationId}.", externalOperationId);
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOperationStatus>.Failure(TrendyolErrorMapper.Configuration()); var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.BatchResult(authorized.Connection.ExternalStoreId, externalOperationId), null, cancellationToken); if (!response.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(response.Error!, response.RateLimit);
        try
        {
            var result = TrendyolJsonMapper.Batch(response.Value!, externalOperationId);
            logger.LogInformation("Trendyol PRODUCT_CREATE batch poll completed for operation {ExternalOperationId}: status {Status}, lines {LineCount}, accepted {AcceptedCount}, rejected {RejectedCount}.", externalOperationId, result.Status, result.Lines.Count, result.Lines.Count(x => x.Succeeded), result.Lines.Count(x => !x.Succeeded));
            return AdapterResult<RemoteOperationStatus>.Success(result, response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemoteOperationStatus>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return AdapterResult<RemotePublicationStatus>.Failure(TrendyolErrorMapper.Contract());
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemotePublicationStatus>.Failure(TrendyolErrorMapper.Configuration());
        var encodedBarcode = Uri.EscapeDataString(barcode.Trim());
        var approved = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ApprovedProducts(authorized.Connection.ExternalStoreId) + $"?barcode={encodedBarcode}&size=100", null, cancellationToken);
        if (!approved.IsSuccess) return AdapterResult<RemotePublicationStatus>.Failure(approved.Error!, approved.RateLimit);
        try
        {
            var approvedStatus = TrendyolJsonMapper.ApprovedPublicationStatus(approved.Value!, barcode.Trim());
            if (approvedStatus is not null) return AdapterResult<RemotePublicationStatus>.Success(approvedStatus, approved.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemotePublicationStatus>.Failure(TrendyolErrorMapper.Contract()); }

        var unapproved = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.UnapprovedProducts(authorized.Connection.ExternalStoreId) + $"?barcode={encodedBarcode}&size=1000", null, cancellationToken);
        if (!unapproved.IsSuccess) return AdapterResult<RemotePublicationStatus>.Failure(unapproved.Error!, unapproved.RateLimit);
        try
        {
            var unapprovedStatus = TrendyolJsonMapper.UnapprovedPublicationStatus(unapproved.Value!, barcode.Trim());
            return AdapterResult<RemotePublicationStatus>.Success(unapprovedStatus ?? new(barcode.Trim(), "NOT_FOUND", null, null, null, "{}"), unapproved.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemotePublicationStatus>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Put, TrendyolEndpoints.ProductArchiveState, payloadJson, "PRODUCT_ARCHIVE_STATE", cancellationToken);

    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.PriceAndInventory, payloadJson, "PRICE_AND_INVENTORY", cancellationToken);

    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(TrendyolErrorMapper.Configuration()); var query = new List<string> { $"size={Math.Clamp(page.Limit, 1, 200)}" };
        // packageItemStatuses is optional in the Stream contract. Omitting it makes this full-scan/incremental
        // reader receive every package status, including marketplace-specific micro-export states that may
        // not be represented by the public status list.
        if (!string.IsNullOrWhiteSpace(page.Cursor)) query.Add("nextCursor=" + Uri.EscapeDataString(page.Cursor));
        if (window.ModifiedAfter is not null) query.Add("lastModifiedStartDate=" + window.ModifiedAfter.Value.ToUnixTimeMilliseconds());
        if (window.ModifiedBefore is not null) query.Add("lastModifiedEndDate=" + window.ModifiedBefore.Value.ToUnixTimeMilliseconds());
        var endpoint = TrendyolEndpoints.OrderStream(authorized.Connection.ExternalStoreId);
        var response = await SendAsync(authorized, HttpMethod.Get, endpoint + "?" + string.Join('&', query), null, cancellationToken, storeFrontCode: window.StoreFrontCode); if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(response.Error!, response.RateLimit);
        LogOrderResponseShape(response.Value!);
        try
        {
            // Orders carry the storefront they were read under, so later targeted reads ask there first.
            var read = TrendyolJsonMapper.Orders(response.Value!);
            var storeFrontCode = string.IsNullOrWhiteSpace(window.StoreFrontCode) ? TrendyolLookupPolicy.HomeStoreFront : window.StoreFrontCode.Trim().ToUpperInvariant();
            var mapped = read with { Items = [.. read.Items.Select(order => order with { StoreFrontCode = storeFrontCode })] };
            return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(mapped, response.RateLimit);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Trendyol order response failed contract mapping during polling.");
            return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(TrendyolErrorMapper.Contract());
        }
    }

    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
        => GetAsync(context, externalOrderId, null, cancellationToken);

    // Order V2 keeps one month of packages and takes at most two weeks per request; the stream
    // keeps three months. A known order is read from its own storefront, by V2 while it is
    // younger than a month and from the stream around its last change after that; an unknown
    // order number is looked up in the last two weeks of every storefront, Türkiye first.
    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, OrderLookupHint? hint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalOrderId)) return AdapterResult<RemoteOrder>.Failure(TrendyolErrorMapper.Contract());
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOrder>.Failure(TrendyolErrorMapper.Configuration());
        var normalizedOrderNumber = externalOrderId.Trim();
        var now = timeProvider.GetUtcNow();
        var storefronts = await LookupStorefrontsAsync(authorized, hint, cancellationToken);
        AdapterPageIssue? firstPageIssue = null;
        RateLimitMetadata? rateLimit = null;
        if (TrendyolLookupPolicy.V2Window(hint, now) is { } window)
        {
            var dateFilters = $"startDate={window.Start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&endDate={window.End.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            foreach (var storeFrontCode in storefronts)
            {
                var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.Orders(authorized.Connection.ExternalStoreId) + $"?{dateFilters}&orderNumber={Uri.EscapeDataString(normalizedOrderNumber)}&page=0&size=200", null, cancellationToken, storeFrontCode: storeFrontCode);
                // Some order numbers are rejected unquoted; the quoted form is the documented fallback.
                if (!response.IsSuccess && response.Error?.HttpStatus == 400)
                    response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.Orders(authorized.Connection.ExternalStoreId) + $"?{dateFilters}&orderNumber={Uri.EscapeDataString($"\"{normalizedOrderNumber}\"")}&page=0&size=200", null, cancellationToken, storeFrontCode: storeFrontCode);
                if (!response.IsSuccess)
                {
                    if (response.Error?.HttpStatus is 400 or 404) continue;
                    return AdapterResult<RemoteOrder>.Failure(response.Error!, response.RateLimit);
                }
                rateLimit = response.RateLimit;
                try
                {
                    var page = TrendyolJsonMapper.Orders(response.Value!);
                    firstPageIssue ??= page.Issues?.FirstOrDefault();
                    var order = TrendyolJsonMapper.MergeOrderPackages(page.Items, normalizedOrderNumber);
                    if (order is not null) return AdapterResult<RemoteOrder>.Success(order with { StoreFrontCode = storeFrontCode }, response.RateLimit);
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Trendyol order response failed contract mapping during direct lookup for {ExternalOrderId}.", normalizedOrderNumber);
                    return AdapterResult<RemoteOrder>.Failure(TrendyolErrorMapper.Contract());
                }
            }
        }

        // A known order V2 cannot serve is read from the stream around its last recorded change.
        if (TrendyolLookupPolicy.StreamWindow(hint, now) is { } streamWindow)
        {
            var scan = await ScanOrderStreamAsync(authorized, normalizedOrderNumber, streamWindow.Start, streamWindow.End, storefronts[0], rateLimit, cancellationToken);
            if (scan.Result is { } streamed) return streamed;
            firstPageIssue ??= scan.Issue;
        }

        if (firstPageIssue is not null)
            return AdapterResult<RemoteOrder>.Failure(new(AdapterErrorClass.ContractViolation, firstPageIssue.Code, firstPageIssue.Message, null, null, null));
        return AdapterResult<RemoteOrder>.Failure(new(AdapterErrorClass.NotFound, "REMOTE_ORDER_NOT_FOUND", "Platform siparişi bulunamadı.", 404, null, null));
    }

    // The order stream is read with the same page cap as the package scan (both cover a 12-day window). Every
    // package of the order is collected before the merge: a split order's packages can sit on different pages,
    // so the first match does not end the read. A cursor the stream hands back twice would loop forever.
    internal const int MaxOrderStreamPages = MaxPackageStreamPages;
    internal const string OrderScanIncompleteCode = "TRENDYOL_ORDER_SCAN_INCOMPLETE";
    private static readonly AdapterError OrderScanIncomplete = new(AdapterErrorClass.TransientNetwork, OrderScanIncompleteCode,
        "Trendyol sipariş akışı sonuna kadar okunamadı; okuma daha sonra yeniden denenecek.", 503, TimeSpan.FromMinutes(15), null);

    // Result is set when the read ends the lookup (the order, or an error); otherwise the caller goes on to
    // "not found" with the first page issue the stream reported.
    internal async Task<(AdapterResult<RemoteOrder>? Result, AdapterPageIssue? Issue)> ScanOrderStreamAsync(
        TrendyolRequestContext authorized, string normalizedOrderNumber, DateTimeOffset modifiedStart, DateTimeOffset modifiedEnd,
        string storeFrontCode, RateLimitMetadata? rateLimit, CancellationToken cancellationToken)
    {
        AdapterPageIssue? firstPageIssue = null;
        var packages = new List<RemoteOrder>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? nextCursor = null;
        var pages = 0;
        do
        {
            if (++pages > MaxOrderStreamPages)
            {
                // The window was not read to its end, so "not found" is unknown, not a 404 (as in ScanPackageStreamAsync).
                logger.LogWarning("Trendyol order {ExternalOrderId} stream lookup stopped after {Pages} pages.", normalizedOrderNumber, MaxOrderStreamPages);
                return (AdapterResult<RemoteOrder>.Failure(OrderScanIncomplete, rateLimit), firstPageIssue);
            }
            var query = new List<string>
            {
                "size=200",
                "lastModifiedStartDate=" + modifiedStart.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                "lastModifiedEndDate=" + modifiedEnd.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            };
            if (nextCursor is not null) query.Add("nextCursor=" + Uri.EscapeDataString(nextCursor));
            var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.OrderStream(authorized.Connection.ExternalStoreId) + "?" + string.Join('&', query), null, cancellationToken, storeFrontCode: storeFrontCode);
            if (!response.IsSuccess)
            {
                if (response.Error?.HttpStatus is 400 or 404) break;
                return (AdapterResult<RemoteOrder>.Failure(response.Error!, response.RateLimit), firstPageIssue);
            }
            rateLimit = response.RateLimit;
            try
            {
                var page = TrendyolJsonMapper.Orders(response.Value!);
                firstPageIssue ??= page.Issues?.FirstOrDefault();
                packages.AddRange(page.Items.Where(item => string.Equals(item.ExternalOrderId, normalizedOrderNumber, StringComparison.OrdinalIgnoreCase) || string.Equals(item.OrderNumber, normalizedOrderNumber, StringComparison.OrdinalIgnoreCase)));
                nextCursor = page.HasMore && !string.IsNullOrWhiteSpace(page.NextCursor) ? page.NextCursor : null;
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Trendyol order stream failed contract mapping during direct lookup for {ExternalOrderId}.", normalizedOrderNumber);
                return (AdapterResult<RemoteOrder>.Failure(TrendyolErrorMapper.Contract()), firstPageIssue);
            }
            if (nextCursor is not null && !seenCursors.Add(nextCursor))
            {
                logger.LogWarning("Trendyol order {ExternalOrderId} stream lookup stopped: a cursor came back after {Pages} pages.", normalizedOrderNumber, pages);
                return (AdapterResult<RemoteOrder>.Failure(OrderScanIncomplete, rateLimit), firstPageIssue);
            }
        } while (nextCursor is not null && !cancellationToken.IsCancellationRequested);
        if (TrendyolJsonMapper.MergeOrderPackages(packages, normalizedOrderNumber) is { } streamed)
            return (AdapterResult<RemoteOrder>.Success(streamed with { StoreFrontCode = storeFrontCode }, rateLimit), firstPageIssue);
        return (null, firstPageIssue);
    }

    // Order V2 takes up to 50 package IDs per request. The group shares one storefront and one
    // date window (TrendyolLookupPolicy.PackageBatches); packages Trendyol does not return are
    // left out, and a request Trendyol rejects (400) is reported so the caller can read one by one.
    public async Task<AdapterResult<IReadOnlyList<RemoteOrder>>> GetShipmentPackagesAsync(AdapterContext context, IReadOnlyList<string> externalPackageIds, OrderLookupHint? hint, CancellationToken cancellationToken)
    {
        var ids = externalPackageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return AdapterResult<IReadOnlyList<RemoteOrder>>.Success([]);
        if (ids.Count > TrendyolLookupPolicy.PackageBatchSize) return AdapterResult<IReadOnlyList<RemoteOrder>>.Failure(TrendyolErrorMapper.Contract());
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<IReadOnlyList<RemoteOrder>>.Failure(TrendyolErrorMapper.Configuration());
        if (TrendyolLookupPolicy.V2Window(hint, timeProvider.GetUtcNow()) is not { } window) return AdapterResult<IReadOnlyList<RemoteOrder>>.Success([]);

        var missing = new HashSet<string>(ids, StringComparer.Ordinal);
        var found = new List<RemoteOrder>();
        RateLimitMetadata? rateLimit = null;
        AdapterError? rejected = null;
        foreach (var storeFrontCode in await LookupStorefrontsAsync(authorized, hint, cancellationToken))
        {
            var query = $"?startDate={window.Start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&endDate={window.End.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&shipmentPackageIds={Uri.EscapeDataString(string.Join(',', missing))}&page=0&size=200";
            var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.Orders(authorized.Connection.ExternalStoreId) + query, null, cancellationToken, storeFrontCode: storeFrontCode);
            if (!response.IsSuccess)
            {
                if (response.Error?.HttpStatus is 400 or 404) { rejected ??= response.Error; continue; }
                return AdapterResult<IReadOnlyList<RemoteOrder>>.Failure(response.Error!, response.RateLimit);
            }
            rateLimit = response.RateLimit;
            try
            {
                foreach (var order in TrendyolJsonMapper.Orders(response.Value!).Items)
                {
                    var packages = order.Packages.Where(package => missing.Contains(package.ExternalPackageId)).ToList();
                    if (packages.Count == 0) continue;
                    found.Add(order with { Packages = packages, StoreFrontCode = storeFrontCode });
                    foreach (var package in packages) missing.Remove(package.ExternalPackageId);
                }
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Trendyol package batch response failed contract mapping.");
                return AdapterResult<IReadOnlyList<RemoteOrder>>.Failure(TrendyolErrorMapper.Contract());
            }
            if (missing.Count == 0) break;
        }
        return found.Count == 0 && rejected is { HttpStatus: 400 }
            ? AdapterResult<IReadOnlyList<RemoteOrder>>.Failure(rejected, rateLimit)
            : AdapterResult<IReadOnlyList<RemoteOrder>>.Success(found, rateLimit);
    }

    public async Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken)
        => await ReadShipmentPackageAsync(context, externalPackageId, new PackageLookup(null), cancellationToken);

    public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, DateTimeOffset? packageStatusOccurredAt, CancellationToken cancellationToken)
        => ReadShipmentPackageAsync(context, externalPackageId, new PackageLookup(packageStatusOccurredAt), cancellationToken);

    public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, PackageLookup lookup, CancellationToken cancellationToken)
        => ReadShipmentPackageAsync(context, externalPackageId, lookup, cancellationToken);

    // Order V2 and the stream serve the same package record, and Trendyol fills invoiceLink only
    // once a package is invoiced. For an invoice read (lookup.InvoiceOnly) a package V2 found
    // without invoice fields has no invoice yet; reading the stream as well would only repeat
    // that answer page by page.
    private async Task<AdapterResult<RemoteOrderPackage>> ReadShipmentPackageAsync(AdapterContext context, string externalPackageId, PackageLookup lookup, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalPackageId)) return AdapterResult<RemoteOrderPackage>.Failure(TrendyolErrorMapper.Contract());
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<RemoteOrderPackage>.Failure(TrendyolErrorMapper.Configuration());
        return await ReadShipmentPackageAsync(authorized, externalPackageId, lookup, cancellationToken);
    }

    private async Task<AdapterResult<RemoteOrderPackage>> ReadShipmentPackageAsync(TrendyolRequestContext authorized, string externalPackageId, PackageLookup lookup, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalPackageId)) return AdapterResult<RemoteOrderPackage>.Failure(TrendyolErrorMapper.Contract());
        var packageStatusOccurredAt = lookup.StatusOccurredAt;
        var stopAtV2Match = lookup.InvoiceOnly;
        var storefronts = await LookupStorefrontsAsync(authorized, lookup.Order, cancellationToken);

        // Order V2 keeps a month of packages and accepts at most two weeks per request. A package
        // of an order older than a month is read from the stream below, around its status time.
        var v2Window = TrendyolLookupPolicy.V2Window(lookup.Order, timeProvider.GetUtcNow());
        var query = v2Window is { } window
            ? $"?startDate={window.Start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&endDate={window.End.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&shipmentPackageIds={Uri.EscapeDataString(externalPackageId.Trim())}&size=200"
            : null;
        AdapterResult<string>? v2Response = null;
        AdapterError? v2LookupError = null;
        RemoteOrderPackage? v2Package = null;
        foreach (var storeFrontCode in query is null ? Array.Empty<string>() : storefronts)
        {
            var response = await SendAsync(
                authorized,
                HttpMethod.Get,
                TrendyolEndpoints.Orders(authorized.Connection.ExternalStoreId) + query,
                null,
                cancellationToken,
                storeFrontCode: storeFrontCode);
            if (!response.IsSuccess)
            {
                if (response.Error?.HttpStatus is 400 or 404)
                {
                    v2LookupError = response.Error;
                    continue;
                }
                return AdapterResult<RemoteOrderPackage>.Failure(response.Error!, response.RateLimit);
            }

            v2Response = response;
            try
            {
                v2Package = TrendyolJsonMapper.ShipmentPackage(response.Value!, externalPackageId.Trim());
            }
            catch (JsonException)
            {
                return AdapterResult<RemoteOrderPackage>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit);
            }

            // Micro-export packages belong to their destination storefront.
            // Keep searching after an empty TR response, but stop as soon as
            // the exact package ID is found under one of the supported codes.
            if (v2Package is not null)
            {
                // The storefront it was found under is its order's, as for an order read (GetAsync).
                if (v2Package.OrderSnapshot is { } snapshot) v2Package = v2Package with { OrderSnapshot = snapshot with { StoreFrontCode = storeFrontCode } };
                break;
            }
        }
        var v2RateLimit = v2Response?.RateLimit;
        var v2ReadError = v2Response?.IsSuccess == true ? null : v2LookupError;
        if (stopAtV2Match && v2Package is not null)
            return AdapterResult<RemoteOrderPackage>.Success(v2Package, v2RateLimit);

        if ((!string.IsNullOrWhiteSpace(v2Package?.Package.CargoProviderExternalId)
                || !string.IsNullOrWhiteSpace(v2Package?.Package.CargoTrackingNumber))
            && HasInvoiceObservation(v2Package))
        {
            logger.LogInformation("Trendyol package read returned provider={ProviderPresent}, tracking={TrackingPresent}, invoice={InvoicePresent} from Order V2.",
                !string.IsNullOrWhiteSpace(v2Package!.Package.CargoProviderExternalId),
                !string.IsNullOrWhiteSpace(v2Package!.Package.CargoTrackingNumber),
                HasInvoiceObservation(v2Package));
            return AdapterResult<RemoteOrderPackage>.Success(v2Package!, v2RateLimit);
        }

        var directPackage = v2Package;
        if ((!string.IsNullOrWhiteSpace(directPackage?.Package.CargoProviderExternalId)
                || !string.IsNullOrWhiteSpace(directPackage?.Package.CargoTrackingNumber))
            && HasInvoiceObservation(directPackage))
        {
            logger.LogInformation("Trendyol package read returned provider={ProviderPresent}, tracking={TrackingPresent}, invoice={InvoicePresent} from targeted reads.",
                !string.IsNullOrWhiteSpace(directPackage!.Package.CargoProviderExternalId),
                !string.IsNullOrWhiteSpace(directPackage!.Package.CargoTrackingNumber),
                HasInvoiceObservation(directPackage));
            return AdapterResult<RemoteOrderPackage>.Success(directPackage!, v2RateLimit);
        }

        AdapterError? streamError = null;
        if (packageStatusOccurredAt is { } statusOccurredAt)
        {
            var now = timeProvider.GetUtcNow();
            var historyCutoff = now.AddMonths(-3);
            if (statusOccurredAt >= historyCutoff && statusOccurredAt <= now)
            {
                var modifiedStart = statusOccurredAt.AddDays(-6);
                if (modifiedStart < historyCutoff) modifiedStart = historyCutoff;
                var modifiedEnd = statusOccurredAt.AddDays(6);
                if (modifiedEnd > now) modifiedEnd = now;
                var scan = await ScanPackageStreamAsync(authorized, externalPackageId.Trim(), v2Package, v2RateLimit, modifiedStart, modifiedEnd, storefronts[0], cancellationToken);
                if (scan.Result is { } found) return found;
                streamError = scan.StreamError;
            }
        }

        var selectedPackage = v2Package;
        if (HasPackageReadbackValue(selectedPackage))
        {
            logger.LogInformation("Trendyol package read returned provider={ProviderPresent}, tracking={TrackingPresent}, invoice={InvoicePresent} from {Source}.",
                !string.IsNullOrWhiteSpace(selectedPackage!.Package.CargoProviderExternalId),
                !string.IsNullOrWhiteSpace(selectedPackage!.Package.CargoTrackingNumber),
                HasInvoiceObservation(selectedPackage),
                "Order V2");
            return AdapterResult<RemoteOrderPackage>.Success(selectedPackage!, v2RateLimit);
        }

        logger.LogInformation("Trendyol package cargo read returned no cargo fields from V2 or historical stream; package match V2={V2PackageFound}.",
            v2Package is not null);
        if (v2Response?.IsSuccess == true && selectedPackage is not null)
            return AdapterResult<RemoteOrderPackage>.Success(selectedPackage, v2RateLimit);
        if (streamError is not null)
            return AdapterResult<RemoteOrderPackage>.Failure(streamError, v2RateLimit);

        var error = v2ReadError
            ?? new AdapterError(AdapterErrorClass.NotFound, "REMOTE_PACKAGE_NOT_FOUND", "Trendyol paket bilgisi bulunamadı.", 404, null, null);
        return error.Class == AdapterErrorClass.NotFound || error.HttpStatus == 404
            ? AdapterResult<RemoteOrderPackage>.Failure(new(AdapterErrorClass.NotFound, "REMOTE_PACKAGE_NOT_FOUND", "Trendyol paket bilgisi bulunamadı.", 404, null, error.RemoteRequestId), v2RateLimit)
            : AdapterResult<RemoteOrderPackage>.Failure(error, v2RateLimit);
    }

    // A stream page is 200 packages and pages are spaced 5 s apart; twenty pages (4,000 packages, about two
    // minutes) cover the 12-day window of a busy seller without letting one package lookup hold the order quota.
    internal const int MaxPackageStreamPages = 20;
    internal const string PackageScanIncompleteCode = "TRENDYOL_PACKAGE_SCAN_INCOMPLETE";
    private static readonly AdapterError PackageScanIncomplete = new(AdapterErrorClass.TransientNetwork, PackageScanIncompleteCode,
        "Trendyol paket akışı sayfa sınırına kadar okundu ama paket bulunamadı; okuma daha sonra yeniden denenecek.", 503, TimeSpan.FromMinutes(15), null);

    // The stream retains three months of history while the package endpoint is date-limited. Scan only the
    // window around the locally recorded status time, using cursor pagination; each page is parsed once.
    internal async Task<(AdapterResult<RemoteOrderPackage>? Result, AdapterError? StreamError)> ScanPackageStreamAsync(
        TrendyolRequestContext authorized, string externalPackageId, RemoteOrderPackage? v2Package, RateLimitMetadata? v2RateLimit,
        DateTimeOffset modifiedStart, DateTimeOffset modifiedEnd, string storefront, CancellationToken cancellationToken)
    {
        string? nextCursor = null;
        for (var page = 1; modifiedStart < modifiedEnd; page++)
        {
            if (page > MaxPackageStreamPages)
            {
                // G10: the window was not read to its end, so "not found" is unknown, not a 404. A retryable error keeps
                // readback and link revocation from being blocked for a package that lies beyond the cap.
                logger.LogWarning("Trendyol package {PackageId} was not found in {Pages} historical stream pages; the scan stopped.", externalPackageId, MaxPackageStreamPages);
                return (null, PackageScanIncomplete);
            }
            var streamQuery = new List<string>
            {
                "size=200",
                "lastModifiedStartDate=" + modifiedStart.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                "lastModifiedEndDate=" + modifiedEnd.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            };
            if (!string.IsNullOrWhiteSpace(nextCursor))
                streamQuery.Add("nextCursor=" + Uri.EscapeDataString(nextCursor));

            var streamResponse = await SendAsync(authorized, HttpMethod.Get,
                TrendyolEndpoints.OrderStream(authorized.Connection.ExternalStoreId) + "?" + string.Join('&', streamQuery),
                null, cancellationToken, storeFrontCode: storefront);
            if (!streamResponse.IsSuccess) return (null, streamResponse.Error);

            try
            {
                var mapped = TrendyolJsonMapper.Orders(streamResponse.Value!);
                var streamedPackage = TrendyolJsonMapper.ShipmentPackage(mapped, externalPackageId);
                var mergedStreamPackage = TrendyolJsonMapper.PreferShipmentPackageWithCargo(streamedPackage, v2Package);
                if (HasPackageReadbackValue(streamedPackage) && HasPackageReadbackValue(mergedStreamPackage))
                {
                    logger.LogInformation("Trendyol package read returned provider={ProviderPresent}, tracking={TrackingPresent}, invoice={InvoicePresent} from historical stream.",
                        !string.IsNullOrWhiteSpace(mergedStreamPackage!.Package.CargoProviderExternalId),
                        !string.IsNullOrWhiteSpace(mergedStreamPackage!.Package.CargoTrackingNumber),
                        HasInvoiceObservation(mergedStreamPackage));
                    return (AdapterResult<RemoteOrderPackage>.Success(mergedStreamPackage!, streamResponse.RateLimit ?? v2RateLimit), null);
                }

                if (!mapped.HasMore) return (null, null);
                if (string.IsNullOrWhiteSpace(mapped.NextCursor))
                    return (AdapterResult<RemoteOrderPackage>.Failure(TrendyolErrorMapper.Contract(), streamResponse.RateLimit), null);
                // The resilience handler spaces stream pages; no extra sleep is needed here.
                nextCursor = mapped.NextCursor;
            }
            catch (JsonException)
            {
                return (AdapterResult<RemoteOrderPackage>.Failure(TrendyolErrorMapper.Contract(), streamResponse.RateLimit), null);
            }
        }
        return (null, null);
    }

    // Whether the read brought invoice values (status, number or link); an authoritative "no invoice" without
    // values is not a readback value here (TrendyolJsonMapper.HasInvoiceObservation counts that too).
    private static bool HasInvoiceObservation(RemoteOrderPackage? package) => TrendyolJsonMapper.HasInvoiceValue(package?.Package.Invoice);

    private static bool HasPackageReadbackValue(RemoteOrderPackage? package) =>
        !string.IsNullOrWhiteSpace(package?.Package.CargoProviderExternalId)
        || !string.IsNullOrWhiteSpace(package?.Package.CargoTrackingNumber)
        || HasInvoiceObservation(package);

    public async Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<PackageActionResult>.Failure(TrendyolErrorMapper.Configuration());
        return await ExecutePackageActionAsync(authorized, context, command, cancellationToken);
    }

    internal async Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(TrendyolRequestContext authorized, AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
    {
        if (!CanWrite(authorized, context)) return AdapterResult<PackageActionResult>.Failure(TrendyolErrorMapper.WriteClosed());
        if (string.IsNullOrWhiteSpace(command.ExternalPackageId)) return AdapterResult<PackageActionResult>.Failure(TrendyolErrorMapper.Contract());
        var action = command.Action.Trim().ToUpperInvariant();
        var seller = authorized.Connection.ExternalStoreId;
        var endpoint = action switch
        {
            "PICKING" or "INVOICED" => TrendyolEndpoints.ShipmentPackage(seller, command.ExternalPackageId),
            "TRACKING_NUMBER" => TrendyolEndpoints.ShipmentTrackingDetails(seller, command.ExternalPackageId),
            "CANCEL_ITEMS" => TrendyolEndpoints.ShipmentUnsupplied(seller, command.ExternalPackageId),
            "SPLIT" => TrendyolEndpoints.ShipmentSplit(seller, command.ExternalPackageId),
            "MULTI_SPLIT" => TrendyolEndpoints.ShipmentMultiSplit(seller, command.ExternalPackageId),
            "CHANGE_CARGO_PROVIDER" => TrendyolEndpoints.ShipmentCargoProvider(seller, command.ExternalPackageId),
            "ALTERNATIVE_DELIVERY" => TrendyolEndpoints.ShipmentAlternativeDelivery(seller, command.ExternalPackageId),
            "MANUAL_DELIVER" => TrendyolEndpoints.ShipmentManualDeliver(seller, command.ExternalPackageId),
            "MANUAL_RETURN" => TrendyolEndpoints.ShipmentManualReturn(seller, command.ExternalPackageId),
            _ => ""
        };
        if (endpoint.Length == 0) return AdapterResult<PackageActionResult>.Failure(TrendyolErrorMapper.Unsupported("Paket aksiyonu resmî Trendyol sözleşmesinde tanımlı değil."));
        HttpContent? content = null;
        if (action == "CANCEL_ITEMS")
        {
            // C14: the unsupplied body is rebuilt from validated values only (lines of lineId/quantity, reasonId 500-506).
            if (!TrendyolPackageActionBodies.TryBuildUnsupplied(command.PayloadJson, out var unsupplied))
                return AdapterResult<PackageActionResult>.Failure(new(AdapterErrorClass.Validation, "TRENDYOL_CANCEL_ITEMS_INVALID", TrendyolPackageActionBodies.UnsuppliedInvalidMessage, 422, null, null));
            content = JsonContent.Create(unsupplied);
        }
        else if (action is not ("MANUAL_DELIVER" or "MANUAL_RETURN"))
        {
            JsonDocument payload; try { payload = JsonDocument.Parse(command.PayloadJson); } catch (JsonException) { return AdapterResult<PackageActionResult>.Failure(TrendyolErrorMapper.Contract()); }
            content = JsonContent.Create(payload.RootElement.Clone()); payload.Dispose();
        }
        var method = action is "SPLIT" or "MULTI_SPLIT" ? HttpMethod.Post : HttpMethod.Put;
        using (content)
        {
            // C2: package writes go to the package's own storefront (micro-export / DE / AE packages are not TR).
            var response = await SendAsync(authorized, method, endpoint, content, cancellationToken, storeFrontCode: WriteStoreFront(command.StoreFrontCode)); if (!response.IsSuccess) return AdapterResult<PackageActionResult>.Failure(response.Error!, response.RateLimit);
            return AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "ACCEPTED", response.Error?.RemoteRequestId), response.RateLimit);
        }
    }

    public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<IReadOnlyList<RemoteCargoCompany>>.Failure(new(AdapterErrorClass.NotSupported, "TRENDYOL_CARGO_COMPANY_LIST_UNSUPPORTED", "Bu bağlantı kargo firması listesini sağlamıyor.", 501, null, null)));

    public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<IReadOnlyList<RemotePackageableLine>>.Failure(new(AdapterErrorClass.NotSupported, "TRENDYOL_PACKAGEABLE_LINES_UNSUPPORTED", "Bu bağlantı Hepsiburada paketlenebilir kalem listesini sağlamıyor.", 501, null, null)));

    public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<CreateOrderPackageResult>.Failure(new(AdapterErrorClass.NotSupported, "TRENDYOL_PACKAGE_CREATE_UNSUPPORTED", "Bu bağlantı Hepsiburada paket oluşturma işlemini sağlamıyor.", 501, null, null)));

    public async Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<bool>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized, context)) return AdapterResult<bool>.Failure(TrendyolErrorMapper.WriteClosed());
        if (string.IsNullOrWhiteSpace(request.CargoTrackingNumber) || request.BoxQuantity < 1 || request.VolumetricHeight <= 0) return AdapterResult<bool>.Failure(TrendyolErrorMapper.Contract());
        var body = JsonContent.Create(new { format = "ZPL", boxQuantity = request.BoxQuantity, volumetricHeight = request.VolumetricHeight });
        var response = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.CommonLabel(authorized.Connection.ExternalStoreId, request.CargoTrackingNumber), body, cancellationToken);
        return response.IsSuccess ? AdapterResult<bool>.Success(true, response.RateLimit) : AdapterResult<bool>.Failure(response.Error!, response.RateLimit);
    }

    public async Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string requestedFormat = "ZPL")
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<CommonLabelDocument>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.CommonLabel(authorized.Connection.ExternalStoreId, cargoTrackingNumber), null, cancellationToken); if (!response.IsSuccess) return AdapterResult<CommonLabelDocument>.Failure(response.Error!, response.RateLimit);
        try
        {
            using var document = JsonDocument.Parse(response.Value!);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return AdapterResult<CommonLabelDocument>.Failure(TrendyolErrorMapper.Contract());
            var labels = data.EnumerateArray().Select(x => x.TryGetProperty("label", out var label) ? label.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var format = data.GetArrayLength() > 0 && data[0].TryGetProperty("format", out var formatValue) ? formatValue.GetString() : "ZPL";
            return labels.Length == 0 ? AdapterResult<CommonLabelDocument>.Failure(TrendyolErrorMapper.Contract()) : AdapterResult<CommonLabelDocument>.Success(new(cargoTrackingNumber, format ?? "ZPL", Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, labels))), response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<CommonLabelDocument>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Configuration());
        var endpoint = ClaimsEndpoint(authorized.Connection.ExternalStoreId, page.Limit, Page(page.Cursor), window);
        // The documented getClaims endpoint is global and does not define the
        // order-stream storeFrontCode header. Keep this read independent from
        // storefront selection so international claims are not hidden or timed out.
        // Production returns default to the Created workflow bucket when no
        // claimItemStatus is supplied. Read each documented status so the
        // local model also receives WaitingInAction (the seller's shipped tab).
        if (string.Equals(authorized.Connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase))
            return await PollClaimsByStatusAsync(authorized, window, page, cancellationToken);
        var response = await SendAsync(authorized, HttpMethod.Get, endpoint, null, cancellationToken, storeFrontCode: null);
        if (response.Error?.HttpStatus == (int)HttpStatusCode.NotFound && string.Equals(authorized.Connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase))
            return await PollClaimsByStatusAsync(authorized, window, page, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(TrendyolJsonMapper.Returns(response.Value!), response.RateLimit); } catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    async Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteReturnClaim>.Failure(TrendyolErrorMapper.Configuration());
        if (string.IsNullOrWhiteSpace(externalReturnId)) return AdapterResult<RemoteReturnClaim>.Failure(TrendyolErrorMapper.Contract());
        var endpoint = TrendyolEndpoints.Claims(authorized.Connection.ExternalStoreId) + "?claimIds=" + Uri.EscapeDataString(externalReturnId);
        var response = await SendAsync(authorized, HttpMethod.Get, endpoint, null, cancellationToken, storeFrontCode: null); if (!response.IsSuccess) return AdapterResult<RemoteReturnClaim>.Failure(response.Error!, response.RateLimit);
        try
        {
            var page = TrendyolJsonMapper.Returns(response.Value!);
            var claim = page.Items.FirstOrDefault(x => string.Equals(x.ExternalClaimId, externalReturnId, StringComparison.OrdinalIgnoreCase));
            return claim is null ? AdapterResult<RemoteReturnClaim>.Failure(new(AdapterErrorClass.NotFound, "REMOTE_RETURN_NOT_FOUND", "Platform iade kaydı bulunamadı.", 404, null, null), response.RateLimit) : AdapterResult<RemoteReturnClaim>.Success(claim, response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<RemoteReturnClaim>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Failure(TrendyolErrorMapper.Configuration());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ClaimIssueReasons, null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success(TrendyolJsonMapper.ReturnIssueReasons(response.Value!), response.RateLimit); }
        catch (JsonException) { return AdapterResult<IReadOnlyList<ReturnIssueReason>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Configuration());
        return await ExecuteReturnActionAsync(authorized, context, command, cancellationToken);
    }

    internal async Task<AdapterResult<ReturnActionResult>> ExecuteReturnActionAsync(TrendyolRequestContext authorized, AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
    {
        if (!CanWrite(authorized, context)) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.WriteClosed());
        if (string.IsNullOrWhiteSpace(command.ExternalClaimId) || command.ExternalLineItemIds.Count == 0) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Contract());
        var action = command.Action.Trim().ToUpperInvariant();
        if (action == "APPROVE")
        {
            var response = await SendAsync(authorized, HttpMethod.Put, TrendyolEndpoints.ApproveClaim(authorized.Connection.ExternalStoreId, command.ExternalClaimId), JsonContent.Create(new { claimLineItemIdList = command.ExternalLineItemIds, @params = new { } }), cancellationToken);
            return response.IsSuccess ? AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, "ACCEPTED", null), response.RateLimit) : AdapterResult<ReturnActionResult>.Failure(response.Error!, response.RateLimit);
        }
        if (action != "REJECT" || string.IsNullOrWhiteSpace(command.ReasonCode) || string.IsNullOrWhiteSpace(command.Explanation) || command.Explanation.Trim().Length > 500) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Contract());
        var evidenceOptional = command.ReasonCode is "1651" or "451" or "2101";
        if (!evidenceOptional && command.EvidenceFiles.Count == 0) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Unsupported("Bu ret nedeni için kanıt dosyası zorunludur."));
        // C11: createClaimIssue accepts evidence files of at most 5 MB each; a bigger file is refused here, not by Trendyol.
        if (command.EvidenceFiles.Any(file => file.Content.LongLength > MaxClaimEvidenceBytes))
            return AdapterResult<ReturnActionResult>.Failure(new(AdapterErrorClass.Validation, "TRENDYOL_RETURN_EVIDENCE_TOO_LARGE", "Trendyol iade kanıtı dosya başına en fazla 5 MB olabilir; daha küçük bir dosya yükleyin.", 422, null, null));
        using var multipart = new MultipartFormDataContent();
        foreach (var file in command.EvidenceFiles)
        {
            var part = new ByteArrayContent(file.Content); part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.MimeType); multipart.Add(part, "files", Path.GetFileName(file.FileName));
        }
        var endpoint = TrendyolEndpoints.RejectClaim(authorized.Connection.ExternalStoreId, command.ExternalClaimId, command.ReasonCode, command.ExternalLineItemIds, command.Explanation.Trim());
        var rejected = await SendAsync(authorized, HttpMethod.Post, endpoint, multipart, cancellationToken);
        return rejected.IsSuccess ? AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, "ISSUE_CREATED", null), rejected.RateLimit) : AdapterResult<ReturnActionResult>.Failure(rejected.Error!, rejected.RateLimit);
    }

    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Configuration());
        return await DeliverAsync(authorized, context, command, cancellationToken);
    }

    internal async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(TrendyolRequestContext authorized, AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var invoiceDeliveryWriteAllowed = IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(
            authorized.Connection,
            context,
            configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites"))
            || IntegrationRuntimePolicy.AllowsExplicitManualInvoiceMarketplaceDelivery(
                authorized.Connection,
                context,
                GlobalWritesEnabled,
                authorized.ExternalWritesEnabled);
        if (!CanWrite(authorized, context) || !invoiceDeliveryWriteAllowed) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.WriteClosed());
        if (!string.Equals(command.DeliveryType, "LINK", StringComparison.Ordinal)) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Unsupported("Yalnız resmî link delivery sözleşmesi doğrulandı; file delivery ayrı content akışı kanıtı bekliyor."));
        JsonDocument payload; try { payload = JsonDocument.Parse(command.PayloadJson); } catch (JsonException) { return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Contract()); }
        using (payload)
        {
            if (!payload.RootElement.TryGetProperty("invoiceLink", out var link) || link.ValueKind != JsonValueKind.String || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var invoiceUri) || invoiceUri.Scheme != "https"
                || !payload.RootElement.TryGetProperty("shipmentPackageId", out var packageId) || packageId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Unsupported("HTTPS invoice link ve shipmentPackageId doğrulanmadan delivery HTTP çağrısı yapılmaz."));

            var isMicro = payload.RootElement.TryGetProperty("micro", out var micro) && micro.ValueKind == JsonValueKind.True;
            if (isMicro)
            {
                if (!payload.RootElement.TryGetProperty("invoiceDateTime", out var invoiceDateTime) || !invoiceDateTime.TryGetInt64(out _)
                    || !payload.RootElement.TryGetProperty("invoiceNumber", out var invoiceNumber) || invoiceNumber.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(invoiceNumber.GetString()))
                    return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Unsupported("Mikro ihracat delivery payloadında invoiceDateTime ve invoiceNumber zorunludur."));
            }
            if (InvoiceLinkBody(payload.RootElement) is not { } body)
                return AdapterResult<InvoiceDeliveryResult>.Failure(new(AdapterErrorClass.Validation, InvoiceLinkBodyInvalidCode,
                    "Trendyol fatura linki gönderilmedi: paket numarası sayı değil veya mikro ihracat faturasının numarası GİB biçiminde (3 harf/rakam ve 13 rakam) değil.", null, null, null));
            var response = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.InvoiceLinks(authorized.Connection.ExternalStoreId), JsonContent.Create(body), cancellationToken, storeFrontCode: WriteStoreFront(command.StoreFrontCode)); if (!response.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(response.Error!, response.RateLimit);
            return AdapterResult<InvoiceDeliveryResult>.Success(new(command.ExternalPackageId, "SUBMITTED"), response.RateLimit);
        }
    }

    internal const string InvoiceLinkBodyInvalidCode = "TRENDYOL_INVOICE_LINK_BODY_INVALID";

    // The documented "send-invoice-link" body: shipmentPackageId as a number (int64), invoiceLink, and the optional
    // invoiceDateTime and invoiceNumber (GİB format, three letters/digits and thirteen digits). The panel's own fields
    // (micro, the other marketplaces' keys) are not sent. Null when the package number does not fit, or when a
    // micro-export invoice number is not in GİB format.
    internal static Dictionary<string, object>? InvoiceLinkBody(JsonElement payload)
    {
        var packageText = payload.TryGetProperty("shipmentPackageId", out var packageValue)
            ? packageValue.ValueKind == JsonValueKind.Number ? packageValue.GetRawText() : packageValue.ValueKind == JsonValueKind.String ? packageValue.GetString()?.Trim() : null
            : null;
        if (!long.TryParse(packageText, NumberStyles.None, CultureInfo.InvariantCulture, out var shipmentPackageId) || shipmentPackageId <= 0) return null;
        if (!payload.TryGetProperty("invoiceLink", out var link) || link.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(link.GetString())) return null;
        var body = new Dictionary<string, object> { ["shipmentPackageId"] = shipmentPackageId, ["invoiceLink"] = link.GetString()! };
        if (payload.TryGetProperty("invoiceDateTime", out var issuedAt) && issuedAt.TryGetInt64(out var issuedAtMilliseconds))
            body["invoiceDateTime"] = issuedAtMilliseconds;
        if (payload.TryGetProperty("invoiceNumber", out var number) && number.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(number.GetString()))
        {
            var invoiceNumber = number.GetString()!.Trim();
            // The number is optional for a domestic package: one Trendyol would refuse (a manual upload numbered
            // "2026/0045") is left out and the link is still sent. Micro-export requires a valid number.
            if (InvoiceIssuePolicy.IsGibInvoiceNumber(invoiceNumber)) body["invoiceNumber"] = invoiceNumber;
            else if (IsMicroExportPayload(payload)) return null;
        }
        return body;
    }

    private static bool IsMicroExportPayload(JsonElement payload) => payload.TryGetProperty("micro", out var micro) && micro.ValueKind == JsonValueKind.True;

    public async Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference.ExternalReference))
            return AdapterResult<InvoiceDeliveryStatus>.Failure(TrendyolErrorMapper.Contract());
        // With the order's date and storefront the package is read in its own window and storefront (one or two
        // requests); without them every storefront is tried in the last 14 days only.
        var package = await ReadShipmentPackageAsync(context, reference.ExternalReference, reference.Lookup ?? new PackageLookup(null), cancellationToken);
        if (!package.IsSuccess) return AdapterResult<InvoiceDeliveryStatus>.Failure(package.Error!, package.RateLimit);
        var invoice = package.Value?.Package.Invoice;
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.RawStatus))
            return AdapterResult<InvoiceDeliveryStatus>.Failure(new(AdapterErrorClass.ContractViolation,
                "TRENDYOL_INVOICE_STATUS_UNAVAILABLE", "Trendyol yanıtı paket için açık fatura durumu içermiyor; mali kesim güvenle başlatılamaz.", 502, null, null), package.RateLimit);
        return AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, invoice.RawStatus,
            MarketplaceInvoiceStatePolicy.FromRemote(invoice.RawStatus) != MarketplaceInvoiceStatus.Unknown,
            invoice.InvoiceNumber, invoice.InvoiceUrl, invoice.ReferencesAreAuthoritative), package.RateLimit);
    }

    // Removes the link a cancelled invoice left on a package ("Fatura Linki Silme"). Only on the operator's request
    // (an explicit manual invoice write through the same gates as delivery), and only when the package is read
    // first and holds this invoice's link or number; the package is read again to prove the link is gone.
    public async Task<AdapterResult<InvoiceLinkRevocationResult>> RevokeLinkAsync(AdapterContext context, InvoiceLinkRevocationCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<InvoiceLinkRevocationResult>.Failure(TrendyolErrorMapper.Configuration());
        return await RevokeLinkAsync(authorized, context, command, cancellationToken);
    }

    internal async Task<AdapterResult<InvoiceLinkRevocationResult>> RevokeLinkAsync(TrendyolRequestContext authorized, AdapterContext context, InvoiceLinkRevocationCommand command, CancellationToken cancellationToken)
    {
        if (!CanWrite(authorized, context)
            || !IntegrationRuntimePolicy.AllowsExplicitManualInvoiceMarketplaceDelivery(authorized.Connection, context, GlobalWritesEnabled, authorized.ExternalWritesEnabled))
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(TrendyolErrorMapper.WriteClosed());
        var packageId = command.Reference.ExternalReference?.Trim();
        if (string.IsNullOrWhiteSpace(packageId) || !long.TryParse(packageId, NumberStyles.None, CultureInfo.InvariantCulture, out var shipmentPackageId))
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.Validation, "TRENDYOL_SHIPMENT_PACKAGE_ID_INVALID",
                "Trendyol paket numarası sayısal olmalıdır; silme isteği gönderilmedi.", null, null, null));
        var lookup = (command.Reference.Lookup ?? new PackageLookup(null)) with { InvoiceOnly = true };

        var before = await ReadShipmentPackageAsync(authorized, packageId, lookup, cancellationToken);
        if (!before.IsSuccess) return AdapterResult<InvoiceLinkRevocationResult>.Failure(before.Error!, before.RateLimit);
        var current = LinkPresence(before.Value!);
        if (current.Presence == InvoiceLinkPresence.Absent)
            return AdapterResult<InvoiceLinkRevocationResult>.Success(new(packageId, true, current.RawStatus), before.RateLimit);
        if (current.Presence == InvoiceLinkPresence.Unknown)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.ContractViolation, "TRENDYOL_INVOICE_STATUS_UNAVAILABLE",
                "Trendyol paketinin fatura durumu okunamadı; silme isteği gönderilmedi.", 502, null, null), before.RateLimit);
        if (!HoldsThisInvoice(before.Value!.Package.Invoice, command))
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.BusinessConflict, MarketplaceInvoiceLinkRevocationPolicy.NotOursCode,
                "Trendyol paketindeki fatura bu faturaya ait görünmüyor; başka bir faturanın linki silinmesin diye istek gönderilmedi.", 409, null, null), before.RateLimit);
        if (CustomerId(before.Value!) is not { } customerId)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.ContractViolation, "TRENDYOL_CUSTOMER_ID_MISSING",
                "Trendyol paketi müşteri numarasını (customerId) içermiyor; silme isteği gönderilmedi.", 502, null, null), before.RateLimit);

        var deleted = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.InvoiceLinkDelete(authorized.Connection.ExternalStoreId),
            JsonContent.Create(new { serviceSourceId = shipmentPackageId, channelId = 1, customerId }), cancellationToken, storeFrontCode: WriteStoreFront(command.Reference.Lookup?.Order?.StoreFrontCode));
        // 404 is "no invoice link found": the read below decides whether the package is clear.
        if (!deleted.IsSuccess && deleted.Error!.HttpStatus != 404)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(deleted.Error, deleted.RateLimit);

        var after = await ReadShipmentPackageAsync(authorized, packageId, lookup, cancellationToken);
        if (!after.IsSuccess)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(after.Error!.Class, MarketplaceInvoiceLinkRevocationPolicy.ReadbackPendingCode,
                "Trendyol silme isteği gönderildi ancak paket yeniden okunamadı. Birkaç dakika sonra tekrar deneyin; panel önce paketi okur.", after.Error.HttpStatus, after.Error.RetryAfter, after.Error.RemoteRequestId), after.RateLimit ?? deleted.RateLimit);
        var removed = LinkPresence(after.Value!);
        if (removed.Presence == InvoiceLinkPresence.Absent)
            return AdapterResult<InvoiceLinkRevocationResult>.Success(new(packageId, false, removed.RawStatus), after.RateLimit ?? deleted.RateLimit);
        if (!deleted.IsSuccess)
            return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.NotFound, "TRENDYOL_INVOICE_LINK_NOT_FOUND",
                "Trendyol bu pakette silinecek bir fatura linki bulamadı ama paket faturalı görünüyor. Faturayı Trendyol Satıcı Paneli’nden kaldırın.", 404, null, deleted.Error!.RemoteRequestId), after.RateLimit ?? deleted.RateLimit);
        return AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.BusinessConflict, MarketplaceInvoiceLinkRevocationPolicy.ReadbackPendingCode,
            "Trendyol silme isteğini kabul etti ancak paket hâlâ faturalı görünüyor. Birkaç dakika sonra tekrar deneyin; panel önce paketi okur.", 409, TimeSpan.FromMinutes(2), null), after.RateLimit ?? deleted.RateLimit);
    }

    internal enum InvoiceLinkPresence { Present, Absent, Unknown }

    // invoiceStatus decides; without it, an observation that carries both reference fields empty means no invoice.
    internal static (InvoiceLinkPresence Presence, string RawStatus) LinkPresence(RemoteOrderPackage package)
    {
        var invoice = package.Package.Invoice;
        var raw = invoice?.RawStatus?.Trim();
        switch (MarketplaceInvoiceStatePolicy.FromRemote(raw))
        {
            case MarketplaceInvoiceStatus.NotInvoiced: return (InvoiceLinkPresence.Absent, raw!);
            case MarketplaceInvoiceStatus.Invoiced or MarketplaceInvoiceStatus.Received or MarketplaceInvoiceStatus.Rejected: return (InvoiceLinkPresence.Present, raw!);
        }
        if (!string.IsNullOrWhiteSpace(invoice?.InvoiceUrl)) return (InvoiceLinkPresence.Present, "LINK_PRESENT");
        return invoice is { ReferencesAreAuthoritative: true } && string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? (InvoiceLinkPresence.Absent, "NO_INVOICE_REFERENCES")
            : (InvoiceLinkPresence.Unknown, raw ?? "UNKNOWN");
    }

    internal static bool HoldsThisInvoice(RemotePackageInvoiceObservation? observed, InvoiceLinkRevocationCommand command)
    {
        var url = observed?.InvoiceUrl?.Trim().TrimEnd('/');
        var number = observed?.InvoiceNumber?.Trim();
        var urlMatches = !string.IsNullOrWhiteSpace(url)
            && command.ExpectedInvoiceLinks.Any(link => string.Equals(link.Trim().TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase));
        var numberMatches = !string.IsNullOrWhiteSpace(number) && !string.IsNullOrWhiteSpace(command.ExpectedInvoiceNumber)
            && string.Equals(number, command.ExpectedInvoiceNumber.Trim(), StringComparison.Ordinal);
        // A link or a number that names another invoice wins over a partial match: a package with the same number but
        // another link (or the same link but another number) holds a live invoice that is not this one.
        if (!string.IsNullOrWhiteSpace(url) && !urlMatches) return false;
        if (!string.IsNullOrWhiteSpace(number) && !string.IsNullOrWhiteSpace(command.ExpectedInvoiceNumber) && !numberMatches) return false;
        return urlMatches || numberMatches;
    }

    // Trendyol's package (Order V2 and the stream) carries the customer number the delete request needs.
    internal static long? CustomerId(RemoteOrderPackage package)
    {
        foreach (var json in new[] { package.OrderSnapshot?.RawJson, package.OrderSnapshot?.CustomerSnapshotJson })
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("customerId", out var value)) continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0) return number;
                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) return parsed;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private async Task<AdapterResult<RemoteOperationRef>> SubmitBatchAsync(AdapterContext context, HttpMethod method, Func<string, string> endpointFactory, string payloadJson, string kind, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized, context)) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.WriteClosed());
        JsonDocument payload; try { payload = JsonDocument.Parse(payloadJson); } catch (JsonException) { return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract()); }
        using (payload)
        {
            var response = await SendAsync(authorized, method, endpointFactory(authorized.Connection.ExternalStoreId), JsonContent.Create(payload.RootElement), cancellationToken); if (!response.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(response.Error!, response.RateLimit);
            try
            {
                if (!TrendyolContractGuard.HasBatchRequestId(response.Value!)) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract());
                using var document = JsonDocument.Parse(response.Value!);
                var id = document.RootElement.GetProperty("batchRequestId").GetString()!;
                return AdapterResult<RemoteOperationRef>.Success(new(id, kind, timeProvider.GetUtcNow()), response.RateLimit);
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract());
            }
        }
    }

    private bool CanWrite(TrendyolRequestContext context, AdapterContext adapterContext) =>
        IntegrationRuntimePolicy.AllowsExternalWrite(context.Connection, adapterContext, GlobalWritesEnabled, context.ExternalWritesEnabled);
    private static CapabilityEvidence SupportedEvidence(string code, ConnectionIdentity identity, string sourceUrl, string note, DateTimeOffset verifiedAt) =>
        new(code, "SUPPORTED", "V2", identity.Environment, identity.ExternalStoreId, sourceUrl, "2026-08-04", null, null, note, null, verifiedAt);

    private static CapabilityEvidence ReadProbeEvidence<T>(string code, ConnectionIdentity identity, string sourceUrl, AdapterResult<T> probe, string label, DateTimeOffset verifiedAt) =>
        probe.IsSuccess
            ? SupportedEvidence(code, identity, sourceUrl, $"{label} salt-okunur probu başarılı yanıt aldı.", verifiedAt)
            : new(code, "UNKNOWN", "V2", identity.Environment, identity.ExternalStoreId, sourceUrl, "2026-08-04", null, null, $"{label} salt-okunur probu kanıt üretmedi: {probe.Error?.Code ?? "UNKNOWN_ERROR"}.", null, verifiedAt);

    private void LogOrderResponseShape(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var rootProperties = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray() : [];
            var packages = root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content : default;
            var first = packages.ValueKind == JsonValueKind.Array && packages.GetArrayLength() > 0 ? packages[0] : default;
            var packageProperties = first.ValueKind == JsonValueKind.Object ? first.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray() : [];
            var lineCounts = packageProperties.Where(x => first.TryGetProperty(x, out var value) && value.ValueKind == JsonValueKind.Array)
                .Select(x => new { Name = x, Count = first.GetProperty(x).GetArrayLength() }).ToArray();
            logger.LogInformation("Trendyol order response schema: RootProperties={RootProperties}; PackageCount={PackageCount}; FirstPackageProperties={FirstPackageProperties}; ArrayFields={ArrayFields}", rootProperties, packages.ValueKind == JsonValueKind.Array ? packages.GetArrayLength() : 0, packageProperties, lineCounts);
        }
        catch (JsonException)
        {
            logger.LogWarning("Trendyol order response is not valid JSON while recording its schema.");
        }
    }
    private static int Page(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 0 ? page : 0;
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

    internal static readonly string[] ClaimStatuses = ["Created", "WaitingInAction", "WaitingFraudCheck", "Accepted", "Cancelled", "Rejected", "Unresolved", "InAnalysis"];

    internal static string ClaimsEndpoint(string sellerId, int limit, int pageNumber, ReturnPollWindow window, string? claimItemStatus = null)
    {
        var query = new List<string> { $"size={Math.Clamp(limit, 1, 200).ToString(CultureInfo.InvariantCulture)}", $"page={pageNumber.ToString(CultureInfo.InvariantCulture)}" };
        if (window.ModifiedAfter is { } after) query.Add("startDate=" + after.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        if (window.ModifiedBefore is { } before) query.Add("endDate=" + before.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        if (claimItemStatus is not null) query.Add("claimItemStatus=" + claimItemStatus);
        return TrendyolEndpoints.Claims(sellerId) + "?" + string.Join('&', query);
    }

    // Each status bucket pages on its own: the cursor "s:Created=2,Accepted=5" names the next page of every
    // bucket that still has claims, so an exhausted bucket is not asked again for empty pages. A plain page
    // number (or no cursor) reads every bucket at that page.
    internal static IReadOnlyList<(string Status, int Page)> ClaimBucketPages(string? cursor)
    {
        if (cursor is not null && cursor.StartsWith("s:", StringComparison.Ordinal))
        {
            var pages = new List<(string Status, int Page)>();
            foreach (var entry in cursor[2..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split('=');
                if (parts.Length == 2 && ClaimStatuses.Contains(parts[0], StringComparer.Ordinal) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    pages.Add((parts[0], number));
            }
            if (pages.Count > 0) return pages;
        }
        var shared = Page(cursor);
        return ClaimStatuses.Select(status => (status, shared)).ToArray();
    }

    internal static string? ClaimBucketCursor(IReadOnlyList<(string Status, int Page)> nextPages) =>
        nextPages.Count == 0 ? null : "s:" + string.Join(',', nextPages.Select(next => $"{next.Status}={next.Page.ToString(CultureInfo.InvariantCulture)}"));

    private async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollClaimsByStatusAsync(TrendyolRequestContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        logger.LogInformation("Trendyol claims are read with the documented claimItemStatus buckets so all seller return tabs are synchronized.");
        var buckets = ClaimBucketPages(page.Cursor);
        var claims = new List<RemoteReturnClaim>();
        var nextPages = new List<(string Status, int Page)>();
        RateLimitMetadata? rateLimit = null;
        var responses = await Task.WhenAll(buckets.Select(bucket =>
            SendAsync(context, HttpMethod.Get, ClaimsEndpoint(context.Connection.ExternalStoreId, page.Limit, bucket.Page, window, bucket.Status), null, cancellationToken, storeFrontCode: null)));
        for (var index = 0; index < responses.Length; index++)
        {
            var response = responses[index];
            var bucket = buckets[index];
            rateLimit = response.RateLimit ?? rateLimit;
            if (response.Error?.HttpStatus == (int)HttpStatusCode.NotFound) continue;
            if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(response.Error!, response.RateLimit);
            try
            {
                var result = TrendyolJsonMapper.Returns(response.Value!);
                logger.LogInformation("Trendyol claims bucket {Status} page {Page} returned {Count} claims.", bucket.Status, bucket.Page, result.Items.Count);
                claims.AddRange(result.Items);
                if (result.HasMore) nextPages.Add((bucket.Status, bucket.Page + 1));
            }
            catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit); }
        }
        var uniqueClaims = claims.GroupBy(x => x.ExternalClaimId, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var primary = group.OrderByDescending(x => x.LastModifiedAt).First();
            var cargo = group.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CargoTrackingNumber) || !string.IsNullOrWhiteSpace(x.CargoProviderName));
            var mergedLines = group.SelectMany(x => x.Lines)
                .GroupBy(x => x.ExternalLineId, StringComparer.Ordinal)
                .Select(lines => lines.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.RawStatus)) ?? lines.First())
                .ToList();
            return primary with
            {
                Lines = mergedLines,
                CargoProviderName = primary.CargoProviderName ?? cargo?.CargoProviderName,
                CargoTrackingNumber = primary.CargoTrackingNumber ?? cargo?.CargoTrackingNumber,
                CargoTrackingLink = primary.CargoTrackingLink ?? cargo?.CargoTrackingLink
            };
        }).OrderByDescending(x => x.LastModifiedAt).ToArray();
        logger.LogInformation("Trendyol claims status buckets merged to {Count} unique claims: {Statuses}.", uniqueClaims.Length, string.Join(",", uniqueClaims.GroupBy(x => x.RawStatus).Select(x => $"{x.Key}:{x.Count()}")));
        var nextCursor = ClaimBucketCursor(nextPages);
        return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new(uniqueClaims, nextCursor, nextCursor is not null), rateLimit);
    }

    public async Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (authorized is null) return AdapterResult<StageTestOrderResult>.Failure(TrendyolErrorMapper.Configuration());
        if (!context.IsStageCapabilityProbe || !string.Equals(authorized.Connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || !string.Equals(authorized.Connection.ExternalStoreId, "2738", StringComparison.Ordinal)) return AdapterResult<StageTestOrderResult>.Failure(TrendyolErrorMapper.WriteClosed());
        if (string.IsNullOrWhiteSpace(barcode)) return AdapterResult<StageTestOrderResult>.Failure(TrendyolErrorMapper.Contract());
        var address = new { addressText = "Stage test address", city = "Istanbul", company = "", district = "Kadikoy", email = "stage.fixture@example.invalid", latitude = "", longitude = "", neighborhood = "Test", phone = "5301234567", postalCode = "34710" };
        var body = JsonContent.Create(new { customer = new { customerFirstName = "Stage", customerLastName = "Fixture" }, invoiceAddress = new { address.addressText, address.city, address.company, address.district, address.email, invoiceFirstName = "Stage", invoiceLastName = "Fixture", address.latitude, address.longitude, address.neighborhood, address.phone, address.postalCode, invoiceTaxNumber = "", invoiceTaxOffice = "" }, lines = new[] { new { barcode, quantity = 1, discountPercentage = 50, trendyolDiscountPercentage = 30 } }, seller = new { sellerId = 2738 }, shippingAddress = new { address.addressText, address.city, address.company, address.district, address.email, address.latitude, address.longitude, address.neighborhood, address.phone, address.postalCode, shippingFirstName = "Stage", shippingLastName = "Fixture" }, commercial = false });
        var response = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.StageTestOrder, body, cancellationToken, configure: request => request.Headers.TryAddWithoutValidation("sellerId", authorized.Connection.ExternalStoreId));
        if (!response.IsSuccess) return AdapterResult<StageTestOrderResult>.Failure(response.Error!, response.RateLimit);
        try { using var document = JsonDocument.Parse(response.Value!); var orderNumber = document.RootElement.TryGetProperty("orderNumber", out var value) ? value.ToString() : null; return string.IsNullOrWhiteSpace(orderNumber) ? AdapterResult<StageTestOrderResult>.Failure(TrendyolErrorMapper.Contract()) : AdapterResult<StageTestOrderResult>.Success(new(orderNumber), response.RateLimit); }
        catch (JsonException) { return AdapterResult<StageTestOrderResult>.Failure(TrendyolErrorMapper.Contract()); }
    }

    private async Task<AdapterResult<string>> SendAsync(TrendyolRequestContext context, HttpMethod method, string endpoint, HttpContent? content, CancellationToken cancellationToken, bool includeStoreFrontCode = true, Action<HttpRequestMessage>? configure = null, string? storeFrontCode = "TR")
    {
        using var request = TrendyolAuthenticationHandler.Create(context, method, endpoint, content, includeStoreFrontCode, storeFrontCode); configure?.Invoke(request); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The request timeout measures the exchange with Trendyol only. Waiting for the seller quota
        // (order reads are spaced 5 s apart and shared by every job) used to count towards it, so
        // queued reads were cancelled as REMOTE_TIMEOUT before they were ever sent. The timer starts
        // when the resilience handler releases the request; a long cap still bounds the quota wait.
        var instantLimits = TrendyolQuotaLane.Limits;
        var timeout = instantLimits?.RequestTimeout ?? RequestTimeout(trendyolOptions.Value.Timeout);
        linked.CancelAfter(instantLimits is null ? timeout + MaximumQuotaWait : instantLimits.MaximumQuotaWait);
        var quotaAcquired = false;
        request.Options.Set(TrendyolResilienceHandler.QuotaAcquiredKey, () => { quotaAcquired = true; linked.CancelAfter(timeout); });
        CallerCancellation.Mark(request, cancellationToken);
        try
        {
            using var response = await clients.CreateClient("Trendyol").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token); var retryAfter = RetryAfterDelay(response.Headers.RetryAfter, timeProvider.GetUtcNow()); var rate = new RateLimitMetadata(null, response.Headers.RetryAfter?.Date, retryAfter); var remoteRequestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                var vendorCode = TrendyolErrorMapper.SafeVendorCode(await response.Content.ReadBoundedStringAsync(linked.Token));
                return AdapterResult<string>.Failure(TrendyolErrorMapper.FromStatus(response.StatusCode, retryAfter, remoteRequestId, vendorCode), rate);
            }
            return AdapterResult<string>.Success(await response.Content.ReadBoundedStringAsync(linked.Token), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An instant action that never got a quota token was not sent; it is queued instead.
            if (instantLimits is not null && !quotaAcquired)
                return AdapterResult<string>.Failure(new(AdapterErrorClass.RateLimit, "LOCAL_QUOTA_BUSY", "Pazaryeri istek kotası şu an dolu.", 429, TimeSpan.FromSeconds(10), null));
            logger.LogWarning("Trendyol {Method} {Path} (storefront {StoreFront}) timed out after the quota wait", method.Method, request.RequestUri?.AbsolutePath, includeStoreFrontCode ? storeFrontCode : "none");
            return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_TIMEOUT", "Platform isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null));
        }
        catch (HttpRequestException) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_NETWORK_ERROR", "Platform ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }

    private static readonly TimeSpan MaximumQuotaWait = TimeSpan.FromMinutes(10);

    // Trendyol documents 5 MB per evidence file; the decimal megabyte is the safe reading.
    internal const long MaxClaimEvidenceBytes = 5_000_000;

    // A known order is read from its own storefront; an unknown one from the documented storefronts and any
    // undocumented one the connection already has orders from (C28).
    private async Task<IReadOnlyList<string>> LookupStorefrontsAsync(TrendyolRequestContext authorized, OrderLookupHint? hint, CancellationToken cancellationToken) =>
        hint?.StoreFrontCode is { Length: > 0 }
            ? TrendyolLookupPolicy.Storefronts(hint, [])
            : await authentication.ReadStorefrontsAsync(authorized.Connection.TenantId, authorized.Connection.Id, cancellationToken);

    internal static AdapterPageResult<RemoteCatalogProduct> FilterByContentId(AdapterPageResult<RemoteCatalogProduct> page, string contentId) =>
        page with { Items = [.. page.Items.Where(product => string.Equals(product.ExternalProductId, contentId.Trim(), StringComparison.Ordinal))] };

    // The stored storefront of the package's order; TR only when it is not known (older rows, non-Trendyol callers).
    internal static string WriteStoreFront(string? storeFrontCode) =>
        string.IsNullOrWhiteSpace(storeFrontCode) ? TrendyolLookupPolicy.HomeStoreFront : storeFrontCode.Trim().ToUpperInvariant();

    // Retry-After as seconds or as an HTTP date; a date in the past means no wait.
    internal static TimeSpan? RetryAfterDelay(System.Net.Http.Headers.RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header?.Delta is { } delta) return delta > TimeSpan.Zero ? delta : null;
        if (header?.Date is { } date && date - now is var wait && wait > TimeSpan.Zero) return wait;
        return null;
    }

    private static TimeSpan RequestTimeout(TimeSpan configured) => configured > TimeSpan.Zero
        ? TimeSpan.FromSeconds(Math.Clamp(configured.TotalSeconds, 5, 300))
        : TimeSpan.FromSeconds(30);
}
