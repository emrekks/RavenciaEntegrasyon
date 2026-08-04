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

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolHttpClient(IHttpClientFactory clients, TrendyolAuthenticationHandler authentication, IConfiguration configuration, TimeProvider timeProvider, ILogger<TrendyolHttpClient> logger)
    : IConnectionPort, IReferenceDataPort, IProductPort, IInventoryPricePort, IOrderPort, IReturnPort, IInvoiceMarketplacePort
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
            SupportedEvidence(F3Capabilities.ConnectionTest, identity, "https://developers.trendyol.com/v2.0/docs/authorization", "Stage/Production kimlik doğrulaması order stream read ile geçti.", now),
            SupportedEvidence(F3Capabilities.OrderRead, identity, "https://developers.trendyol.com/v2.0/docs/getshipmentpackagesstream", "Cursor order stream read yanıtı alındı.", now),
            ReadProbeEvidence(F3Capabilities.ReferenceRead, identity, "https://developers.trendyol.com/v2.0/docs/trendyol-category-list-getcategorytree", references, "Kategori ağacı", now),
            ReadProbeEvidence(F3Capabilities.ProductRead, identity, "https://developers.trendyol.com/v2.0/docs/product-filtering-approved-products-v2", products, "Onaylı ürün listesi", now),
            ReadProbeEvidence(F3Capabilities.ReturnRead, identity, "https://developers.trendyol.com/v2.0/docs/getting-returned-orders-getclaims", returns, "İade talepleri", now)
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
            var hasMore = type is "BRANDS" or "ATTRIBUTE_VALUES" && items.Count >= page.Limit;
            var next = hasMore ? (Page(page.Cursor) + 1).ToString(CultureInfo.InvariantCulture) : null;
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(items, next, hasMore), response.RateLimit);
        }
        catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(TrendyolErrorMapper.Configuration());
        var query = new List<string> { $"size={page.Limit}" }; if (!string.IsNullOrWhiteSpace(page.Cursor)) query.Add("nextPageToken=" + Uri.EscapeDataString(page.Cursor)); if (filter.ModifiedAfter is not null) { query.Add("startDate=" + filter.ModifiedAfter.Value.ToUnixTimeMilliseconds()); query.Add("dateQueryType=VARIANT_MODIFIED_DATE"); }
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.ApprovedProducts(authorized.Connection.ExternalStoreId) + "?" + string.Join('&', query), null, cancellationToken); if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<AdapterPageResult<RemoteProduct>>.Success(TrendyolJsonMapper.Products(response.Value!), response.RateLimit); } catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public async Task<AdapterResult<RemoteOperationRef>> UpsertAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized)) return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.WriteClosed());
        JsonDocument payload; try { payload = JsonDocument.Parse(publication.PayloadJson); } catch (JsonException) { return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract()); }
        using (payload)
        {
            var response = await SendAsync(authorized, HttpMethod.Post, TrendyolEndpoints.ProductCreate(authorized.Connection.ExternalStoreId), JsonContent.Create(payload.RootElement), cancellationToken); if (!response.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(response.Error!, response.RateLimit);
            try { using var doc = JsonDocument.Parse(response.Value!); var id = doc.RootElement.GetProperty("batchRequestId").GetString(); return string.IsNullOrWhiteSpace(id) ? AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract()) : AdapterResult<RemoteOperationRef>.Success(new(id, "PRODUCT_V2", timeProvider.GetUtcNow()), response.RateLimit); } catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return AdapterResult<RemoteOperationRef>.Failure(TrendyolErrorMapper.Contract()); }
        }
    }

    public async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<RemoteOperationStatus>.Failure(TrendyolErrorMapper.Configuration()); var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.BatchResult(authorized.Connection.ExternalStoreId, externalOperationId), null, cancellationToken); if (!response.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<RemoteOperationStatus>.Success(TrendyolJsonMapper.Batch(response.Value!, externalOperationId), response.RateLimit); } catch (JsonException) { return AdapterResult<RemoteOperationStatus>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public Task<AdapterResult<bool>> ArchiveAsync(AdapterContext context, ExternalProductIdentity identity, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<bool>.Failure(TrendyolErrorMapper.Unsupported("Archive write capability ve exact V2 request Stage kanıtı bekleniyor.")));
    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushStockAsync(AdapterContext context, IReadOnlyList<StockPushLine> lines, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<BatchResult<BatchLineResult>>.Failure(TrendyolErrorMapper.Unsupported("Ayrık stok payload capability kanıtı yok; birleşik price-and-inventory isteği uydurulmadı.")));
    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushPricesAsync(AdapterContext context, IReadOnlyList<PricePushLine> lines, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<BatchResult<BatchLineResult>>.Failure(TrendyolErrorMapper.Unsupported("Ayrık fiyat payload capability kanıtı yok; birleşik price-and-inventory isteği uydurulmadı.")));

    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(TrendyolErrorMapper.Configuration()); var query = new List<string> { $"size={page.Limit}" }; if (!string.IsNullOrWhiteSpace(page.Cursor)) query.Add("nextCursor=" + Uri.EscapeDataString(page.Cursor)); if (window.ModifiedAfter is not null) query.Add("lastModifiedStartDate=" + window.ModifiedAfter.Value.ToUnixTimeMilliseconds()); if (window.ModifiedBefore is not null) query.Add("lastModifiedEndDate=" + window.ModifiedBefore.Value.ToUnixTimeMilliseconds());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.OrderStream(authorized.Connection.ExternalStoreId) + "?" + string.Join('&', query), null, cancellationToken); if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(response.Error!, response.RateLimit);
        LogOrderResponseShape(response.Value!);
        try { return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(TrendyolJsonMapper.Orders(response.Value!), response.RateLimit); } catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<RemoteOrder>.Failure(TrendyolErrorMapper.Unsupported("External order identity tekil lookup sözleşmesiyle eşlenmeden çağrı yapılmaz.")));
    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<PackageActionResult>.Failure(TrendyolErrorMapper.Unsupported("Package action capability ve exact endpoint Stage kanıtı bekleniyor.")));

    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Configuration()); var query = new List<string> { $"size={page.Limit}", $"page={Page(page.Cursor)}" }; if (window.ModifiedAfter is not null) query.Add("startDate=" + window.ModifiedAfter.Value.ToUnixTimeMilliseconds()); if (window.ModifiedBefore is not null) query.Add("endDate=" + window.ModifiedBefore.Value.ToUnixTimeMilliseconds());
        var response = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.Claims(authorized.Connection.ExternalStoreId) + "?" + string.Join('&', query), null, cancellationToken); if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(TrendyolJsonMapper.Returns(response.Value!), response.RateLimit); } catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Failure(TrendyolErrorMapper.Contract()); }
    }

    Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<RemoteReturnClaim>.Failure(TrendyolErrorMapper.Unsupported("Claim kimliği tekil lookup biçimi Stage kanıtı bekliyor.")));
    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => Task.FromResult(AdapterResult<ReturnActionResult>.Failure(TrendyolErrorMapper.Unsupported("Return write capability ve exact endpoint Stage kanıtı bekleniyor.")));

    public async Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.Configuration()); if (!CanWrite(authorized)) return AdapterResult<InvoiceDeliveryResult>.Failure(TrendyolErrorMapper.WriteClosed());
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

    private bool CanWrite(TrendyolRequestContext context) => GlobalWritesEnabled && context.ExternalWritesEnabled;
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
    private static bool TryParts(string? value, out string categoryId, out string attributeId) { var parts = value?.Split('/', 2, StringSplitOptions.RemoveEmptyEntries) ?? []; categoryId = parts.ElementAtOrDefault(0) ?? ""; attributeId = parts.ElementAtOrDefault(1) ?? ""; return parts.Length == 2; }

    private async Task<AdapterResult<string>> SendAsync(TrendyolRequestContext context, HttpMethod method, string endpoint, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = TrendyolAuthenticationHandler.Create(context, method, endpoint, content); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await clients.CreateClient("Trendyol").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token); var retryAfter = response.Headers.RetryAfter?.Delta; var rate = new RateLimitMetadata(null, response.Headers.RetryAfter?.Date, retryAfter); var remoteRequestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode) return AdapterResult<string>.Failure(TrendyolErrorMapper.FromStatus(response.StatusCode, retryAfter, remoteRequestId), rate);
            return AdapterResult<string>.Success(await response.Content.ReadAsStringAsync(linked.Token), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_TIMEOUT", "Platform isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null)); }
        catch (HttpRequestException) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_NETWORK_ERROR", "Platform ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }
}
