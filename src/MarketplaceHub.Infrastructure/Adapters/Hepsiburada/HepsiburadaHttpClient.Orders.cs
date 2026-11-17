using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient
{
    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (window.ModifiedAfter is { } start && window.ModifiedBefore is { } end && end - start > TimeSpan.FromHours(24))
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Validation, "HEPSIBURADA_ORDER_WINDOW_TOO_LARGE", "Hepsiburada sipariş endpoint'i en fazla 24 saatlik tarih aralığı kabul eder.", HttpStatusCode.BadRequest);
        var (offset, limit) = Page(page, settings.PageSize, documentedMaximum: 100);
        var query = new List<string> { $"offset={offset.ToString(CultureInfo.InvariantCulture)}", $"limit={limit.ToString(CultureInfo.InvariantCulture)}" };
        if (window.ModifiedAfter is { } after) query.Add("begindate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(after)));
        if (window.ModifiedBefore is { } before) query.Add("enddate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(before)));
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Orders(account, string.Join('&', query)), cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(response.Error!, response.RateLimit);
        try
        {
            var pageResult = HepsiburadaJsonMapper.OrderPage(response.Value!.RootElement);
            // The paid-order list contains the line, customer, address, price,
            // and due-date fields needed by the panel. Use it directly; the
            // detail endpoint is reserved for lifecycle checks on existing orders.
            // One item the mapper cannot take does not stop the page (5 Nov 2026): its order is set aside with every other
            // item of it (an order is never imported short of a line) and read by number below.
            var items = pageResult.Items.Select(item => (Order: HepsiburadaJsonMapper.PaidOrderLine(item, out var refusal), Refusal: refusal)).ToList();
            var refusedOrders = items.Where(item => item.Refusal?.RereadOrderId is not null).Select(item => item.Refusal!.RereadOrderId!).ToHashSet(StringComparer.Ordinal);
            var mapped = items.Where(item => item.Order is not null && !refusedOrders.Contains(item.Order.ExternalOrderId)).Select(item => item.Order!).ToList();
            var issues = items.Where(item => item.Refusal is { RereadOrderId: null }).Select(item => item.Refusal!).ToList();
            foreach (var orderNumber in refusedOrders.Order(StringComparer.Ordinal))
            {
                // The detail read applies the common rule (UnreadableOrderLines): its packages count a line it cannot read.
                var detail = await GetAsync(context, orderNumber, cancellationToken);
                if (detail.IsSuccess) { mapped.Add(detail.Value!); continue; }
                if (detail.Error!.Class is not (AdapterErrorClass.ContractViolation or AdapterErrorClass.NotFound))
                    return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(detail.Error, detail.RateLimit);
                // Not readable by number either: the list's refusal is recorded and the order joins the re-read list.
                issues.Add(items.First(item => item.Refusal?.RereadOrderId == orderNumber).Refusal!);
            }
            var nextOffset = offset + pageResult.Items.Count;
            var hasMore = pageResult.TotalCount is { } total ? nextOffset < total : pageResult.Items.Count == limit;
            return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(new(mapped, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, pageResult.TotalCount, issues), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_ORDER_LIST_CONTRACT_INVALID", "Hepsiburada sipariş listesi beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
    }

    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOrder>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        var result = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, OrderDetails(account, externalOrderId), cancellationToken);
        using var resultDocument = result.Value;
        if (!result.IsSuccess) return AdapterResult<RemoteOrder>.Failure(result.Error!, result.RateLimit);
        try
        {
            var order = HepsiburadaJsonMapper.Order(result.Value!.RootElement, externalOrderId);
            var packages = order.Packages.ToArray();
            for (var index = 0; index < packages.Length; index++)
            {
                // R5: the detail's own carrier and tracking number are used first. Package status reconciliation reads
                // the detail to see packages move on, so the tracking read is left out only where it cannot change
                // anything (TrackingReadCannotChange); an Undelivered, cancelled or returned package is still read.
                if (TrackingReadCannotChange(packages[index])) continue;
                var readback = await ReadPackageTrackingInfoAsync(
                    account,
                    new RemoteOrderPackage(order.ExternalOrderId, packages[index]),
                    cancellationToken);
                packages[index] = readback.Package.Package;
            }
            return AdapterResult<RemoteOrder>.Success(order with { Packages = packages }, result.RateLimit);
        }
        // The order was found but cannot be taken (no order date, an unknown currency…): a contract error naming why, never
        // "not found", so the re-read list spends a try on it (RefusedOrders).
        catch (JsonException exception) { return Failure<RemoteOrder>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_ORDER_CONTRACT_INVALID", $"Hepsiburada sipariş detay yanıtı beklenen sözleşmeyle eşleşmiyor: {exception.Message}", HttpStatusCode.BadGateway); }
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteOrderPackage>>> PollPackagesAsync(AdapterContext context, PackagePollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<AdapterPageResult<RemoteOrderPackage>>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        return await PollPackagesAsync(account, window, page, cancellationToken);
    }

    internal async Task<AdapterResult<AdapterPageResult<RemoteOrderPackage>>> PollPackagesAsync(HepsiburadaRequestContext account, PackagePollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (window.ModifiedAfter is { } start && window.ModifiedBefore is { } end && (end < start || end - start > TimeSpan.FromHours(24)))
            return Failure<AdapterPageResult<RemoteOrderPackage>>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_WINDOW_INVALID", "Hepsiburada paket listelemesi artan ve en fazla 24 saatlik tarih aralığı kabul eder.", HttpStatusCode.BadRequest);

        var (offset, limit) = Page(page, settings.PageSize);
        var (statusOffset, statusLimit) = StatusListPage(offset, limit);
        // The status pages stay open until their rows are mapped below.
        var statusDocuments = new List<JsonDocument>();
        try
        {
            var query = PackageListQuery(offset, limit, window);
            var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, Packages(account, query), cancellationToken);
            using var responseDocument = response.Value;
            if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrderPackage>>.Failure(response.Error!, response.RateLimit);

            var sources = new List<(string? Status, IReadOnlyList<JsonElement> Items, int? TotalCount, int Offset, int Limit)>();
            var openPage = HepsiburadaJsonMapper.PackagePage(response.Value!.RootElement);
            sources.Add((null, openPage.Items, openPage.TotalCount, offset, limit));
            var rateLimit = response.RateLimit;

            // The generic package list only returns Open packages. Hepsiburada
            // exposes shipped/delivered/undelivered packages through separate
            // read-only endpoints, each with a 30-day lookback and max limit 50.
            var statusQuery = PackageListQuery(statusOffset, statusLimit, window);
            foreach (var status in window.IncludeStatusLists ? new[] { "shipped", "delivered", "undelivered" } : [])
            {
                var statusResponse = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, PackagesByStatus(account, status, statusQuery), cancellationToken);
                if (!statusResponse.IsSuccess)
                    return AdapterResult<AdapterPageResult<RemoteOrderPackage>>.Failure(statusResponse.Error!, statusResponse.RateLimit ?? rateLimit);
                statusDocuments.Add(statusResponse.Value!);
                var statusPage = HepsiburadaJsonMapper.PackagePage(statusResponse.Value!.RootElement);
                sources.Add((status, statusPage.Items, statusPage.TotalCount, statusOffset, statusLimit));
                rateLimit = statusResponse.RateLimit ?? rateLimit;
            }

            var rawItems = new List<RemoteOrderPackage>();
            var issues = new List<AdapterPageIssue>();
            foreach (var source in sources)
            {
                foreach (var item in source.Items)
                {
                    try
                    {
                        rawItems.Add(source.Status is null
                            ? HepsiburadaJsonMapper.OrderPackage(item)
                            : HepsiburadaJsonMapper.OrderStatusPackage(item, source.Status));
                    }
                    catch (JsonException)
                    {
                        // A row that still names its order makes the panel read that order by number.
                        if (HepsiburadaJsonMapper.PackageOrderNumber(item) is { } unlinkedOrder)
                        {
                            issues.Add(new(HepsiburadaPackagePolicy.OrderToReadCode, unlinkedOrder, "Hepsiburada paket kaydı siparişe bağlanamadı; sipariş numarasıyla okunacak."));
                            continue;
                        }
                        var identity = HepsiburadaJsonMapper.PackageIdentity(item) ?? $"offset:{offset + rawItems.Count}";
                        issues.Add(new("HEPSIBURADA_PACKAGE_ORDER_LINK_MISSING", identity, "Hepsiburada paket/durum kaydında sipariş, paket kimliği veya olay tarihi yok; kayıt siparişe bağlanmadı."));
                    }
                }
            }

            var deduplicated = rawItems
                .GroupBy(item => (item.ExternalOrderId, item.Package.ExternalPackageId))
                .Select(MergePackageStatusObservation)
                .OrderBy(item => item.ExternalOrderId, StringComparer.Ordinal)
                .ThenBy(item => item.Package.ExternalPackageId, StringComparer.Ordinal)
                .ToList();
            var items = new List<RemoteOrderPackage>(deduplicated.Count);
            foreach (var item in deduplicated)
            {
                // A package the lists already show as delivered, undelivered, cancelled or returned cannot move on
                // through the tracking read; asking for it anyway made every page cost one extra request per row.
                // G13: only when the list already names the carrier and the tracking number, so a package first
                // seen delivered or undelivered still gets its cargo company once. An open package listed with both
                // fields is not asked for either (R5).
                if (SkipsTrackingRead(item))
                {
                    items.Add(item);
                    continue;
                }
                var trackingRead = await ReadPackageTrackingInfoAsync(account, item, cancellationToken);
                items.Add(trackingRead.Package);
                if (trackingRead.Issue is not null) issues.Add(trackingRead.Issue);
            }

            var nextOffset = offset + limit;
            var hasMore = sources.Any(source => source.TotalCount is { } total
                ? source.Offset + source.Limit < total
                : source.Items.Count == source.Limit);
            var totalCount = sources.Where(source => source.TotalCount is not null).Select(source => source.TotalCount!.Value).DefaultIfEmpty().Max();
            return AdapterResult<AdapterPageResult<RemoteOrderPackage>>.Success(new(items, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore, totalCount == 0 ? null : totalCount, issues), rateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteOrderPackage>>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PACKAGE_LIST_CONTRACT_INVALID", "Hepsiburada paket veya sevkiyat durumu listesi beklenen sayfa sözleşmesiyle eşleşmiyor.", HttpStatusCode.BadGateway);
        }
        finally
        {
            foreach (var document in statusDocuments) document.Dispose();
        }
    }

    public async Task<AdapterResult<PackageTrackingStatusSnapshot>> GetPackageTrackingInfoAsync(AdapterContext context, string packageNumber, CancellationToken cancellationToken)
    {
        var normalizedPackageNumber = packageNumber.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPackageNumber))
            return Failure<PackageTrackingStatusSnapshot>(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_NUMBER_REQUIRED", "Hepsiburada paket numarası gerekli.", HttpStatusCode.BadRequest);

        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null)
            return Failure<PackageTrackingStatusSnapshot>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);

        return await GetPackageTrackingInfoAsync(account, normalizedPackageNumber, cancellationToken);
    }

    internal async Task<AdapterResult<PackageTrackingStatusSnapshot>> GetPackageTrackingInfoAsync(HepsiburadaRequestContext account, string packageNumber, CancellationToken cancellationToken)
    {
        var normalizedPackageNumber = packageNumber.Trim();
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get,
            PackageTrackingInfo(account.Connection.ExternalStoreId, normalizedPackageNumber), cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess)
            return AdapterResult<PackageTrackingStatusSnapshot>.Failure(response.Error!, response.RateLimit);

        try
        {
            var tracking = HepsiburadaJsonMapper.PackageTrackingInfo(response.Value!.RootElement, normalizedPackageNumber);
            return AdapterResult<PackageTrackingStatusSnapshot>.Success(
                new(normalizedPackageNumber, tracking.Status, tracking.CargoCompany, tracking.TrackingInfoCode, tracking.OrderNumber, tracking.Barcode), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<PackageTrackingStatusSnapshot>(AdapterErrorClass.ContractViolation,
                "HEPSIBURADA_PACKAGE_TRACKING_INFO_INVALID",
                "Hepsiburada kargo yanıtı istenen paket numarası ve durum bilgileriyle eşleşmedi.", HttpStatusCode.BadGateway);
        }
    }

    // The open package list and the shipped/delivered/undelivered lists all take the lower-case "offset" that live
    // reads have used. A schema copy spells it "Offset"; that spelling was never confirmed against the service, so it
    // is not sent until a read past the first page proves it on SIT.
    // R6: the shipped, delivered and undelivered lists take up to 50 rows, while the open list keeps its page of 10
    // (each open row can cost a tracking read). The job's cursor is the open list's offset, so the status lists
    // page by the same page number: page n reads open rows from n*10 and status rows from n*50. A list already read
    // to its end is still asked (and answers empty) while another list has more, because a plain offset cursor
    // cannot say which lists are done.
    internal const int StatusListPageSize = 50;

    internal static (int Offset, int Limit) StatusListPage(int offset, int limit) =>
        ((int)Math.Min((long)(offset / Math.Max(1, limit)) * StatusListPageSize, int.MaxValue), StatusListPageSize);

    internal static string PackageListQuery(int offset, int limit, PackagePollWindow window)
    {
        var query = new List<string>
        {
            $"offset={offset.ToString(CultureInfo.InvariantCulture)}",
            $"limit={limit.ToString(CultureInfo.InvariantCulture)}"
        };
        if (window.ModifiedAfter is { } after) query.Add("begindate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(after)));
        if (window.ModifiedBefore is { } before) query.Add("enddate=" + Uri.EscapeDataString(HepsiburadaJsonMapper.QueryTime(before)));
        return string.Join('&', query);
    }

    // R5: an open-list package that already names its carrier and tracking number gains only those two fields from
    // the tracking read, so it is not asked for; its later move on comes from the status lists and from package
    // status reconciliation.
    internal static bool SkipsTrackingRead(RemoteOrderPackage package) =>
        HasCarrierAndTracking(package.Package)
        && (HasTerminalListStatus(package) || IsListedOpen(package.Package));

    // ReadPackageTrackingInfoAsync keeps a Delivered status (and its date) whatever the tracking read says; it would
    // only restate the carrier and tracking number, so for a delivered package with both known the read is skipped.
    internal static bool TrackingReadCannotChange(RemotePackage package) =>
        HasCarrierAndTracking(package)
        && string.Equals(package.RawStatus, "Delivered", StringComparison.OrdinalIgnoreCase);

    private static bool HasCarrierAndTracking(RemotePackage package) =>
        !string.IsNullOrWhiteSpace(package.CargoProviderExternalId)
        && !string.IsNullOrWhiteSpace(package.CargoTrackingNumber);

    private static bool IsListedOpen(RemotePackage package) =>
        !package.IsStatusObservation && string.Equals(package.RawStatus, "Open", StringComparison.OrdinalIgnoreCase);

    internal static bool HasTerminalListStatus(RemoteOrderPackage package) =>
        HepsiburadaShipmentPackageStatusPolicy.FromRemote(package.Package.RawStatus) is ShipmentPackageStatus.Delivered
            or ShipmentPackageStatus.Undelivered
            or ShipmentPackageStatus.Cancelled
            or ShipmentPackageStatus.Returned;

    private static RemoteOrderPackage MergePackageStatusObservation(IGrouping<(string ExternalOrderId, string ExternalPackageId), RemoteOrderPackage> group)
    {
        var latest = group.OrderByDescending(item => item.Package.OccurredAt).First();
        var detailed = group.FirstOrDefault(item => !item.Package.IsStatusObservation || item.Package.Allocations.Count > 0);
        if (detailed is null) return latest;

        var updatedPackage = detailed.Package with
        {
            RawStatus = latest.Package.OccurredAt >= detailed.Package.OccurredAt ? latest.Package.RawStatus : detailed.Package.RawStatus,
            OccurredAt = latest.Package.OccurredAt > detailed.Package.OccurredAt ? latest.Package.OccurredAt : detailed.Package.OccurredAt,
            CargoProviderExternalId = latest.Package.CargoProviderExternalId ?? detailed.Package.CargoProviderExternalId,
            CargoTrackingNumber = latest.Package.CargoTrackingNumber ?? detailed.Package.CargoTrackingNumber,
            CargoBarcode = latest.Package.CargoBarcode ?? detailed.Package.CargoBarcode,
            Invoice = latest.Package.Invoice ?? detailed.Package.Invoice,
            IsStatusObservation = false
        };
        var order = detailed.OrderSnapshot is null
            ? null
            : detailed.OrderSnapshot with { Packages = detailed.OrderSnapshot.Packages.Select(package => package.ExternalPackageId == updatedPackage.ExternalPackageId ? updatedPackage : package).ToArray() };
        return detailed with { Package = updatedPackage, OrderSnapshot = order };
    }

    internal async Task<(RemoteOrderPackage Package, AdapterPageIssue? Issue)> ReadPackageTrackingInfoAsync(
        HepsiburadaRequestContext account,
        RemoteOrderPackage remotePackage,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(account, account.OmsBaseAddress, HttpMethod.Get, PackageTrackingInfo(account.Connection.ExternalStoreId, remotePackage.Package.ExternalPackageId), cancellationToken);
        if (!response.IsSuccess)
        {
            var issue = response.Error?.HttpStatus == (int)HttpStatusCode.NotFound
                ? null
                : new AdapterPageIssue("HEPSIBURADA_PACKAGE_TRACKING_INFO_UNAVAILABLE", remotePackage.Package.ExternalPackageId, "Paket kargo takip bilgisi alınamadı; paket listesi bilgisi korundu.");
            return (remotePackage, issue);
        }

        using var trackingDocument = response.Value!;
        try
        {
            var tracking = HepsiburadaJsonMapper.PackageTrackingInfo(trackingDocument.RootElement, remotePackage.Package.ExternalPackageId);
            var trackingStatus = string.Equals(tracking.Status, "Delivered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tracking.Status, "Undelivered", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(remotePackage.Package.RawStatus, "Delivered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tracking.Status, "InTransit", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(remotePackage.Package.RawStatus, "Undelivered", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(remotePackage.Package.RawStatus, "Delivered", StringComparison.OrdinalIgnoreCase)
                ? tracking.Status!
                : remotePackage.Package.RawStatus;
            // The tracking read has no event date. When it moves the package on (an Open package that is now in
            // transit), the listed date belongs to the earlier state, so the change counts as observed now;
            // otherwise a stored status time later than that date would make the newer state look stale.
            var advanced = HepsiburadaShipmentPackageStatusPolicy.FromRemote(trackingStatus) != HepsiburadaShipmentPackageStatusPolicy.FromRemote(remotePackage.Package.RawStatus);
            var observedAt = timeProvider.GetUtcNow();
            var enrichedPackage = remotePackage.Package with
            {
                RawStatus = trackingStatus,
                OccurredAt = advanced && observedAt > remotePackage.Package.OccurredAt ? observedAt : remotePackage.Package.OccurredAt,
                CargoProviderExternalId = string.IsNullOrWhiteSpace(tracking.CargoCompany) ? remotePackage.Package.CargoProviderExternalId : tracking.CargoCompany,
                CargoTrackingNumber = string.IsNullOrWhiteSpace(tracking.TrackingInfoCode) ? remotePackage.Package.CargoTrackingNumber : tracking.TrackingInfoCode,
                CargoBarcode = string.IsNullOrWhiteSpace(tracking.Barcode) ? remotePackage.Package.CargoBarcode : tracking.Barcode
            };
            var orderSnapshot = remotePackage.OrderSnapshot is null
                ? null
                : remotePackage.OrderSnapshot with
                {
                    Packages = remotePackage.OrderSnapshot.Packages
                        .Select(package => package.ExternalPackageId == enrichedPackage.ExternalPackageId ? enrichedPackage : package)
                        .ToArray()
                };
            return (remotePackage with { Package = enrichedPackage, OrderSnapshot = orderSnapshot }, null);
        }
        catch (JsonException)
        {
            return (remotePackage, new AdapterPageIssue("HEPSIBURADA_PACKAGE_TRACKING_INFO_INVALID", remotePackage.Package.ExternalPackageId, "Paket kargo yanıtı beklenen packageNumber alanıyla eşleşmedi; paket listesi bilgisi korundu."));
        }
    }

    public async Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string sku, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<StageTestOrderResult>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar test siparişi gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<StageTestOrderResult>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!context.IsStageCapabilityProbe || !string.Equals(account.Connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)
            || !ExternalWriteGate.AllowsRuntimeWrite(account.Connection, context, GlobalWritesEnabled))
            return AdapterResult<StageTestOrderResult>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_TEST_ORDER_STAGE_ONLY", "Hepsiburada test siparişi yalnız kullanıcı tarafından başlatılan Stage capability fixture'ında kullanılabilir.", null, null, null));
        if (account.StageTestOrderBaseAddress is null || !ValidTestOrderSku(sku))
            return Failure<StageTestOrderResult>(AdapterErrorClass.Validation, "HEPSIBURADA_TEST_ORDER_INPUT_INVALID", "Stage test siparişi için doğrulanmış HBSKU ve test ortamı adresi gerekir.", HttpStatusCode.BadRequest);
        if ((await StageHepsiburadaSkusAsync(account, cancellationToken)).TryGetValue(sku, out var hepsiburadaSku)) sku = hepsiburadaSku;

        var orderNumber = RandomNumberGenerator.GetInt32(1_000_000_000, int.MaxValue).ToString(CultureInfo.InvariantCulture);
        using var content = new StringContent(TestOrderPayload(account.Connection.ExternalStoreId, orderNumber, sku, timeProvider.GetUtcNow()), Encoding.UTF8, "application/json");
        var response = await SendAsync(account, account.StageTestOrderBaseAddress, HttpMethod.Post, StageTestOrder(account, account.Connection.ExternalStoreId), content, cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<StageTestOrderResult>.Failure(response.Error!, response.RateLimit);
        // The SIT stub answers 201 without echoing the order number; the number is the one this request
        // created, so a successful answer without it still identifies the order.
        var responseOrderNumber = Text(response.Value!.RootElement, "orderNumber", "OrderNumber");
        return AdapterResult<StageTestOrderResult>.Success(new(string.IsNullOrWhiteSpace(responseOrderNumber) ? orderNumber : responseOrderNumber), response.RateLimit);
    }

    internal static string Orders(HepsiburadaRequestContext context, string query) => $"orders/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}?{query}";
    internal static string OrderDetails(HepsiburadaRequestContext context, string orderNumber) => $"orders/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/ordernumber/{Uri.EscapeDataString(orderNumber)}";
    internal static string Packages(HepsiburadaRequestContext context, string query) => $"packages/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}?{query}";
    internal static string PackagesByStatus(HepsiburadaRequestContext context, string status, string query) => $"packages/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/{Uri.EscapeDataString(status)}?{query}";
    internal static string PackageTrackingInfo(string merchantId, string packageNumber) => $"packages/merchantid/{Uri.EscapeDataString(merchantId)}/packagenumber/{Uri.EscapeDataString(packageNumber)}";
    // The product import reads a listing's hbSku from "hbSku"/"sku", but the listing service sends it as
    // "hepsiburadaSku", so imported links hold the merchantSku where the hbSku belongs. A STAGE upload or
    // test order reads the merchant's listings first and maps each merchantSku to its real hbSku; a
    // PRODUCTION connection gets an empty map and is sent exactly as before.
    private async Task<IReadOnlyDictionary<string, string>> StageHepsiburadaSkusAsync(HepsiburadaRequestContext account, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(account.Connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)) return map;
        const int limit = 100;
        for (var offset = 0; offset < 2_000; offset += limit)
        {
            var response = await SendAsync(account, account.ListingBaseAddress, HttpMethod.Get, Listings(account, $"offset={offset.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}"), cancellationToken);
            using var responseDocument = response.Value;
            if (!response.IsSuccess) break;
            IReadOnlyList<JsonElement> items;
            int? total;
            try { (items, total) = HepsiburadaJsonMapper.ListingPage(response.Value!.RootElement); }
            catch (JsonException) { break; }
            foreach (var (merchantSku, hepsiburadaSku) in HepsiburadaJsonMapper.ListingSkuPairs(items)) map.TryAdd(merchantSku, hepsiburadaSku);
            if (items.Count < limit || total is { } count && offset + items.Count >= count) break;
        }
        logger.LogInformation("Hepsiburada STAGE listing hbSku eşlemesi okundu: {Count} merchantSku, {Changed} farklı hbSku. ConnectionId: {ConnectionId}", map.Count, map.Count(pair => !string.Equals(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase)), account.Connection.Id);
        return map;
    }
    internal static string WithHepsiburadaSkus(string payloadJson, IReadOnlyDictionary<string, string> hepsiburadaSkus)
    {
        if (hepsiburadaSkus.Count == 0) return payloadJson;
        var root = System.Text.Json.Nodes.JsonNode.Parse(payloadJson)!.AsObject();
        foreach (var item in root["items"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
            if (item["merchantSku"]?.GetValue<string>() is { } merchantSku && hepsiburadaSkus.TryGetValue(merchantSku, out var hepsiburadaSku))
                item["hepsiburadaSku"] = hepsiburadaSku;
        return root.ToJsonString();
    }
    internal static string StageTestOrder(HepsiburadaRequestContext context, string merchantId) => $"orders/merchantId/{Uri.EscapeDataString(merchantId)}";
    internal static bool ValidTestOrderSku(string? sku) => !string.IsNullOrWhiteSpace(sku) && sku == sku.Trim() && !sku.Any(char.IsWhiteSpace) && sku.Length <= 100;
    internal static string TestOrderPayload(string merchantId, string orderNumber, string sku, DateTimeOffset orderDate) => JsonSerializer.Serialize(new
    {
        OrderNumber = orderNumber,
        OrderDate = orderDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        PaymentStatus = "Paid",
        Customer = new { CustomerId = "stage-customer", Name = "Stage Test" },
        DeliveryAddress = new { AddressId = "stage-address", Name = "Stage Test", AddressDetail = "Test adresi", Email = "stage-test@example.invalid", CountryCode = "TR", PhoneNumber = "0000000000", AlternatePhoneNumber = "0000000000", Town = "Kadikoy", District = "Test", City = "Istanbul" },
        LineItems = new[] { new { Sku = sku, MerchantId = merchantId, Quantity = 1, Price = new { Amount = 1m, Currency = "TRY" }, Vat = 0m, TotalPrice = new { Amount = 1m, Currency = "TRY" }, CargoCompanyId = 1, DeliveryOptionId = 1 } }
    });
}
