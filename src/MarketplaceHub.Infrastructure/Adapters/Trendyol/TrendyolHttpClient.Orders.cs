using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient
{
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
                // A 400/404 on the first page means the stream does not serve this window. After it, the packages read
                // so far are only part of the order, so the scan is incomplete and retried, never a success (O6).
                if (response.Error?.HttpStatus is 400 or 404 && pages == 1) break;
                if (response.Error?.HttpStatus is 400 or 404)
                {
                    logger.LogWarning("Trendyol order {ExternalOrderId} stream lookup was refused on page {Page}; the scan is incomplete.", normalizedOrderNumber, pages);
                    return (AdapterResult<RemoteOrder>.Failure(OrderScanIncomplete, rateLimit), firstPageIssue);
                }
                return (AdapterResult<RemoteOrder>.Failure(response.Error!, response.RateLimit), firstPageIssue);
            }
            rateLimit = response.RateLimit;
            try
            {
                var page = TrendyolJsonMapper.Orders(response.Value!);
                // The stream page holds other orders too: only a refusal of this order answers for it.
                firstPageIssue ??= page.Issues?.FirstOrDefault(issue => string.Equals(issue.RereadOrderId, normalizedOrderNumber, StringComparison.OrdinalIgnoreCase));
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
        // A cancelled read leaves pages unread; merging what was read would return part of a split order as the order.
        cancellationToken.ThrowIfCancellationRequested();
        if (TrendyolJsonMapper.MergeOrderPackages(packages, normalizedOrderNumber) is { } streamed)
            return (AdapterResult<RemoteOrder>.Success(streamed with { StoreFrontCode = storeFrontCode }, rateLimit), firstPageIssue);
        return (null, firstPageIssue);
    }

    private void LogOrderResponseShape(string json)
    {
        if (!logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug)) return;
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
            // Debug: it was 14 lines every ten minutes on live; the outline is only needed when a mapping is investigated.
            logger.LogDebug("Trendyol order response schema:RootProperties={RootProperties}; PackageCount={PackageCount}; FirstPackageProperties={FirstPackageProperties}; ArrayFields={ArrayFields}", rootProperties, packages.ValueKind == JsonValueKind.Array ? packages.GetArrayLength() : 0, packageProperties, lineCounts);
        }
        catch (JsonException)
        {
            logger.LogWarning("Trendyol order response is not valid JSON while recording its schema.");
        }
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
}
