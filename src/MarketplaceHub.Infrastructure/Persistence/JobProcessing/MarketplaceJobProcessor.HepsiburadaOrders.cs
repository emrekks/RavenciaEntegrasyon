using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private async Task<bool> SyncHepsiburadaOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
    {
        string? externalOrderId = null;
        string? packageNumber = null;
        var full = false;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.TryGetProperty("externalOrderId", out var id) && id.ValueKind == JsonValueKind.String) externalOrderId = id.GetString();
            if (payload.RootElement.TryGetProperty("packageNumber", out var package) && package.ValueKind == JsonValueKind.String) packageNumber = package.GetString()?.Trim();
            if (payload.RootElement.TryGetProperty("full", out var fullValue) && fullValue.ValueKind is JsonValueKind.True or JsonValueKind.False) full = fullValue.GetBoolean();
        }
        catch (JsonException) { return false; }

        if (!string.IsNullOrWhiteSpace(externalOrderId))
        {
            TrackRequest();
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-order-get:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
            if (!single.IsSuccess) { TrackResultFailure(single.Error); throw JobProcessingException.FromAdapter(single.Error!); }
            TrackReceived();
            var remoteOrder = single.Value!;
            if (!string.IsNullOrWhiteSpace(packageNumber))
            {
                TrackRequest();
                var tracking = await orderPackages.GetPackageTrackingInfoAsync(
                    Context(tenantId, connectionId, correlationId, $"hepsiburada-package-status:{packageNumber}"), packageNumber, cancellationToken);
                if (!tracking.IsSuccess) { TrackResultFailure(tracking.Error); throw JobProcessingException.FromAdapter(tracking.Error!); }
                TrackReceived();
                if (!string.IsNullOrWhiteSpace(tracking.Value!.OrderNumber)
                    && !string.Equals(tracking.Value.OrderNumber.Trim(), remoteOrder.OrderNumber, StringComparison.OrdinalIgnoreCase))
                    throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_ORDER_MISMATCH", "Paket numarası girilen Hepsiburada siparişine ait değil; durum güncellenmedi.", 422, null, null));
                if (string.IsNullOrWhiteSpace(tracking.Value.Status)
                    || HepsiburadaOrderLifecycleStatusPolicy.FromRemote(tracking.Value.Status) is null)
                    throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PACKAGE_STATUS_UNSUPPORTED", "Hepsiburada paket durum yanıtı tanınmadı; sipariş durumu değiştirilmedi.", 502, null, null));

                // The direct package endpoint is a point-in-time GET and does
                // not return the original delivery timestamp. Record the
                // observation time while keeping the remote lifecycle status
                // and package identity authoritative.
                var observation = new RemotePackage(
                    packageNumber,
                    null,
                    tracking.Value.Status,
                    timeProvider.GetUtcNow(),
                    tracking.Value.CargoCompany,
                    tracking.Value.TrackingInfoCode,
                    [],
                    IsStatusObservation: true,
                    CargoBarcode: tracking.Value.Barcode);
                remoteOrder = remoteOrder with
                {
                    Packages = [observation]
                };
            }
            if (!await UpsertOrder(tenantId, connectionId, remoteOrder, cancellationToken))
                throw JobProcessingException.FromAdapter(new AdapterError(
                    AdapterErrorClass.ContractViolation,
                    "HEPSIBURADA_ORDER_SYNC_NOT_APPLIED",
                    "Hepsiburada sipariş verisi alındı ancak panel bütünlük denetimi güncellemeyi uygulamadı; işlem hata olarak kaydedildi.",
                    502,
                    null,
                    null));
            return true;
        }

        var cursor = await Cursor(tenantId, connectionId, cursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var overlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? DefaultOrderSyncOverlapSeconds;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(overlapSeconds, 0, 86_399));
        var hasSnapshots = await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        // A failed run does not force a new baseline: the saved window state and the watermark are only
        // advanced after a page is stored, so the next run resumes where the failure stopped it.
        var forceBaseline = allowBaseline && (full || !hasSnapshots);
        var state = ReadHepsiburadaOrderSyncState(cursor.OpaqueCursor, now, forceBaseline);
        if (state is null)
        {
            var anchor = now;
            var oldestAvailable = HepsiburadaOrderHistoryPolicy.InitialWindowStart(anchor);
            var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt ?? anchor.AddHours(-24);
            if (watermark > anchor) watermark = anchor;
            var start = forceBaseline
                ? oldestAvailable
                : HepsiburadaOrderHistoryPolicy.ClampWindowStart(anchor, watermark.Subtract(overlap));
            state = new(HepsiburadaOrderSyncStateVersion, anchor, start, 0, 0);
        }

        while (true)
        {
            var windowEnd = state.AnchorEnd.AddDays(-state.WindowIndex);
            if (windowEnd > state.AnchorEnd) windowEnd = state.AnchorEnd;
            if (windowEnd <= state.StartAt)
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.LastSuccessAt = timeProvider.GetUtcNow();
                cursor.LastError = null;
                cursor.LastErrorAt = null;
                cursor.ConsecutiveFailureCount = 0;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var windowStart = windowEnd.Subtract(TimeSpan.FromHours(24));
            if (windowStart < state.StartAt) windowStart = state.StartAt;
            TrackRequest();
            var page = await orders.PollAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-orders:{state.WindowIndex}:{state.Offset}"),
                new OrderPollWindow(windowStart, windowEnd),
                new(state.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture), 100),
                cancellationToken);
            if (!page.IsSuccess) { TrackResultFailure(page.Error); throw JobProcessingException.FromAdapter(page.Error!); }
            foreach (var _ in page.Value!.Items) TrackReceived();
            // A paid-order line the list could not map stops neither the page nor the job: its order was read by number
            // instead, or is named here and read again later (HepsiburadaHttpClient.PollAsync).
            foreach (var issue in page.Value.Issues ?? [])
                await RecordIssue(tenantId, $"order-contract:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
            NoteRefusedOrders(page.Value.Issues);
            var importableOrders = page.Value.Items
                .Where(order => HepsiburadaOrderHistoryPolicy.ShouldImportPaidOrder(order, state.AnchorEnd))
                .ToArray();
            await UpsertOrders(tenantId, connectionId, importableOrders, cancellationToken);

            state = NextHepsiburadaOrderPage(state, page.Value);
            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            // Each finished day window releases the lane and the job's timeout: a month-long read would
            // otherwise not fit one run, time out, restart and never finish.
            if (state.Offset == 0 && HepsiburadaWindowRemains(state))
                throw new JobProcessingException(JobExecutionResult.Continue("Hepsiburada sipariş okumasının sonraki günü birkaç saniye içinde okunacak."));
        }
    }

    private const string HepsiburadaOrderSyncStateVersion = "hepsiburada-orders-v2";
    // PageTotal: the totalCount the window's first page gave; Moved: a later page gave another one; Rereads: how many times
    // the window was read again from its start for that reason.
    internal sealed record HepsiburadaOrderSyncState(string Version, DateTimeOffset AnchorEnd, DateTimeOffset StartAt, int WindowIndex, int Offset,
        int? PageTotal = null, bool Moved = false, int Rereads = 0);

    internal const int HepsiburadaOrderWindowRereads = 2;

    // The paid-order list is the live queue of lines waiting to be packaged (hepsiburada-api-son.md:8391-8399), read by
    // offset: a line packaged between two pages moves every later one up a place, and one is never seen; an unpacked
    // package puts its lines back (:9222). totalCount is the count at that moment (:18089), so a window whose pages give
    // different totals is read again from its start, at most twice (before 6 Nov 2026 the window was closed as read).
    internal static HepsiburadaOrderSyncState NextHepsiburadaOrderPage(HepsiburadaOrderSyncState state, AdapterPageResult<RemoteOrder> page)
    {
        var moved = state.Moved || state.Offset > 0 && state.PageTotal is { } first && page.TotalCount is { } total && total != first;
        var pageTotal = state.Offset == 0 ? page.TotalCount : state.PageTotal;
        if (page.HasMore)
        {
            if (!int.TryParse(page.NextCursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var nextOffset)
                || nextOffset <= state.Offset)
                throw new InvalidOperationException("Hepsiburada sayfalaması hasMore=true döndürdü ancak ileri offset üretmedi.");
            return state with { Offset = nextOffset, PageTotal = pageTotal, Moved = moved };
        }
        return moved && state.Rereads < HepsiburadaOrderWindowRereads
            ? state with { Offset = 0, PageTotal = null, Moved = false, Rereads = state.Rereads + 1 }
            : state with { WindowIndex = state.WindowIndex + 1, Offset = 0, PageTotal = null, Moved = false, Rereads = 0 };
    }

    internal static bool HepsiburadaWindowRemains(HepsiburadaOrderSyncState state) =>
        state.AnchorEnd.AddDays(-state.WindowIndex) > state.StartAt;

    // A saved state continues a run under way. When a baseline is asked for (a full read, an empty store),
    // only a baseline that is itself under way and less than a day old continues; restarting it would read
    // its first windows forever. Anything else starts afresh.
    internal static HepsiburadaOrderSyncState? ReadHepsiburadaOrderSyncState(string? opaqueCursor, DateTimeOffset now, bool forceBaseline)
    {
        if (string.IsNullOrWhiteSpace(opaqueCursor)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<HepsiburadaOrderSyncState>(opaqueCursor);
            if (saved is not { Version: HepsiburadaOrderSyncStateVersion, WindowIndex: >= 0, Offset: >= 0 }) return null;
            if (!forceBaseline) return saved;
            var isBaseline = saved.StartAt <= HepsiburadaOrderHistoryPolicy.InitialWindowStart(saved.AnchorEnd);
            return isBaseline && now - saved.AnchorEnd < TimeSpan.FromDays(1) ? saved : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<bool> SyncHepsiburadaPackages(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Marketplace:PersistOrderSnapshots", true)) return true;

        var full = false;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.TryGetProperty("full", out var fullValue) && fullValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                full = fullValue.GetBoolean();
        }
        catch (JsonException) { return false; }

        var cursor = await Cursor(tenantId, connectionId, "PACKAGES", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var overlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "PACKAGES")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? 600;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(overlapSeconds, 0, 86_399));
        var forceBaseline = full || cursor.LastSuccessAt is null || cursor.ConsecutiveFailureCount > 0 || !string.IsNullOrWhiteSpace(cursor.LastError);
        // A full (or first) read resumes the month-long read it left unfinished less than a day ago (a slice ended, a
        // timeout), as the order read does, instead of starting the month again on every try.
        var state = ReadHepsiburadaPackageSyncState(cursor.OpaqueCursor, now, forceBaseline);
        if (state is null)
        {
            var anchor = now;
            var oldestAvailable = OpenOrderLifecyclePolicy.HepsiburadaPackageStatusHistoryCutoff(anchor);
            var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt ?? anchor.AddHours(-24);
            if (watermark > anchor) watermark = anchor;
            var start = forceBaseline ? oldestAvailable : watermark.Subtract(overlap);
            if (start < oldestAvailable) start = oldestAvailable;
            state = new(HepsiburadaPackageSyncStateVersion, anchor, start, 0, 0);
        }
        // The shipped, delivered and undelivered lists ride along every five minutes (always on a full read or a
        // continued one), so the minute-by-minute read is one list request instead of four.
        var statusListsCursor = await Cursor(tenantId, connectionId, HepsiburadaPackagePolicy.StatusListsCursorResource, cancellationToken);
        var includeStatusLists = HepsiburadaPackagePolicy.ShouldReadStatusLists(statusListsCursor.LastSuccessAt, now, full || forceBaseline || state.WindowIndex > 0 || state.Offset > 0);

        while (true)
        {
            var windowEnd = state.AnchorEnd.AddDays(-state.WindowIndex);
            if (windowEnd > state.AnchorEnd) windowEnd = state.AnchorEnd;
            if (windowEnd <= state.StartAt)
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.LastSuccessAt = timeProvider.GetUtcNow();
                cursor.LastError = null;
                cursor.LastErrorAt = null;
                cursor.ConsecutiveFailureCount = 0;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var windowStart = windowEnd.Subtract(TimeSpan.FromHours(24));
            if (windowStart < state.StartAt) windowStart = state.StartAt;
            TrackRequest();
            var page = await orderPackages.PollPackagesAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-packages:{state.WindowIndex}:{state.Offset}"),
                new PackagePollWindow(windowStart, windowEnd, includeStatusLists),
                new(state.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture), 10),
                cancellationToken);
            if (!page.IsSuccess) { TrackResultFailure(page.Error); throw JobProcessingException.FromAdapter(page.Error!); }
            if (includeStatusLists && statusListsCursor.LastSuccessAt != now) { statusListsCursor.LastSuccessAt = now; statusListsCursor.Version++; }
            foreach (var item in page.Value!.Items) TrackReceived();
            foreach (var issue in (page.Value.Issues ?? []).Where(issue => issue.Code != HepsiburadaPackagePolicy.OrderToReadCode))
                await RecordIssue(tenantId, $"package-contract:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
            await ReadMissingHepsiburadaOrdersAsync(tenantId, connectionId, correlationId,
                (page.Value.Issues ?? []).Where(issue => issue.Code == HepsiburadaPackagePolicy.OrderToReadCode).Select(issue => (issue.Identity, (string?)null)), cancellationToken);

            foreach (var orderPackagesForOrder in page.Value.Items
                         .GroupBy(x => x.ExternalOrderId, StringComparer.Ordinal)
                         .OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var snapshots = orderPackagesForOrder.Select(x => x.OrderSnapshot).Where(x => x is not null).Select(x => x!).ToArray();
                var mergedOrder = TrendyolJsonMapper.MergeOrderPackages(snapshots, orderPackagesForOrder.Key);
                if (mergedOrder is null && orderPackagesForOrder.Any(item => item.Package.IsStatusObservation))
                {
                    TrackRequest();
                    var details = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-status-order:{orderPackagesForOrder.Key}"), orderPackagesForOrder.Key, cancellationToken);
                    if (!details.IsSuccess)
                    {
                        if (details.Error?.HttpStatus == 404)
                        {
                            await RecordIssue(tenantId, $"package-status-order:{connectionId}:{orderPackagesForOrder.Key}", "HEPSIBURADA_STATUS_ORDER_DETAIL_NOT_FOUND", "Hepsiburada paket durumunda sipariş var ancak detay servisi siparişi bulamadı; paket durumu bu turda uygulanmadı.", cancellationToken);
                            continue;
                        }
                        TrackResultFailure(details.Error);
                        throw JobProcessingException.FromAdapter(details.Error!);
                    }
                    TrackReceived();
                    mergedOrder = details.Value;
                }
                if (mergedOrder is null)
                {
                    await RecordIssue(tenantId, $"package-order-snapshot:{connectionId}:{orderPackagesForOrder.Key}:{state.WindowIndex}:{state.Offset}", "HEPSIBURADA_PACKAGE_ORDER_SNAPSHOT_MISSING", "Hepsiburada paket yanıtından bağlı sipariş satırları oluşturulamadı; paket yerel siparişe uygulanmadı.", cancellationToken);
                    continue;
                }

                var packages = mergedOrder.Packages.ToList();
                foreach (var observation in orderPackagesForOrder.Where(item => item.Package.IsStatusObservation))
                {
                    var packageIndex = packages.FindIndex(package => package.ExternalPackageId == observation.Package.ExternalPackageId);
                    if (packageIndex < 0)
                    {
                        packages.Add(observation.Package);
                        continue;
                    }

                    var existingPackage = packages[packageIndex];
                    packages[packageIndex] = existingPackage with
                    {
                        RawStatus = observation.Package.OccurredAt >= existingPackage.OccurredAt ? observation.Package.RawStatus : existingPackage.RawStatus,
                        OccurredAt = observation.Package.OccurredAt > existingPackage.OccurredAt ? observation.Package.OccurredAt : existingPackage.OccurredAt,
                        CargoProviderExternalId = observation.Package.CargoProviderExternalId ?? existingPackage.CargoProviderExternalId,
                        CargoTrackingNumber = observation.Package.CargoTrackingNumber ?? existingPackage.CargoTrackingNumber,
                        CargoBarcode = observation.Package.CargoBarcode ?? existingPackage.CargoBarcode,
                        Invoice = observation.Package.Invoice ?? existingPackage.Invoice,
                        IsStatusObservation = false
                    };
                }
                mergedOrder = mergedOrder with
                {
                    LastModifiedAt = packages.Select(package => package.OccurredAt).Append(mergedOrder.LastModifiedAt).Max(),
                    Packages = packages
                };
                TrackReceived();
                await UpsertOrder(tenantId, connectionId, mergedOrder, cancellationToken);
            }

            var pageKey = HepsiburadaPackagePageKey(page.Value.Items);
            if (HepsiburadaPackageOffsetIgnored(state.Offset, state.LastPageKey, pageKey))
            {
                // The same packages again at the next offset: the list did not take the offset (its docs spell it `Offset`,
                // the code sends `offset` as on every other OMS list; never seen live with 10 or more open packages). The
                // window is closed instead of being read forever; status lists, the hourly recovery and webhooks still
                // bring the packages after the first ten.
                await RecordIssue(tenantId, $"hepsiburada-package-offset:{connectionId}", "HEPSIBURADA_PACKAGE_OFFSET_IGNORED",
                    "Hepsiburada açık paket listesi sonraki sayfada aynı paketleri döndürdü (offset yok sayıldı); bu pencerede ilk sayfadan sonrası okunamadı. Paketler durum listeleri ve saatlik okuma ile gelir.", cancellationToken);
                state = state with { WindowIndex = state.WindowIndex + 1, Offset = 0, LastPageKey = null };
            }
            else if (page.Value.HasMore)
            {
                if (!int.TryParse(page.Value.NextCursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var nextOffset)
                    || nextOffset <= state.Offset)
                    throw new InvalidOperationException("Hepsiburada paket sayfalaması hasMore=true döndürdü ancak ileri offset üretmedi.");
                state = state with { Offset = nextOffset, LastPageKey = pageKey };
            }
            else state = state with { WindowIndex = state.WindowIndex + 1, Offset = 0, LastPageKey = null };
            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            // Persist every offset/window so an interrupted full scan resumes
            // without repeating the completed pages. Each finished day of a month-long read releases the lane and the
            // job's timeout, as the order read does.
            if (state.Offset == 0 && state.WindowIndex > 0 && state.AnchorEnd.AddDays(-state.WindowIndex) > state.StartAt
                && state.AnchorEnd - state.StartAt > TimeSpan.FromDays(2))
                throw new JobProcessingException(JobExecutionResult.Continue("Hepsiburada paket okumasının sonraki günü birkaç saniye içinde okunacak."));
        }
    }

    // The saved package read state: always on an ordinary read; on a full or first read only an unfinished month-long
    // read started less than a day ago (one older is read again from the start).
    internal static HepsiburadaPackageSyncState? ReadHepsiburadaPackageSyncState(string? opaqueCursor, DateTimeOffset now, bool forceBaseline)
    {
        if (string.IsNullOrWhiteSpace(opaqueCursor)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<HepsiburadaPackageSyncState>(opaqueCursor);
            if (saved is not { Version: HepsiburadaPackageSyncStateVersion, WindowIndex: >= 0, Offset: >= 0 }) return null;
            if (!forceBaseline) return saved;
            return HepsiburadaPackageBaseline(saved, now) ? saved : null;
        }
        catch (JsonException) { return null; }
    }

    internal static bool HepsiburadaPackageBaseline(HepsiburadaPackageSyncState saved, DateTimeOffset now) =>
        saved.StartAt <= OpenOrderLifecyclePolicy.HepsiburadaPackageStatusHistoryCutoff(saved.AnchorEnd) && now - saved.AnchorEnd < TimeSpan.FromDays(1);

    // A full order read (Verileri Çek, activation) whose orders finished and whose package month is unfinished: the
    // package state was started after the order read last completed.
    private async Task<bool> HepsiburadaPackageBaselinePendingAsync(Guid tenantId, Guid connectionId, string payloadJson, string cursorResourceType, CancellationToken cancellationToken)
    {
        if (cursorResourceType is not ("ORDERS_HOT" or "ORDERS_RECOVERY")) return false;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (!payload.RootElement.TryGetProperty("full", out var full) || full.ValueKind != JsonValueKind.True) return false;
            if (payload.RootElement.TryGetProperty("externalOrderId", out var single) && single.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(single.GetString())) return false;
        }
        catch (JsonException) { return false; }
        var cursors = await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ResourceType == "PACKAGES" || x.ResourceType == cursorResourceType))
            .Select(x => new { x.ResourceType, x.OpaqueCursor, x.LastSuccessAt })
            .ToListAsync(cancellationToken);
        var orders = cursors.FirstOrDefault(x => x.ResourceType == cursorResourceType);
        var packages = cursors.FirstOrDefault(x => x.ResourceType == "PACKAGES");
        return HepsiburadaOrdersDoneForPackageBaseline(orders?.OpaqueCursor, orders?.LastSuccessAt, packages?.OpaqueCursor, timeProvider.GetUtcNow());
    }

    internal static bool HepsiburadaOrdersDoneForPackageBaseline(string? ordersOpaqueCursor, DateTimeOffset? ordersLastSuccessAt, string? packagesOpaqueCursor, DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(ordersOpaqueCursor) && ordersLastSuccessAt is { } ordersDone
        && ReadHepsiburadaPackageSyncState(packagesOpaqueCursor, now, forceBaseline: true) is { } packages
        && (packages.WindowIndex > 0 || packages.Offset > 0) && packages.AnchorEnd >= ordersDone;

    internal const string HepsiburadaPackageSyncStateVersion = "hepsiburada-packages-v2";
    // LastPageKey: the package numbers of the page before, to tell a list that ignores the offset.
    internal sealed record HepsiburadaPackageSyncState(string Version, DateTimeOffset AnchorEnd, DateTimeOffset StartAt, int WindowIndex, int Offset, string? LastPageKey = null);

    internal static string? HepsiburadaPackagePageKey(IReadOnlyList<RemoteOrderPackage> items) =>
        items.Count == 0 ? null : string.Join(',', items.Select(item => item.Package.ExternalPackageId).Order(StringComparer.Ordinal));

    internal static bool HepsiburadaPackageOffsetIgnored(int offset, string? previousPageKey, string? pageKey) =>
        offset > 0 && pageKey is not null && string.Equals(previousPageKey, pageKey, StringComparison.Ordinal);
}
