using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient
{
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

    internal async Task<AdapterResult<RemoteOrderPackage>> ReadShipmentPackageAsync(TrendyolRequestContext authorized, string externalPackageId, PackageLookup lookup, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalPackageId)) return AdapterResult<RemoteOrderPackage>.Failure(TrendyolErrorMapper.Contract());
        var packageStatusOccurredAt = lookup.StatusOccurredAt;
        var stopAtV2Match = lookup.InvoiceOnly;
        var storefronts = await LookupStorefrontsAsync(authorized, lookup.Order, cancellationToken);

        // Order V2 keeps a month of packages and accepts at most two weeks per request. A package
        // of an order older than a month is read from the stream below, around its status time (or its order's).
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
        var streamRead = false;
        if (PackageStreamWindow(packageStatusOccurredAt, lookup.Order, timeProvider.GetUtcNow()) is { } streamWindow)
        {
            streamRead = true;
            var scan = await ScanPackageStreamAsync(authorized, externalPackageId.Trim(), v2Package, v2RateLimit, streamWindow.Start, streamWindow.End, storefronts[0], cancellationToken);
            if (scan.Result is { } found) return found;
            streamError = scan.StreamError;
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
        // Audit 2026-11-15 ADAPTOR-TY-HB-3: no Order V2 window (an order older than a month) and no status or order time to read
        // the stream around: nothing was asked, so Trendyol did not say the package is missing. Its own code and summary
        // tell the two apart; the class and status stay NotFound/404, so every caller handles it as it did.
        if (query is null && !streamRead)
            return AdapterResult<RemoteOrderPackage>.Failure(PackageNotAddressable, v2RateLimit);

        var error = v2ReadError
            ?? new AdapterError(AdapterErrorClass.NotFound, "REMOTE_PACKAGE_NOT_FOUND", "Trendyol paket bilgisi bulunamadı.", 404, null, null);
        return error.Class == AdapterErrorClass.NotFound || error.HttpStatus == 404
            ? AdapterResult<RemoteOrderPackage>.Failure(new(AdapterErrorClass.NotFound, "REMOTE_PACKAGE_NOT_FOUND", "Trendyol paket bilgisi bulunamadı.", 404, null, error.RemoteRequestId), v2RateLimit)
            : AdapterResult<RemoteOrderPackage>.Failure(error, v2RateLimit);
    }

    // The stream window a package is looked for in (the stream keeps three months; trendyol.md "Windows and paging"):
    // six days either side of its recorded status time; a package without one (ADAPTOR-TY-HB-3 step 2) is looked for
    // around its order's last recorded change, or its order date (TrendyolLookupPolicy.StreamWindow, the same rule an
    // order read uses). A status time outside the stream's months is not read, as before; null means nothing to read.
    internal static (DateTimeOffset Start, DateTimeOffset End)? PackageStreamWindow(DateTimeOffset? statusOccurredAt, OrderLookupHint? order, DateTimeOffset now)
    {
        if (statusOccurredAt is not { } status) return TrendyolLookupPolicy.StreamWindow(order, now);
        var historyCutoff = now.AddMonths(-TrendyolOrderHistoryPolicy.StreamInitialLookbackMonths);
        if (status < historyCutoff || status > now) return null;
        var start = status - TrendyolLookupPolicy.StreamMargin;
        var end = status + TrendyolLookupPolicy.StreamMargin;
        return (start < historyCutoff ? historyCutoff : start, end > now ? now : end);
    }

    // A stream page is 200 packages and pages are spaced 5 s apart; twenty pages (4,000 packages, about two
    // minutes) cover the 12-day window of a busy seller without letting one package lookup hold the order quota.
    internal const int MaxPackageStreamPages = 20;
    internal const string PackageScanIncompleteCode = "TRENDYOL_PACKAGE_SCAN_INCOMPLETE";
    internal const string PackageNotAddressableCode = "TRENDYOL_PACKAGE_OUT_OF_READ_WINDOW";
    private static readonly AdapterError PackageNotAddressable = new(AdapterErrorClass.NotFound, PackageNotAddressableCode,
        "Trendyol paketi okunabilir dönemin dışında kaldığı için sorulamadı; paketin durumu bilinmiyor.", 404, null, null);
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
                // T-3: the matching package ends the scan even without cargo or invoice fields (a cancelled package
                // has none); reading on would turn a found package into "not found" or an endless incomplete scan.
                // Empty fields stay unknown for the caller; they are not read as "not invoiced".
                if (streamedPackage is not null)
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
}
