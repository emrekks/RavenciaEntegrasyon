using System.Diagnostics;
using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class OrderReadService
{
    private const string HepsiburadaLifecycleCursorPrefix = "after:";

    internal static string HepsiburadaLifecycleCursor(string lastSeenOrderId) => HepsiburadaLifecycleCursorPrefix + lastSeenOrderId;

    // An older numeric offset cursor (or none) starts the scan from the first order.
    internal static string? HepsiburadaLifecycleLastSeen(string? opaqueCursor) =>
        opaqueCursor is { Length: > 6 } saved && saved.StartsWith(HepsiburadaLifecycleCursorPrefix, StringComparison.Ordinal)
            ? saved[HepsiburadaLifecycleCursorPrefix.Length..]
            : null;

    internal static string OpenOrderMissingIssueKey(Guid connectionId, string externalOrderId) =>
        $"order-lifecycle-missing:{connectionId}:{externalOrderId}";

    // The packages an order status scan follows: neither delivered, cancelled nor returned (the scans below and the
    // scheduler's check that one is worth queueing, ScheduledJobProducer.OpenPackagesExistAsync).
    internal static IQueryable<ShipmentPackage> OpenPackages(IQueryable<ShipmentPackage> packages, Guid tenantId, Guid connectionId) =>
        OpenPackages(packages, tenantId).Where(package => package.ConnectionId == connectionId);

    internal static IQueryable<ShipmentPackage> OpenPackages(IQueryable<ShipmentPackage> packages, Guid tenantId) =>
        packages.Where(package => package.TenantId == tenantId
            && package.Status != ShipmentPackageStatus.Delivered && package.Status != ShipmentPackageStatus.Cancelled && package.Status != ShipmentPackageStatus.Returned);

    internal async Task<bool> SyncOpenOrders(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        var isHepsiburada = platformCode == "HEPSIBURADA";
        var isTrendyol = string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase);
        if (isTrendyol) return await SyncTrendyolOpenPackages(tenantId, connectionId, correlationId, cancellationToken);
        var lifecycleBatchSize = OpenOrderLifecyclePolicy.LifecycleBatchSize(
            platformCode,
            configuration.GetValue("MarketplaceSync:OrderLifecycle:BatchSize", 25),
            configuration.GetValue("MarketplaceSync:OrderLifecycle:TrendyolBatchSize", 1));
        var cursor = await Cursor(tenantId, connectionId, "ORDER_LIFECYCLE", cancellationToken);
        List<string> externalOrderIds;
        var hepsiburadaListedCancellations = new List<RemoteCancelledOrderLine>();
        if (isHepsiburada)
        {
            var includeUnpackagedNewOrders = OpenOrderLifecyclePolicy.ShouldPollWithoutPackage(platformCode, "NEW");
            var includeUnpackagedOnHoldOrders = OpenOrderLifecyclePolicy.ShouldPollWithoutPackage(platformCode, "ON_HOLD");
            // An order reviewed because its lifecycle status was unknown is read again until Hepsiburada names one.
            var includeUnpackagedReviewOrders = OpenOrderLifecyclePolicy.ShouldPollWithoutPackage(platformCode, "MANUAL_REVIEW");
            var unpackagedCutoff = OpenOrderLifecyclePolicy.HepsiburadaUnpackagedOrderVerificationCutoff(timeProvider.GetUtcNow());
            // Unpackaged as the order list counts it (O14): a package the panel unpacked (UNPACKED_BY_PANEL,
            // cancelled) leaves the order to be read again.
            var lifecycleOrders = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId
                && order.ConnectionId == connectionId
                && (db.ShipmentPackages.Any(package => package.TenantId == tenantId
                        && package.ConnectionId == connectionId
                        && package.OrderId == order.Id
                        && package.Status != ShipmentPackageStatus.Delivered
                        && package.Status != ShipmentPackageStatus.Cancelled
                        && package.Status != ShipmentPackageStatus.Returned)
                    || (order.OrderedAt >= unpackagedCutoff
                        && (includeUnpackagedNewOrders && order.DerivedStatus == "NEW"
                            || includeUnpackagedOnHoldOrders && order.DerivedStatus == "ON_HOLD"
                            || includeUnpackagedReviewOrders && order.DerivedStatus == "MANUAL_REVIEW")
                        && !db.ShipmentPackages.Any(package => package.TenantId == tenantId
                            && package.ConnectionId == connectionId
                            && package.OrderId == order.Id
                            && package.Status != ShipmentPackageStatus.Cancelled))));

            // Keyset paging: the set shrinks as packages are delivered, so an offset would skip the orders
            // that slid into the pages already read. The cursor keeps the last order id read instead.
            var lastSeenOrderId = HepsiburadaLifecycleLastSeen(cursor.OpaqueCursor);
            var lifecyclePage = lastSeenOrderId is null
                ? lifecycleOrders
                : lifecycleOrders.Where(order => string.Compare(order.ExternalOrderId, lastSeenOrderId) > 0);
            externalOrderIds = await lifecyclePage
                .OrderBy(order => order.ExternalOrderId)
                .Select(order => order.ExternalOrderId)
                .Take(lifecycleBatchSize)
                .ToListAsync(cancellationToken);
            if (externalOrderIds.Count == 0 && lastSeenOrderId is not null)
            {
                // Wrap to the first page so the lifecycle scan keeps polling
                // when a status request fails or an order has no new package.
                externalOrderIds = await lifecycleOrders
                    .OrderBy(order => order.ExternalOrderId)
                    .Select(order => order.ExternalOrderId)
                    .Take(lifecycleBatchSize)
                    .ToListAsync(cancellationToken);
            }
            cursor.OpaqueCursor = externalOrderIds.Count == lifecycleBatchSize
                ? HepsiburadaLifecycleCursor(externalOrderIds[^1])
                : null;
            // Orders Hepsiburada lists as cancelled but the panel does not are read again now.
            foreach (var cancelled in await HepsiburadaCancelledOrdersToRead(tenantId, connectionId, correlationId, hepsiburadaListedCancellations, cancellationToken))
                if (!externalOrderIds.Contains(cancelled, StringComparer.Ordinal)) externalOrderIds.Add(cancelled);
        }
        else if (platformCode == "PAZARAMA" && await SyncPazaramaOpenOrdersByWindow(tenantId, connectionId, correlationId, lifecycleBatchSize, cancellationToken)
            || platformCode == "PTTAVM" && await SyncPttAvmOpenOrdersByWindow(tenantId, connectionId, correlationId, lifecycleBatchSize, cancellationToken))
        {
            cursor.LastModifiedWatermark = timeProvider.GetUtcNow();
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        else
        {
            // ISLEYICI-5: keyset progress over the orders with an open package, as the Hepsiburada branch does. The scan
            // used to take the least lately read orders first; an order the marketplace no longer finds is never updated,
            // so a batch of them stayed at the head of the list and the orders behind it were not read again. The cursor
            // keeps the last order id read and the scan wraps to the first order when the list ends.
            var openOrderIds = db.Orders.AsNoTracking()
                .Where(order => order.TenantId == tenantId
                    && db.ShipmentPackages.Any(package => package.TenantId == tenantId
                        && package.ConnectionId == connectionId
                        && package.OrderId == order.Id
                        && package.Status != ShipmentPackageStatus.Delivered
                        && package.Status != ShipmentPackageStatus.Cancelled
                        && package.Status != ShipmentPackageStatus.Returned))
                .Select(order => order.ExternalOrderId)
                .Distinct();
            var lastSeenOrderId = HepsiburadaLifecycleLastSeen(cursor.OpaqueCursor);
            var lifecyclePage = lastSeenOrderId is null
                ? openOrderIds
                : openOrderIds.Where(id => string.Compare(id, lastSeenOrderId) > 0);
            externalOrderIds = await lifecyclePage.OrderBy(id => id).Take(lifecycleBatchSize).ToListAsync(cancellationToken);
            if (externalOrderIds.Count == 0 && lastSeenOrderId is not null)
                externalOrderIds = await openOrderIds.OrderBy(id => id).Take(lifecycleBatchSize).ToListAsync(cancellationToken);
            cursor.OpaqueCursor = externalOrderIds.Count == lifecycleBatchSize
                ? HepsiburadaLifecycleCursor(externalOrderIds[^1])
                : null;
        }

        var recoveredOrders = new List<RemoteOrder>();
        foreach (var externalOrderId in externalOrderIds)
        {
            TrackRequest();
            var result = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-lifecycle:{externalOrderId}"), externalOrderId, await OrderHint(tenantId, connectionId, externalOrderId, cancellationToken), cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound)
                {
                    // ISLEYICI-5: the owner sees an open order the marketplace no longer finds; its state is left as it
                    // is (a missing answer says nothing about the order). Hepsiburada keeps its own checks for this.
                    if (!isHepsiburada)
                        await RecordIssue(
                            tenantId,
                            OpenOrderMissingIssueKey(connectionId, externalOrderId),
                            "ORDER_NOT_FOUND_AT_MARKETPLACE",
                            "Açık sipariş pazaryerinde bulunamadı; durumu değiştirilmedi, sonraki taramalarda yeniden sorulacak.",
                            cancellationToken);
                    continue;
                }
                TrackResultFailure(result.Error);
                var error = result.Error!;
                await RecordIssue(
                    tenantId,
                    $"order-lifecycle:{connectionId}:{externalOrderId}",
                    error.Code,
                    $"Sipariş yaşam döngüsü yenilenemedi; sonraki otomatik taramada tekrar denenecek. {error.SafeMessage}",
                    cancellationToken);
                continue;
            }
            TrackReceived();
            recoveredOrders.Add(WithHepsiburadaListedCancelTimes(result.Value!, hepsiburadaListedCancellations));
            await ResolveIssue(tenantId, $"order-lifecycle:{connectionId}:{externalOrderId}", cancellationToken);
            if (!isHepsiburada) await ResolveIssue(tenantId, OpenOrderMissingIssueKey(connectionId, externalOrderId), cancellationToken);
        }
        if (recoveredOrders.Count > 0) await UpsertOrders(tenantId, connectionId, recoveredOrders, cancellationToken);
        if (isHepsiburada)
            await ReconcileHepsiburadaPackageStatuses(tenantId, connectionId, correlationId, cancellationToken);

        cursor.LastModifiedWatermark = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Trendyol open packages (not delivered, cancelled or returned). Those of the last month are
    // read through Order V2 fifty at a time, grouped by storefront and date window; older open
    // packages once a day, one order at a time from the stream around their last change. The
    // order stream already brings every status change, so this is the safety net.
    // The scan runs in slices (audit 2026-10-20 Q1): the "orders" lock it shares with webhook ingests,
    // new-order reads and effect verifications is held for one slice of about twenty seconds, not for the
    // whole scan (up to twenty minutes). Each chunk's results are written and its position saved before the
    // next one, so a timeout loses one chunk instead of everything read; between slices the job is requeued
    // (Continue) and the waiting order jobs of the connection run first. Slicing keeps the shared, versioned
    // order projection and its lock; a separate lock with version-free writes would need a second projection.
    internal const string TrendyolOpenScanCursorPrefix = "trendyol-open-v1:";
    internal const int TrendyolOpenScanRecentChunk = 250;
    internal const int TrendyolOpenScanOlderChunk = 25;

    // ISLEYICI-12 (16 Nov 2026): the position stays a text cursor ("trendyol-open-v1:{recent|older}:{after}") in the
    // sync cursor's OpaqueCursor, as live cursors hold it today: a typed column would need a migration and a reader for
    // both forms, and any other value already starts a new scan.
    internal sealed record TrendyolOpenScanPosition(bool Older, string? After);

    internal static string TrendyolOpenScanCursor(TrendyolOpenScanPosition position) =>
        $"{TrendyolOpenScanCursorPrefix}{(position.Older ? "older" : "recent")}:{position.After}";

    // A position saved by this scan, or the start of a new scan (any other cursor value, null included).
    internal static TrendyolOpenScanPosition ReadTrendyolOpenScanCursor(string? value)
    {
        if (value is null || !value.StartsWith(TrendyolOpenScanCursorPrefix, StringComparison.Ordinal)) return new(false, null);
        var rest = value[TrendyolOpenScanCursorPrefix.Length..];
        var separator = rest.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0) return new(false, null);
        var phase = rest[..separator];
        var after = rest[(separator + 1)..];
        return phase switch
        {
            "older" => new(true, after.Length == 0 ? null : after),
            "recent" => new(false, after.Length == 0 ? null : after),
            _ => new(false, null)
        };
    }

    // The ids still to read in this phase, in a stable order, after the saved position.
    internal static IReadOnlyList<string> TrendyolOpenScanRemaining(IEnumerable<string> ids, string? after) =>
        [.. ids.Distinct(StringComparer.Ordinal).Where(id => after is null || string.CompareOrdinal(id, after) > 0).Order(StringComparer.Ordinal)];

    private TimeSpan TrendyolOpenScanSliceBudget() =>
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:OrderLifecycle:TrendyolSliceSeconds", 20), 5, 300));

    private async Task<bool> SyncTrendyolOpenPackages(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sliceStarted = Stopwatch.StartNew();
        var budget = TrendyolOpenScanSliceBudget();
        var cursor = await Cursor(tenantId, connectionId, "ORDER_LIFECYCLE", cancellationToken);
        var position = ReadTrendyolOpenScanCursor(cursor.OpaqueCursor);
        var open = await (from package in db.ShipmentPackages.AsNoTracking()
                          join order in db.Orders.AsNoTracking()
                              on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                          where package.TenantId == tenantId && package.ConnectionId == connectionId
                              && package.Status != ShipmentPackageStatus.Delivered
                              && package.Status != ShipmentPackageStatus.Cancelled
                              && package.Status != ShipmentPackageStatus.Returned
                              && package.ExternalPackageId != ""
                          select new { package.ExternalPackageId, order.ExternalOrderId, order.OrderedAt, order.LastRemoteModifiedAt, order.StoreFrontCode })
            .ToListAsync(cancellationToken);
        var v2Cutoff = now - TrendyolLookupPolicy.V2History;

        // Saves the chunk's results and the position, then hands the lock back when the slice is used up.
        async Task CheckpointAsync(List<RemoteOrder> received, TrendyolOpenScanPosition next)
        {
            if (received.Count > 0) await UpsertOrders(tenantId, connectionId, received, cancellationToken);
            cursor.OpaqueCursor = TrendyolOpenScanCursor(next);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            if (sliceStarted.Elapsed >= budget)
                throw new JobProcessingException(JobExecutionResult.Continue("Açık paket taraması kaldığı paketten birkaç saniye içinde devam edecek."));
        }

        // A "0" placeholder cannot be read by its package number: its order is read by order number in the daily pass
        // below, which brings the real package and closes the placeholder.
        if (!position.Older)
        {
            var recent = open.Where(package => package.OrderedAt >= v2Cutoff && !ShipmentPackageSupersession.IsPlaceholder(package.ExternalPackageId)).ToList();
            var byPackage = recent.GroupBy(package => package.ExternalPackageId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var chunk in TrendyolOpenScanRemaining(byPackage.Keys, position.After).Chunk(TrendyolOpenScanRecentChunk))
            {
                var received = new List<RemoteOrder>();
                foreach (var (packageIds, hint) in TrendyolLookupPolicy.PackageBatches(chunk.Select(id => byPackage[id]).Select(package => new TrendyolPackageRef(package.ExternalPackageId, package.OrderedAt, package.StoreFrontCode)), now))
                {
                    TrackRequest();
                    var batch = await orders.GetShipmentPackagesAsync(Context(tenantId, connectionId, correlationId, $"order-lifecycle-batch:{packageIds[0]}"), packageIds, hint, cancellationToken);
                    if (batch.IsSuccess)
                    {
                        foreach (var _ in batch.Value!) TrackReceived();
                        received.AddRange(batch.Value!);
                        await ResolveIssue(tenantId, $"order-lifecycle-batch:{connectionId}", cancellationToken);
                        continue;
                    }
                    if (batch.Error?.Class == AdapterErrorClass.RateLimit) { TrackResultFailure(batch.Error); throw JobProcessingException.FromAdapter(batch.Error); }
                    TrackResultFailure(batch.Error);
                    await RecordIssue(tenantId, $"order-lifecycle-batch:{connectionId}", batch.Error!.Code,
                        $"Açık Trendyol paketleri toplu okunamadı; sonraki taramada tekrar denenecek. {batch.Error.SafeMessage}", cancellationToken);
                }
                await CheckpointAsync(received, new(false, chunk[^1]));
            }
            position = new(true, null);
        }

        var olderCursor = await Cursor(tenantId, connectionId, "ORDER_LIFECYCLE_OLDER", cancellationToken);
        // The daily pass over older packages; one that is under way (a saved position) is finished first.
        if (position.After is not null || olderCursor.LastSuccessAt is not { } lastOlderRead || now - lastOlderRead >= TimeSpan.FromDays(1))
        {
            var older = open.Where(package => package.OrderedAt < v2Cutoff || ShipmentPackageSupersession.IsPlaceholder(package.ExternalPackageId)).GroupBy(package => package.ExternalOrderId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var chunk in TrendyolOpenScanRemaining(older.Keys, position.After).Chunk(TrendyolOpenScanOlderChunk))
            {
                var received = new List<RemoteOrder>();
                foreach (var order in chunk.Select(id => older[id]))
                {
                    TrackRequest();
                    var result = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-lifecycle:{order.ExternalOrderId}"), order.ExternalOrderId,
                        new OrderLookupHint(order.OrderedAt, order.LastRemoteModifiedAt, order.StoreFrontCode), cancellationToken);
                    if (result.IsSuccess)
                    {
                        TrackReceived();
                        received.Add(result.Value!);
                        await ResolveIssue(tenantId, $"order-lifecycle:{connectionId}:{order.ExternalOrderId}", cancellationToken);
                        continue;
                    }
                    if (result.Error?.Class == AdapterErrorClass.NotFound) continue;
                    if (result.Error?.Class == AdapterErrorClass.RateLimit) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error); }
                    TrackResultFailure(result.Error);
                    await RecordIssue(tenantId, $"order-lifecycle:{connectionId}:{order.ExternalOrderId}", result.Error!.Code,
                        $"Sipariş yaşam döngüsü yenilenemedi; sonraki otomatik taramada tekrar denenecek. {result.Error.SafeMessage}", cancellationToken);
                }
                await CheckpointAsync(received, new(true, chunk[^1]));
            }
            olderCursor.LastSuccessAt = now;
            olderCursor.Version++;
        }

        cursor.OpaqueCursor = null;
        cursor.LastModifiedWatermark = now;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal async Task<bool> ReconcileTrendyolCargoInfo(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cursor = await Cursor(tenantId, connectionId, "ORDER_CARGO_INFO", cancellationToken);
        var intervalSeconds = Math.Clamp(
            configuration.GetValue("MarketplaceSync:TrendyolCargoInfo:IntervalSeconds", (int)TimeSpan.FromMinutes(5).TotalSeconds),
            (int)TimeSpan.FromMinutes(1).TotalSeconds,
            (int)TimeSpan.FromDays(1).TotalSeconds);
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        if (!OpenOrderLifecyclePolicy.ShouldRunTrendyolCargoInfoReconciliation(cursor.LastAttemptAt, now, interval)) return true;

        var batchSize = Math.Clamp(configuration.GetValue("MarketplaceSync:TrendyolCargoInfo:BatchSize", 1), 1, 25);
        var cutoff = OpenOrderLifecyclePolicy.TrendyolCargoInfoLookbackCutoff(now);
        var candidates =
            from package in db.ShipmentPackages.AsNoTracking()
            join order in db.Orders.AsNoTracking()
                on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
            where package.TenantId == tenantId
                && package.ConnectionId == connectionId
                && package.Status == ShipmentPackageStatus.Delivered
                && package.StatusOccurredAt >= cutoff
                && package.ExternalPackageId != ""
                && (package.CargoProviderExternalId == null || package.CargoProviderExternalId == ""
                    || package.CargoTrackingNumber == null || package.CargoTrackingNumber == "")
            select new { package.Id, package.ExternalPackageId, order.OrderNumber, package.StatusOccurredAt };
        var distinctCandidates = candidates.Distinct();
        var candidateCount = await distinctCandidates.CountAsync(cancellationToken);
        var selectedCandidates = new List<(Guid PackageId, string ExternalPackageId, string OrderNumber, DateTimeOffset PackageStatusOccurredAt)>();
        var nextOffset = 0;
        if (candidateCount > 0)
        {
            var offset = int.TryParse(cursor.OpaqueCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var savedOffset)
                ? Math.Max(0, savedOffset) % candidateCount
                : 0;
            var takeCount = Math.Min(batchSize, candidateCount);
            var afterCount = Math.Min(takeCount, candidateCount - offset);
            var orderedCandidates = distinctCandidates.OrderBy(candidate => candidate.ExternalPackageId);
            var afterCandidates = await orderedCandidates
                .Skip(offset)
                .Take(afterCount)
                .Select(candidate => new { candidate.Id, candidate.ExternalPackageId, candidate.OrderNumber, candidate.StatusOccurredAt })
                .ToListAsync(cancellationToken);
            selectedCandidates.AddRange(afterCandidates.Select(candidate => (candidate.Id, candidate.ExternalPackageId, candidate.OrderNumber, candidate.StatusOccurredAt)));
            if (selectedCandidates.Count < takeCount)
            {
                var wrappedCandidates = await orderedCandidates
                    .Take(takeCount - selectedCandidates.Count)
                    .Select(candidate => new { candidate.Id, candidate.ExternalPackageId, candidate.OrderNumber, candidate.StatusOccurredAt })
                    .ToListAsync(cancellationToken);
                selectedCandidates.AddRange(wrappedCandidates.Select(candidate => (candidate.Id, candidate.ExternalPackageId, candidate.OrderNumber, candidate.StatusOccurredAt)));
            }
            nextOffset = (offset + selectedCandidates.Count) % candidateCount;
        }

        var refreshedPackageCount = 0;
        foreach (var candidate in selectedCandidates)
        {
            TrackRequest();
            var result = await orders.GetShipmentPackageAsync(
                Context(tenantId, connectionId, correlationId, $"trendyol-cargo-info:{candidate.ExternalPackageId}"),
                candidate.ExternalPackageId,
                new PackageLookup(candidate.PackageStatusOccurredAt, await OrderHint(tenantId, connectionId, candidate.OrderNumber, cancellationToken)),
                cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound || result.Error?.HttpStatus == 404) continue;
                TrackResultFailure(result.Error);
                await RecordIssue(
                    tenantId,
                    $"trendyol-cargo-info:{connectionId}:{candidate.ExternalPackageId}",
                    result.Error!.Code,
                    $"Trendyol kargo bilgileri yenilenemedi; sonraki uzlaştırmada tekrar denenecek. {result.Error.SafeMessage}",
                    cancellationToken);
                continue;
            }

            TrackReceived();
            var remotePackage = result.Value!;
            if (!string.Equals(remotePackage.ExternalOrderId.Trim(), candidate.OrderNumber.Trim(), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(remotePackage.Package.ExternalPackageId.Trim(), candidate.ExternalPackageId.Trim(), StringComparison.Ordinal))
            {
                await RecordIssue(
                    tenantId,
                    $"trendyol-cargo-info-owner:{connectionId}:{candidate.ExternalPackageId}",
                    "TRENDYOL_CARGO_INFO_ORDER_MISMATCH",
                    "Trendyol kargo yanıtı yerel sipariş numarasıyla eşleşmedi; paket bilgileri güncellenmedi.",
                    cancellationToken);
                continue;
            }
            await ResolveIssue(tenantId, $"trendyol-cargo-info:{connectionId}:{candidate.ExternalPackageId}", cancellationToken);
            await ResolveIssue(tenantId, $"trendyol-cargo-info-owner:{connectionId}:{candidate.ExternalPackageId}", cancellationToken);
            if (string.IsNullOrWhiteSpace(remotePackage.Package.CargoProviderExternalId)
                && string.IsNullOrWhiteSpace(remotePackage.Package.CargoTrackingNumber))
                continue;

            var localPackage = await db.ShipmentPackages.SingleAsync(
                package => package.TenantId == tenantId && package.ConnectionId == connectionId && package.Id == candidate.PackageId,
                cancellationToken);
            var changed = false;
            if (!string.IsNullOrWhiteSpace(remotePackage.Package.CargoProviderExternalId))
            {
                var provider = remotePackage.Package.CargoProviderExternalId.Trim();
                if (!string.Equals(localPackage.CargoProviderExternalId, provider, StringComparison.Ordinal))
                {
                    localPackage.CargoProviderExternalId = provider;
                    changed = true;
                }
            }
            if (!string.IsNullOrWhiteSpace(remotePackage.Package.CargoTrackingNumber))
            {
                var trackingNumber = remotePackage.Package.CargoTrackingNumber.Trim();
                if (!string.Equals(localPackage.CargoTrackingNumber, trackingNumber, StringComparison.Ordinal))
                {
                    localPackage.CargoTrackingNumber = trackingNumber;
                    changed = true;
                }
            }
            if (!changed) continue;
            localPackage.UpdatedAt = now;
            localPackage.Version++;
            refreshedPackageCount++;
        }

        if (refreshedPackageCount > 0) await db.SaveChangesAsync(cancellationToken);
        cursor.LastAttemptAt = now;
        cursor.LastSuccessAt = now;
        cursor.OpaqueCursor = candidateCount > 0 ? nextOffset.ToString(CultureInfo.InvariantCulture) : null;
        cursor.LastError = null;
        cursor.LastErrorAt = null;
        cursor.ConsecutiveFailureCount = 0;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ReconcileHepsiburadaPackageStatuses(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, HepsiburadaPackageStatusReconciliationPolicy.CursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var hasContinuation = !string.IsNullOrWhiteSpace(cursor.OpaqueCursor);
        var intervalSeconds = Math.Clamp(
            configuration.GetValue("MarketplaceSync:HepsiburadaPackageStatusReconciliation:IntervalSeconds", (int)HepsiburadaPackageStatusReconciliationPolicy.DefaultInterval.TotalSeconds),
            (int)TimeSpan.FromHours(1).TotalSeconds,
            (int)TimeSpan.FromDays(7).TotalSeconds);
        if (!HepsiburadaPackageStatusReconciliationPolicy.ShouldRun(cursor.LastSuccessAt, hasContinuation, now, TimeSpan.FromSeconds(intervalSeconds)))
            return;

        var batchSize = Math.Clamp(
            configuration.GetValue("MarketplaceSync:HepsiburadaPackageStatusReconciliation:BatchSize", HepsiburadaPackageStatusReconciliationPolicy.DefaultBatchSize),
            1,
            HepsiburadaPackageStatusReconciliationPolicy.MaximumBatchSize);
        var packageStatusCutoff = OpenOrderLifecyclePolicy.HepsiburadaPackageStatusHistoryCutoff(now);
        var offset = int.TryParse(cursor.OpaqueCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var savedOffset)
            ? Math.Max(0, savedOffset)
            : 0;
        var packageBatch = await (from package in db.ShipmentPackages.AsNoTracking()
                                  join order in db.Orders.AsNoTracking()
                                      on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                                  where package.TenantId == tenantId
                                      && package.ConnectionId == connectionId
                                      && package.ExternalPackageId != ""
                                      && !package.ExternalPackageId.StartsWith("order:")
                                      && (package.StatusOccurredAt >= packageStatusCutoff
                                          || package.Status != ShipmentPackageStatus.Delivered
                                              && package.Status != ShipmentPackageStatus.Cancelled
                                              && package.Status != ShipmentPackageStatus.Returned)
                                  orderby package.Id
                                  select new
                                  {
                                      package.Id,
                                      package.ExternalPackageId,
                                      package.Status,
                                      package.StatusOccurredAt,
                                      order.ExternalOrderId,
                                      order.OrderNumber
                                  })
            .Skip(offset)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (packageBatch.Count == 0)
        {
            cursor.OpaqueCursor = null;
            cursor.LastSuccessAt = now;
            cursor.LastError = null;
            cursor.LastErrorAt = null;
            cursor.ConsecutiveFailureCount = 0;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        foreach (var orderGroup in packageBatch.GroupBy(package => package.ExternalOrderId, StringComparer.Ordinal))
        {
            TrackRequest();
            var orderResult = await orders.GetAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-package-reconcile:{orderGroup.Key}"),
                orderGroup.Key,
                cancellationToken);
            if (!orderResult.IsSuccess)
            {
                if (orderResult.Error?.Class == AdapterErrorClass.NotFound || orderResult.Error?.HttpStatus == 404)
                {
                    await RecordIssue(tenantId,
                        $"hepsiburada-package-reconcile:{connectionId}:{orderGroup.Key}",
                        "HEPSIBURADA_PACKAGE_RECONCILIATION_ORDER_NOT_FOUND",
                        "Paket durumu yenilenirken Hepsiburada sipariş detayı bulunamadı; sonraki taramada yeniden denenecek.",
                        cancellationToken);
                    continue;
                }
                TrackResultFailure(orderResult.Error);
                throw JobProcessingException.FromAdapter(orderResult.Error!);
            }

            TrackReceived();
            var remoteOrder = orderResult.Value!;
            var expectedOrderNumber = orderGroup.First().OrderNumber;
            if (!string.IsNullOrWhiteSpace(remoteOrder.OrderNumber)
                && !string.Equals(remoteOrder.OrderNumber.Trim(), expectedOrderNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await RecordIssue(tenantId,
                    $"hepsiburada-package-reconcile-order-mismatch:{connectionId}:{orderGroup.Key}",
                    "HEPSIBURADA_PACKAGE_RECONCILIATION_ORDER_MISMATCH",
                    "Hepsiburada sipariş detayı yerel sipariş numarasıyla eşleşmedi; durum güncellenmedi.",
                    cancellationToken);
                continue;
            }
            await ResolveIssue(tenantId,
                $"hepsiburada-package-reconcile-order-mismatch:{connectionId}:{orderGroup.Key}",
                cancellationToken);

            var observations = new List<RemotePackage>();
            foreach (var localPackage in orderGroup)
            {
                var remotePackage = remoteOrder.Packages.FirstOrDefault(package =>
                    string.Equals(package.ExternalPackageId, localPackage.ExternalPackageId, StringComparison.Ordinal));
                var remoteStatus = remotePackage is null
                    ? ShipmentPackageStatus.ManualReview
                    : HepsiburadaShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus);
                string? rawStatus = remotePackage?.RawStatus;
                string? cargoCompany = remotePackage?.CargoProviderExternalId;
                string? trackingCode = remotePackage?.CargoTrackingNumber;
                string? cargoBarcode = remotePackage?.CargoBarcode;
                var occurredAt = remotePackage?.OccurredAt ?? now;

                // The order detail already read each of its packages' tracking info; a package is asked for on its
                // own only when the detail does not list it.
                if (remotePackage is null)
                {
                    TrackRequest();
                    var tracking = await orderPackages.GetPackageTrackingInfoAsync(
                        Context(tenantId, connectionId, correlationId, $"hepsiburada-package-reconcile:{localPackage.ExternalPackageId}"),
                        localPackage.ExternalPackageId,
                        cancellationToken);
                    if (!tracking.IsSuccess)
                    {
                        if (tracking.Error?.Class == AdapterErrorClass.NotFound || tracking.Error?.HttpStatus == 404)
                        {
                            await RecordIssue(tenantId,
                                $"hepsiburada-package-reconcile:{connectionId}:{localPackage.ExternalPackageId}",
                                "HEPSIBURADA_PACKAGE_RECONCILIATION_PACKAGE_NOT_FOUND",
                                "Hepsiburada paket takip detayı bulunamadı; mevcut panel kaydı korundu.",
                                cancellationToken);
                            continue;
                        }
                        TrackResultFailure(tracking.Error);
                        throw JobProcessingException.FromAdapter(tracking.Error!);
                    }
                    TrackReceived();
                    if (!string.IsNullOrWhiteSpace(tracking.Value!.OrderNumber)
                        && !string.Equals(tracking.Value.OrderNumber.Trim(), expectedOrderNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        await RecordIssue(tenantId,
                            $"hepsiburada-package-reconcile-owner:{connectionId}:{localPackage.ExternalPackageId}",
                            "HEPSIBURADA_PACKAGE_RECONCILIATION_ORDER_MISMATCH",
                            "Hepsiburada paket takip yanıtı yerel sipariş numarasıyla eşleşmedi; durum güncellenmedi.",
                            cancellationToken);
                        continue;
                    }

                    rawStatus = tracking.Value.Status;
                    cargoCompany = tracking.Value.CargoCompany;
                    trackingCode = tracking.Value.TrackingInfoCode;
                    cargoBarcode = tracking.Value.Barcode;
                    occurredAt = now;
                    remoteStatus = string.IsNullOrWhiteSpace(rawStatus)
                        ? ShipmentPackageStatus.ManualReview
                        : HepsiburadaShipmentPackageStatusPolicy.FromRemote(rawStatus);
                }

                if (remoteStatus == ShipmentPackageStatus.ManualReview || string.IsNullOrWhiteSpace(rawStatus))
                {
                    await RecordIssue(tenantId,
                        $"hepsiburada-package-reconcile-status:{connectionId}:{localPackage.ExternalPackageId}",
                        "HEPSIBURADA_PACKAGE_RECONCILIATION_STATUS_UNSUPPORTED",
                        "Hepsiburada paket yanıtındaki durum tanınmadı; mevcut panel durumu korundu.",
                        cancellationToken);
                    continue;
                }

                await ResolveIssue(tenantId,
                    $"hepsiburada-package-reconcile:{connectionId}:{localPackage.ExternalPackageId}",
                    cancellationToken);
                await ResolveIssue(tenantId,
                    $"hepsiburada-package-reconcile-owner:{connectionId}:{localPackage.ExternalPackageId}",
                    cancellationToken);
                await ResolveIssue(tenantId,
                    $"hepsiburada-package-reconcile-status:{connectionId}:{localPackage.ExternalPackageId}",
                    cancellationToken);

                // The package tracking read returns its current state but not
                // the original delivery-event date. Reuse the existing event
                // time when the state is already in sync; timestamp a changed
                // state as observed now instead of inventing a historical date.
                occurredAt = HepsiburadaPackageStatusReconciliationPolicy.StatusTimestamp(
                    localPackage.Status,
                    remoteStatus,
                    localPackage.StatusOccurredAt,
                    remotePackage?.OccurredAt,
                    now);

                observations.Add(new RemotePackage(
                    localPackage.ExternalPackageId,
                    remotePackage?.OriginExternalPackageId,
                    rawStatus,
                    occurredAt,
                    cargoCompany,
                    trackingCode,
                    [],
                    remotePackage?.GrossAmount ?? 0,
                    remotePackage?.DiscountAmount ?? 0,
                    remotePackage?.NetAmount ?? 0,
                    remotePackage?.Invoice,
                    remotePackage?.CreatedBy,
                    IsStatusObservation: true,
                    CargoBarcode: cargoBarcode));
            }

            await ResolveIssue(tenantId, $"hepsiburada-package-reconcile:{connectionId}:{orderGroup.Key}", cancellationToken);
            if (observations.Count > 0)
                await UpsertOrder(tenantId, connectionId, remoteOrder with { Packages = observations }, cancellationToken);
        }

        cursor.OpaqueCursor = (offset + packageBatch.Count).ToString(CultureInfo.InvariantCulture);
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }
}
