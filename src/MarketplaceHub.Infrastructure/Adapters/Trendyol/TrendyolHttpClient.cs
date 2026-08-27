using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolHttpClient(IHttpClientFactory clients, TrendyolAuthenticationHandler authentication, IConfiguration configuration, IOptions<TrendyolOptions> trendyolOptions, TimeProvider timeProvider, ILogger<TrendyolHttpClient> logger)
    : IConnectionPort, IReferenceDataPort, IProductPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IReturnPort, IInvoiceMarketplacePort
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
            ReadProbeEvidence(MarketplaceCapabilities.ReturnRead, identity, "https://developers.trendyol.com/v2.0/docs/getting-returned-orders-getclaims", returns, "İade talepleri", now)
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
        var size = Math.Clamp(page.Limit, 1, 100);
        var query = new List<string> { $"size={size}" };
        if (TryProductCursor(page.Cursor, out var pageNumber, out var nextPageToken))
        {
            if (!string.IsNullOrWhiteSpace(nextPageToken)) query.Add("nextPageToken=" + Uri.EscapeDataString(nextPageToken));
            else query.Add($"page={pageNumber}");
        }
        else query.Add("page=0");
        if (filter.ModifiedAfter is not null)
        {
            query.Add("startDate=" + filter.ModifiedAfter.Value.ToUnixTimeMilliseconds());
            query.Add("dateQueryType=VARIANT_MODIFIED_DATE");
        }
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ApprovedProducts(authorized.Connection.ExternalStoreId) + "?" + string.Join('&', query), null, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(TrendyolJsonMapper.CatalogProducts(response.Value!), response.RateLimit); }
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

    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) =>
        await SubmitBatchAsync(context, HttpMethod.Post, TrendyolEndpoints.ProductCreate, publication.PayloadJson, "PRODUCT_CREATE_V2", cancellationToken);

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
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOperationStatus>.Failure(TrendyolErrorMapper.Configuration()); var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.BatchResult(authorized.Connection.ExternalStoreId, externalOperationId), null, cancellationToken); if (!response.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<RemoteOperationStatus>.Success(TrendyolJsonMapper.Batch(response.Value!, externalOperationId), response.RateLimit); } catch (JsonException) { return AdapterResult<RemoteOperationStatus>.Failure(TrendyolErrorMapper.Contract()); }
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
        var usePackageStatusApi = !string.IsNullOrWhiteSpace(window.PackageItemStatuses);
        OrderWindowCursor? orderCursor = null;
        if (usePackageStatusApi)
        {
            if (!TryOrderWindowCursor(page.Cursor, timeProvider.GetUtcNow(), out orderCursor))
                return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(TrendyolErrorMapper.Contract());
            query.Add($"page={orderCursor.Page}");
            query.Add("status=" + Uri.EscapeDataString(window.PackageItemStatuses!));
            query.Add("orderByField=PackageLastModifiedDate");
            query.Add("orderByDirection=DESC");
            var historyStart = window.PackageItemStatuses is "Delivered" or "Awaiting"
                ? orderCursor.AnchorEnd.AddMonths(-6)
                : orderCursor.AnchorEnd - OrderWindowSpan;
            var endDate = orderCursor.AnchorEnd - TimeSpan.FromTicks(orderCursor.WindowIndex * (OrderWindowSpan.Ticks + TimeSpan.TicksPerMillisecond));
            var startDate = endDate - OrderWindowSpan;
            if (startDate < historyStart) startDate = historyStart;
            query.Add("startDate=" + startDate.ToUnixTimeMilliseconds());
            query.Add("endDate=" + endDate.ToUnixTimeMilliseconds());
        }
        else
        {
            // packageItemStatuses is optional in the Stream contract. Omitting
            // it makes this full-scan/incremental reader receive every
            // package status, including marketplace-specific micro-export
            // states that may not be represented by the public status list.
            if (!string.IsNullOrWhiteSpace(page.Cursor)) query.Add("nextCursor=" + Uri.EscapeDataString(page.Cursor));
        }
        if (window.ModifiedAfter is not null) query.Add("lastModifiedStartDate=" + window.ModifiedAfter.Value.ToUnixTimeMilliseconds());
        if (window.ModifiedBefore is not null) query.Add("lastModifiedEndDate=" + window.ModifiedBefore.Value.ToUnixTimeMilliseconds());
        var endpoint = usePackageStatusApi ? TrendyolEndpoints.Orders(authorized.Connection.ExternalStoreId) : TrendyolEndpoints.OrderStream(authorized.Connection.ExternalStoreId);
        var response = await SendAsync(authorized, HttpMethod.Get, endpoint + "?" + string.Join('&', query), null, cancellationToken, storeFrontCode: window.StoreFrontCode); if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(response.Error!, response.RateLimit);
        LogOrderResponseShape(response.Value!);
        try
        {
            var mapped = TrendyolJsonMapper.Orders(response.Value!);
            if (orderCursor is null) return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(mapped, response.RateLimit);

            var next = mapped.HasMore
                ? orderCursor with { Page = orderCursor.Page + 1 }
                : orderCursor with { WindowIndex = orderCursor.WindowIndex + 1, Page = 0 };
            var nextEnd = next.AnchorEnd - TimeSpan.FromTicks(next.WindowIndex * (OrderWindowSpan.Ticks + TimeSpan.TicksPerMillisecond));
            var historyStart = window.PackageItemStatuses is "Delivered" or "Awaiting"
                ? next.AnchorEnd.AddMonths(-6)
                : next.AnchorEnd - OrderWindowSpan;
            var hasMore = mapped.HasMore || nextEnd >= historyStart;
            return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(
                new(mapped.Items, hasMore ? OrderWindowCursorValue(next) : null, hasMore), response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalOrderId)) return AdapterResult<RemoteOrder>.Failure(TrendyolErrorMapper.Contract());
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOrder>.Failure(TrendyolErrorMapper.Configuration());
        var normalizedOrderNumber = externalOrderId.Trim();
        foreach (var storeFrontCode in TrendyolReadStorefronts.ReturnOrderCodes)
        {
            var lookupFilters = $"orderNumber={Uri.EscapeDataString(normalizedOrderNumber)}&page=0&size=200";
            var endpoints = new[]
            {
                TrendyolEndpoints.Orders(authorized.Connection.ExternalStoreId) + "?" + lookupFilters,
                TrendyolEndpoints.Orders(authorized.Connection.ExternalStoreId) + $"?orderNumber={Uri.EscapeDataString($"\"{normalizedOrderNumber}\"")}&page=0&size=200"
            };
            foreach (var endpoint in endpoints)
            {
                var response = await SendAsync(authorized, HttpMethod.Get, endpoint, null, cancellationToken, storeFrontCode: storeFrontCode);
                if (!response.IsSuccess)
                {
                    if (response.Error?.HttpStatus is 400 or 404) continue;
                    return AdapterResult<RemoteOrder>.Failure(response.Error!, response.RateLimit);
                }
                try
                {
                    var page = TrendyolJsonMapper.Orders(response.Value!);
                    var order = page.Items.FirstOrDefault(x => string.Equals(x.ExternalOrderId, normalizedOrderNumber, StringComparison.OrdinalIgnoreCase) || string.Equals(x.OrderNumber, normalizedOrderNumber, StringComparison.OrdinalIgnoreCase));
                    if (order is not null) return AdapterResult<RemoteOrder>.Success(order, response.RateLimit);
                }
                catch (JsonException) { return AdapterResult<RemoteOrder>.Failure(TrendyolErrorMapper.Contract()); }
            }
        }

        return AdapterResult<RemoteOrder>.Failure(new(AdapterErrorClass.NotFound, "REMOTE_ORDER_NOT_FOUND", "Platform siparişi bulunamadı.", 404, null, null));
    }

    public async Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<PackageActionResult>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized, context)) return AdapterResult<PackageActionResult>.Failure(TrendyolErrorMapper.WriteClosed());
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
        if (action is not ("MANUAL_DELIVER" or "MANUAL_RETURN"))
        {
            JsonDocument payload; try { payload = JsonDocument.Parse(command.PayloadJson); } catch (JsonException) { return AdapterResult<PackageActionResult>.Failure(TrendyolErrorMapper.Contract()); }
            content = JsonContent.Create(payload.RootElement.Clone()); payload.Dispose();
        }
        var method = action is "SPLIT" or "MULTI_SPLIT" ? HttpMethod.Post : HttpMethod.Put;
        using (content)
        {
            var response = await SendAsync(authorized, method, endpoint, content, cancellationToken); if (!response.IsSuccess) return AdapterResult<PackageActionResult>.Failure(response.Error!, response.RateLimit);
            return AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "ACCEPTED", response.Error?.RemoteRequestId), response.RateLimit);
        }
    }

    public async Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<bool>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized, context)) return AdapterResult<bool>.Failure(TrendyolErrorMapper.WriteClosed());
        if (string.IsNullOrWhiteSpace(request.CargoTrackingNumber) || request.BoxQuantity < 1 || request.VolumetricHeight <= 0) return AdapterResult<bool>.Failure(TrendyolErrorMapper.Contract());
        var body = JsonContent.Create(new { format = "ZPL", boxQuantity = request.BoxQuantity, volumetricHeight = request.VolumetricHeight });
        var response = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.CommonLabel(authorized.Connection.ExternalStoreId, request.CargoTrackingNumber), body, cancellationToken);
        return response.IsSuccess ? AdapterResult<bool>.Success(true, response.RateLimit) : AdapterResult<bool>.Failure(response.Error!, response.RateLimit);
    }

    public async Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken)
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
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Configuration()); var query = new List<string> { $"size={Math.Clamp(page.Limit, 1, 50)}", $"page={Page(page.Cursor)}" }; if (window.ModifiedAfter is not null) query.Add("startDate=" + window.ModifiedAfter.Value.ToUnixTimeMilliseconds()); if (window.ModifiedBefore is not null) query.Add("endDate=" + window.ModifiedBefore.Value.ToUnixTimeMilliseconds());
        var endpoint = TrendyolEndpoints.Claims(authorized.Connection.ExternalStoreId) + "?" + string.Join('&', query);
        // The documented getClaims endpoint is global and does not define the
        // order-stream storeFrontCode header. Keep this read independent from
        // storefront selection so international claims are not hidden or timed out.
        // Production returns default to the Created workflow bucket when no
        // claimItemStatus is supplied. Read each documented status so the
        // local model also receives WaitingInAction (the seller's shipped tab).
        if (string.Equals(authorized.Connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase))
            return await PollClaimsByStatusAsync(authorized, endpoint, page, cancellationToken);
        var response = await SendAsync(authorized, HttpMethod.Get, endpoint, null, cancellationToken, storeFrontCode: null);
        if (response.Error?.HttpStatus == (int)HttpStatusCode.NotFound && string.Equals(authorized.Connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase))
            return await PollClaimsByStatusAsync(authorized, endpoint, page, cancellationToken);
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
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized, context)) return AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.WriteClosed());
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
        using var multipart = new MultipartFormDataContent();
        foreach (var file in command.EvidenceFiles)
        {
            var part = new ByteArrayContent(file.Content); part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.MimeType); multipart.Add(part, "file", Path.GetFileName(file.FileName));
        }
        var endpoint = TrendyolEndpoints.RejectClaim(authorized.Connection.ExternalStoreId, command.ExternalClaimId, command.ReasonCode, command.ExternalLineItemIds, command.Explanation.Trim());
        var rejected = await SendAsync(authorized, HttpMethod.Post, endpoint, multipart, cancellationToken);
        return rejected.IsSuccess ? AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, "ISSUE_CREATED", null), rejected.RateLimit) : AdapterResult<ReturnActionResult>.Failure(rejected.Error!, rejected.RateLimit);
    }

    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized, context)) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.WriteClosed());
        if (!string.Equals(command.DeliveryType, "LINK", StringComparison.Ordinal)) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Unsupported("Yalnız resmî link delivery sözleşmesi doğrulandı; file delivery ayrı content akışı kanıtı bekliyor."));
        JsonDocument payload; try { payload = JsonDocument.Parse(command.PayloadJson); } catch (JsonException) { return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Contract()); }
        using (payload)
        {
            if (!payload.RootElement.TryGetProperty("invoiceLink", out var link) || link.ValueKind != JsonValueKind.String || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var invoiceUri) || invoiceUri.Scheme != "https"
                || !payload.RootElement.TryGetProperty("shipmentPackageId", out var packageId) || packageId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)
                || !payload.RootElement.TryGetProperty("invoiceDateTime", out var invoiceDateTime) || !invoiceDateTime.TryGetInt64(out _)
                || !payload.RootElement.TryGetProperty("invoiceNumber", out var invoiceNumber) || invoiceNumber.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(invoiceNumber.GetString()))
                return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Unsupported("HTTPS invoice link, package, invoice date and invoice number doğrulanmadan delivery HTTP çağrısı yapılmaz."));
            var response = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.InvoiceLinks(authorized.Connection.ExternalStoreId), JsonContent.Create(payload.RootElement), cancellationToken); if (!response.IsSuccess) return AdapterResult<InvoiceDeliveryResult>.Failure(response.Error!, response.RateLimit);
            return AdapterResult<InvoiceDeliveryResult>.Success(new(command.ExternalPackageId, "DELIVERED"), response.RateLimit);
        }
    }

    public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<InvoiceDeliveryStatus>.Failure(TrendyolErrorMapper.Unsupported("Invoice delivery status query endpoint’i doğrulanmadı; 409 sahte başarı sayılmaz.")));

    private async Task<AdapterResult<RemoteOperationRef>> SubmitBatchAsync(AdapterContext context, HttpMethod method, Func<string, string> endpointFactory, string payloadJson, string kind, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized, context)) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.WriteClosed());
        JsonDocument payload; try { payload = JsonDocument.Parse(payloadJson); } catch (JsonException) { return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract()); }
        using (payload)
        {
            var response = await SendAsync(authorized, method, endpointFactory(authorized.Connection.ExternalStoreId), JsonContent.Create(payload.RootElement), cancellationToken); if (!response.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(response.Error!, response.RateLimit);
            try { using var document = JsonDocument.Parse(response.Value!); var id = document.RootElement.GetProperty("batchRequestId").GetString(); return string.IsNullOrWhiteSpace(id) ? AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract()) : AdapterResult<RemoteOperationRef>.Success(new(id, kind, timeProvider.GetUtcNow()), response.RateLimit); } catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract()); }
        }
    }

    private bool CanWrite(TrendyolRequestContext context, AdapterContext adapterContext) =>
        IntegrationRuntimePolicy.IsManualStage(context.Connection, adapterContext)
        || (IntegrationRuntimePolicy.IsProduction(context.Connection) && GlobalWritesEnabled && context.ExternalWritesEnabled);
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
    private static readonly TimeSpan OrderWindowSpan = TimeSpan.FromDays(13);
    private sealed record OrderWindowCursor(DateTimeOffset AnchorEnd, int WindowIndex, int Page);
    private static string OrderWindowCursorValue(OrderWindowCursor cursor) =>
        $"v2w:{cursor.AnchorEnd.ToUnixTimeMilliseconds()}:{cursor.WindowIndex}:{cursor.Page}";
    private static bool TryOrderWindowCursor(string? value, DateTimeOffset now, out OrderWindowCursor cursor)
    {
        cursor = new(now, 0, 0);
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.StartsWith("p:", StringComparison.Ordinal)
            && int.TryParse(value.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var legacyPage)
            && legacyPage >= 0)
        {
            cursor = new(now, 0, legacyPage);
            return true;
        }
        var parts = value.Split(':', StringSplitOptions.None);
        if (parts.Length != 4 || parts[0] != "v2w"
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var anchorMilliseconds)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var windowIndex)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var page)
            || windowIndex < 0 || page < 0)
            return false;
        try { cursor = new(DateTimeOffset.FromUnixTimeMilliseconds(anchorMilliseconds), windowIndex, page); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
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

    private async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollClaimsByStatusAsync(TrendyolRequestContext context, string endpoint, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        logger.LogInformation("Trendyol claims are read with the documented claimItemStatus buckets so all seller return tabs are synchronized.");
        string[] statuses = ["Created", "WaitingInAction", "WaitingFraudCheck", "Accepted", "Cancelled", "Rejected", "Unresolved", "InAnalysis"];
        var claims = new List<RemoteReturnClaim>();
        var hasMore = false;
        RateLimitMetadata? rateLimit = null;
        var responses = await Task.WhenAll(statuses.Select(status =>
            SendAsync(context, HttpMethod.Get, endpoint + "&claimItemStatus=" + status, null, cancellationToken, storeFrontCode: null)));
        foreach (var response in responses)
        {
            rateLimit = response.RateLimit ?? rateLimit;
            if (response.Error?.HttpStatus == (int)HttpStatusCode.NotFound) continue;
            if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(response.Error!, response.RateLimit);
            try
            {
                var result = TrendyolJsonMapper.Returns(response.Value!);
                logger.LogInformation("Trendyol claims bucket {Status} returned {Count} claims.", statuses[Array.IndexOf(responses, response)], result.Items.Count);
                claims.AddRange(result.Items);
                hasMore |= result.HasMore;
            }
            catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Contract(), response.RateLimit); }
        }
        var uniqueClaims = claims.GroupBy(x => x.ExternalClaimId, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).OrderByDescending(x => x.LastModifiedAt).ToArray();
        logger.LogInformation("Trendyol claims status buckets merged to {Count} unique claims: {Statuses}.", uniqueClaims.Length, string.Join(",", uniqueClaims.GroupBy(x => x.RawStatus).Select(x => $"{x.Key}:{x.Count()}")));
        var nextCursor = hasMore ? (Page(page.Cursor) + 1).ToString(CultureInfo.InvariantCulture) : null;
        return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new(uniqueClaims, nextCursor, hasMore), rateLimit);
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

    private async Task<AdapterResult<string>> SendClaimsReadAsync(TrendyolRequestContext context, string endpoint, CancellationToken cancellationToken)
    {
        var response = await SendAsync(context, HttpMethod.Get, endpoint, null, cancellationToken);
        if (response.Error?.HttpStatus != (int)HttpStatusCode.NotFound) return response;
        logger.LogWarning("Trendyol Türkiye claims GET returned HTTP 404 with storeFrontCode=TR; retrying the same documented V2 read endpoint without the international storefront header.");
        return await SendAsync(context, HttpMethod.Get, endpoint, null, cancellationToken, includeStoreFrontCode: false);
    }

    private async Task<AdapterResult<string>> SendAsync(TrendyolRequestContext context, HttpMethod method, string endpoint, HttpContent? content, CancellationToken cancellationToken, bool includeStoreFrontCode = true, Action<HttpRequestMessage>? configure = null, string? storeFrontCode = "TR")
    {
        using var request = TrendyolAuthenticationHandler.Create(context, method, endpoint, content, includeStoreFrontCode, storeFrontCode); configure?.Invoke(request); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(RequestTimeout(trendyolOptions.Value.Timeout));
        try
        {
            using var response = await clients.CreateClient("Trendyol").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token); var retryAfter = response.Headers.RetryAfter?.Delta; var rate = new RateLimitMetadata(null, response.Headers.RetryAfter?.Date, retryAfter); var remoteRequestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                var vendorCode = TrendyolErrorMapper.SafeVendorCode(await response.Content.ReadAsStringAsync(linked.Token));
                return AdapterResult<string>.Failure(TrendyolErrorMapper.FromStatus(response.StatusCode, retryAfter, remoteRequestId, vendorCode), rate);
            }
            return AdapterResult<string>.Success(await response.Content.ReadAsStringAsync(linked.Token), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_TIMEOUT", "Platform isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null)); }
        catch (HttpRequestException) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_NETWORK_ERROR", "Platform ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }

    private static TimeSpan RequestTimeout(TimeSpan configured) => configured > TimeSpan.Zero
        ? TimeSpan.FromSeconds(Math.Clamp(configured.TotalSeconds, 5, 300))
        : TimeSpan.FromSeconds(30);
}
