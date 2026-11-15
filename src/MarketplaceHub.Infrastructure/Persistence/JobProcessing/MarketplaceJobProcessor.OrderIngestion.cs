using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private sealed class OrderIngestionBatch
    {
        public Dictionary<string, Order> OrdersByExternalId { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, List<OrderLine>> LinesByOrder { get; } = [];
        public Dictionary<Guid, Dictionary<string, ShipmentPackage>> PackagesByOrder { get; } = [];
        public Dictionary<Guid, HashSet<string>> EventIdsByOrder { get; } = [];
        public Dictionary<string, PackageLineAllocation> AllocationsByKey { get; } = new(StringComparer.Ordinal);
        public OrderLineVariantCandidates VariantCandidates { get; set; } = OrderLineVariantCandidates.Empty;
        public List<(OrderLine Line, DateTimeOffset ModifiedAt)> ReservationSources { get; } = [];
        // Lines read before that found their variant only now (OrderLineLateMatchStockPolicy).
        public List<OrderLine> LateMatchedLines { get; } = [];
        // Lines tied to their variant in this read, which take its cost and desi (OrderLineCostSnapshot).
        public List<OrderLine> CostSnapshotLines { get; } = [];
    }

    private async Task UpsertOrders(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteOrder> remotes, CancellationToken cancellationToken)
    {
        if (remotes.Count == 0) return;
        // A long read projects page after page through one context. Rows this call loads are released
        // once the page is saved, so later SaveChanges calls do not walk every row read so far; rows the
        // caller already tracked (its cursor, its job) stay tracked.
        var trackedBefore = db.ChangeTracker.Entries().Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var isHepsiburada = platformCode == "HEPSIBURADA";
        // The stream is package-shaped: one order can occur once per package.
        // Merge the page before materializing the order so split-package line
        // quantities are summed instead of the last package overwriting them.
        // Shopify returns a complete order snapshot, including all fulfillments
        // and refunds. The Trendyol package merger only sums active package
        // quantities and would erase Shopify cancellation quantities, so the
        // newest Shopify snapshot is retained.
        var mergedRemotes = remotes
            .Where(remote => !string.IsNullOrWhiteSpace(remote.ExternalOrderId))
            .GroupBy(remote => remote.ExternalOrderId, StringComparer.Ordinal)
            .Select(group => isShopify
                ? group.OrderByDescending(remote => remote.LastModifiedAt).First()
                : isHepsiburada
                    ? MergeHepsiburadaOrderLines(group)
                : TrendyolJsonMapper.MergeOrderPackages(group, group.Key) ?? group.OrderByDescending(remote => remote.LastModifiedAt).First())
            .ToList();
        if (mergedRemotes.Count == 0) return;
        var conflictingPackages = OrderPackageIdentityGuard.FindConflicts(mergedRemotes);
        var conflictedOrderIds = conflictingPackages
            .SelectMany(conflict => new[] { conflict.FirstOrderId, conflict.ConflictingOrderId })
            .ToHashSet(StringComparer.Ordinal);
        foreach (var conflict in conflictingPackages)
            await RecordIssue(
                tenantId,
                $"order-package-identity:{connectionId}:{conflict.ExternalPackageId}",
                "ORDER_PACKAGE_ID_CONFLICT",
                $"Paket kimliği {conflict.ExternalPackageId} birden fazla siparişe bağlandı; çakışan siparişler bu taramada uygulanmadı.",
                cancellationToken);

        var remotePackageOwners = mergedRemotes
            .SelectMany(remote => remote.Packages.Select(package => new { package.ExternalPackageId, remote.ExternalOrderId }))
            // Trendyol's "0" is every awaiting-payment order's number until its real package exists, owned by no one
            // order: it is never a conflict, and the order's own "0" row is superseded once the real package arrives.
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalPackageId) && !ShipmentPackageSupersession.IsPlaceholder(x.ExternalPackageId))
            .GroupBy(x => x.ExternalPackageId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().ExternalOrderId, StringComparer.Ordinal);
        var candidatePackageIds = remotePackageOwners.Keys.ToArray();
        if (candidatePackageIds.Length > 0)
        {
            var persistedPackageOwners = await (from package in db.ShipmentPackages.AsNoTracking()
                                                join order in db.Orders.AsNoTracking()
                                                    on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                                                where package.TenantId == tenantId
                                                    && package.ConnectionId == connectionId
                                                    && package.ExternalPackageId != ShipmentPackageSupersession.PlaceholderExternalId
                                                    && candidatePackageIds.Contains(package.ExternalPackageId)
                                                select new { package.ExternalPackageId, order.ExternalOrderId })
                .ToListAsync(cancellationToken);
            foreach (var owner in persistedPackageOwners)
            {
                if (!remotePackageOwners.TryGetValue(owner.ExternalPackageId, out var remoteOrderId)
                    || string.Equals(owner.ExternalOrderId, remoteOrderId, StringComparison.Ordinal)) continue;
                conflictedOrderIds.Add(remoteOrderId);
                await RecordIssue(
                    tenantId,
                    $"order-package-identity:{connectionId}:{owner.ExternalPackageId}",
                    "ORDER_PACKAGE_ID_CONFLICT",
                    $"Paket kimliği {owner.ExternalPackageId} mevcut siparişle çakıştı; çakışan sipariş bu taramada uygulanmadı.",
                    cancellationToken);
            }
        }
        if (conflictedOrderIds.Count > 0)
            mergedRemotes = mergedRemotes.Where(remote => !conflictedOrderIds.Contains(remote.ExternalOrderId)).ToList();
        if (mergedRemotes.Count == 0) return;
        var batch = new OrderIngestionBatch();
        var externalIds = mergedRemotes.Select(x => x.ExternalOrderId).ToArray();
        var orders = await db.Orders.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalOrderId)).ToListAsync(cancellationToken);
        foreach (var order in orders) batch.OrdersByExternalId[order.ExternalOrderId] = order;
        var orderIds = orders.Select(x => x.Id).ToArray();
        if (orderIds.Length > 0)
        {
            var lines = await db.OrderLines.Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
            foreach (var group in lines.GroupBy(x => x.OrderId)) batch.LinesByOrder[group.Key] = group.ToList();

            var packages = await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
            foreach (var group in packages.GroupBy(x => x.OrderId)) batch.PackagesByOrder[group.Key] = group.ToDictionary(x => x.ExternalPackageId, StringComparer.Ordinal);

            var packageIds = packages.Select(x => x.Id).ToArray();
            if (packageIds.Length > 0)
            {
                var allocations = await db.PackageLineAllocations.AsNoTracking().Where(x => x.TenantId == tenantId && packageIds.Contains(x.PackageId)).ToListAsync(cancellationToken);
                foreach (var (key, allocation) in AllocationsByLine(allocations)) batch.AllocationsByKey[key] = allocation;
            }

            var history = await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).Select(x => new { x.OrderId, x.SourceEventId }).ToListAsync(cancellationToken);
            foreach (var group in history.GroupBy(x => x.OrderId)) batch.EventIdsByOrder[group.Key] = group.Select(x => x.SourceEventId).ToHashSet(StringComparer.Ordinal);
        }

        batch.VariantCandidates = await LoadOrderLineVariantCandidates(tenantId, connectionId, OrderLineCodesOf(platformCode, remotes.SelectMany(x => x.Lines)), cancellationToken);
        foreach (var remote in mergedRemotes) await UpsertOrder(tenantId, connectionId, remote, cancellationToken, batch, saveChanges: false, persistFinancialObservations: isShopify);
        await AdoptLateMatchedLineStock(tenantId, connectionId, batch.LateMatchedLines, cancellationToken);
        await OrderLineCostSnapshot.TakeAsync(db, tenantId, batch.CostSnapshotLines, cancellationToken);
        await ProjectOrderReservations(tenantId, connectionId, batch.ReservationSources, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Unchanged && !trackedBefore.Contains(entry.Entity)).ToList())
            entry.State = EntityState.Detached;
    }

    // Scan C-13 (13 Nov 2026): a line the page carries more than once took the alphabetically last status ("Shipped"
    // over "Delivered") and its order's discount was added once per copy. Each line now comes from the most recently
    // modified copy, and a copy whose lines a newer copy already carried adds no discount of its own.
    internal static RemoteOrder MergeHepsiburadaOrderLines(IEnumerable<RemoteOrder> candidates)
    {
        var matches = candidates.OrderByDescending(remote => remote.LastModifiedAt).ToArray();
        var latest = matches[0];
        var lines = matches
            .SelectMany(remote => remote.Lines)
            .GroupBy(line => line.ExternalLineId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var seenLines = new HashSet<string>(StringComparer.Ordinal);
        var discountSources = new List<RemoteOrder>();
        foreach (var match in matches)
        {
            var newLines = match.Lines.Count(line => seenLines.Add(line.ExternalLineId));
            if (newLines > 0 || match.Lines.Count == 0) discountSources.Add(match);
        }
        var packages = matches
            .SelectMany(remote => remote.Packages)
            .GroupBy(package => package.ExternalPackageId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(package => package.OccurredAt).First())
            .ToArray();
        var gross = lines.Sum(line => line.UnitPrice * line.Quantity);
        var discount = Math.Min(gross, discountSources.Sum(remote => remote.DiscountAmount));
        return latest with
        {
            LastModifiedAt = matches.Max(remote => remote.LastModifiedAt),
            GrossAmount = gross,
            DiscountAmount = discount,
            NetAmount = Math.Max(0m, gross - discount),
            Lines = lines,
            Packages = packages
        };
    }

    private async Task<bool> UpsertOrderCore(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionBatch? batch, bool saveChanges, bool persistFinancialObservations)
    {
        // Read once per connection (ConnectionPlatformCode), not once per order of a page (ISLEYICI-8).
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var isHepsiburada = platformCode == "HEPSIBURADA";
        var isTrendyol = platformCode == "TRENDYOL";
        // n11 copies Trendyol's package model (Created, Picking, UnPacked after a split); the Pazarama
        // and PTT AVM mappers translate their item statuses to the same names.
        var usesTrendyolPackageStatuses = isTrendyol || platformCode is "N11" or "PAZARAMA" or "PTTAVM";
        var now = timeProvider.GetUtcNow();
        var order = batch?.OrdersByExternalId.GetValueOrDefault(remote.ExternalOrderId)
            ?? await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.ExternalOrderId == remote.ExternalOrderId, cancellationToken);

        if (isHepsiburada && batch is null && order is not null && remote.Lines.Count == 0)
        {
            var persistedLines = await db.OrderLines.AsNoTracking()
                .Where(line => line.TenantId == tenantId && line.OrderId == order.Id)
                .OrderBy(line => line.ExternalLineId)
                .Select(line => new RemoteOrderLine(
                    line.ExternalLineId,
                    line.Sku,
                    line.Barcode,
                    line.TitleSnapshot,
                    line.OrderedQuantity,
                    line.UnitPrice,
                    line.VatRate,
                    line.RawStatus,
                    line.SourceSnapshotJson ?? "{}",
                    line.CancelledQuantity))
                .ToListAsync(cancellationToken);
            if (HepsiburadaPackageStatusReconciliationPolicy.TryHydrateStatusObservationLines(remote, persistedLines, out var hydratedOrder))
                remote = hydratedOrder;
        }

        IReadOnlyDictionary<string, decimal> remoteLineQuantities;
        // A package whose every product line was unreadable is still stored for shipping, with no lines (UnreadableOrderLines).
        var keptWithUnreadableLines = remote.Packages.Any(package => package.UnreadableLineCount > 0);
        if ((!isTrendyol && remote.Lines.Count == 0 && !keptWithUnreadableLines) || remote.Packages.Count == 0 && !isHepsiburada)
        {
            await NoteRefusedOrderAsync(tenantId, connectionId, remote, isTrendyol, order is null, cancellationToken);
            await RecordIssue(tenantId, $"order-contract:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_CONTRACT_INVALID", "Pazar yeri siparişinde satır veya paket verisi eksikti; eksik sipariş projeksiyonu uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        else
        {
            remoteLineQuantities = new Dictionary<string, decimal>(StringComparer.Ordinal);
            if (remote.Lines.Count > 0 && !PackageIngestionSafety.TryGetOrderedQuantities(remote.Lines, out remoteLineQuantities))
            {
                await NoteRefusedOrderAsync(tenantId, connectionId, remote, isTrendyol, order is null, cancellationToken);
                await RecordIssue(tenantId, $"order-lines:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_QUANTITY_INVARIANT_REJECTED", "Sipariş satır kimliği veya miktarı geçersizdi; olayın hiçbir parçası uygulanmadı.", cancellationToken);
                if (saveChanges) await db.SaveChangesAsync(cancellationToken);
                return false;
            }
        }
        // O8: a split parent is known by its children naming it as their origin, in this read or among the order's stored
        // packages, whatever status Trendyol lists it under (live: 4084999185 and three more, read as their own packages
        // after the split and refused as PACKAGE_QUANTITY_INVARIANT_REJECTED on every run). Its lines live on in the
        // children; a read that brings nothing but known parents of a stored order changes nothing and is skipped quietly.
        var splitParentIds = usesTrendyolPackageStatuses
            ? await KnownSplitParentIdsAsync(tenantId, connectionId, order, remote.Packages, batch, cancellationToken)
            : new HashSet<string>(StringComparer.Ordinal);
        if (order is not null && remote.Packages.Count > 0 && remote.Packages.All(package => splitParentIds.Contains(package.ExternalPackageId)))
        {
            foreach (var parent in remote.Packages) await ResolvePackageQuantityIssues(tenantId, connectionId, parent.ExternalPackageId, cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        // A package kept without a product line it could not read is stored (it still has to be shipped), and its order
        // joins the re-read list (RejectedOrderRereadPolicy) so a later read can fill the line in; the line is never
        // guessed meanwhile and the order gets no invoice (InvoiceIssuePolicy.OrderLineUnreadable). Every marketplace.
        if (remote.Packages.Any(package => package.UnreadableLineCount > 0)) rereadOrderIds.Add(remote.ExternalOrderId);
        var detailedPackages = remote.Packages.Where(package => !package.IsStatusObservation).ToArray();
        var allocatedLineIds = detailedPackages.SelectMany(x => x.Allocations).Select(x => x.ExternalLineId).ToHashSet(StringComparer.Ordinal);
        if (remoteLineQuantities.Count > 0 && detailedPackages.Length > 0 && remoteLineQuantities.Keys.Any(lineId => !allocatedLineIds.Contains(lineId)))
        {
            await NoteRefusedOrderAsync(tenantId, connectionId, remote, isTrendyol, order is null, cancellationToken);
            await RecordIssue(tenantId, $"order-coverage:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_COVERAGE_INVALID", "Pazar yeri cevabındaki sipariş satırlarının tamamı paket tahsisinde yer almıyordu; eksik veri uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        // A new order must arrive as a complete package aggregate. Once the
        // order already exists, a later stream page may contain only one
        // sibling package; that fragment is validated per package below and
        // merged with the persisted projection instead of being rejected as an
        // incomplete order.
        // Split parents (UnPacked) carry no quantities of their own and are left out of the check, as below.
        var quantityPackages = usesTrendyolPackageStatuses
            ? detailedPackages.Where(package => !ShipmentPackageSupersession.IsSplitParentRawStatus(package.RawStatus) && !splitParentIds.Contains(package.ExternalPackageId)).ToArray()
            : detailedPackages;
        if (order is null && remoteLineQuantities.Count > 0 && quantityPackages.Length > 0 && !PackageIngestionSafety.TryNormalizeOrder(remoteLineQuantities, quantityPackages, out _))
        {
            var rejectedEventId = remote.Packages.Count > 0
                ? PackageIngestionSafety.EventId(remote.Packages[0].ExternalPackageId, remote.Packages[0].OccurredAt)
                : remote.ExternalOrderId;
            await NoteRefusedOrderAsync(tenantId, connectionId, remote, isTrendyol, order is null, cancellationToken);
            await RecordIssue(tenantId, $"package-quantity:{connectionId}:{rejectedEventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Paket miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        // The packages this read moves out of ManualReview by their raw status now known: their lines take the read's
        // quantities below (ApplyReadLineQuantities), or a package that turned Returned kept its lines as sold.
        var repairedFromReview = new HashSet<Guid>();
        if (order is not null)
        {
            var repairCandidates = batch is not null
                ? (batch.PackagesByOrder.GetValueOrDefault(order.Id)?.Values.Where(x => x.Status == ShipmentPackageStatus.ManualReview).ToList() ?? [])
                : await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status == ShipmentPackageStatus.ManualReview).ToListAsync(cancellationToken);
            foreach (var candidate in repairCandidates) { var canonical = usesTrendyolPackageStatuses ? TrendyolShipmentPackageStatusPolicy.FromRemote(candidate.RawStatus) : ShipmentPackageStatusPolicy.FromRemote(candidate.RawStatus); if (canonical != ShipmentPackageStatus.ManualReview) { candidate.Status = canonical; candidate.UpdatedAt = now; candidate.Version++; repairedFromReview.Add(candidate.Id); } }
        }
        // Do not short-circuit empty-line replays: the same remote package can need a safe local canonical projection repair after a previously unknown raw status becomes recognized.
        var orderIsFresh = order is null
            || remote.LastModifiedAt >= order.LastRemoteModifiedAt
            || TrendyolJsonMapper.IsReturnClaimReadModelSnapshot(order.CustomerSnapshotJson);
        if (!orderIsFresh) telemetrySkippedCount++;
        if (order is null) { order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = remote.ExternalOrderId, OrderNumber = remote.OrderNumber, Currency = remote.Currency, CustomerSnapshotJson = remote.CustomerSnapshotJson, ShipmentAddressSnapshotJson = remote.ShipmentAddressSnapshotJson, InvoiceAddressSnapshotJson = remote.InvoiceAddressSnapshotJson, DerivedStatus = "NEW", ShipmentDueAt = remote.ShipmentDueAt, CreatedAt = now, Version = 1 }; db.Orders.Add(order); batch?.OrdersByExternalId.TryAdd(remote.ExternalOrderId, order); telemetryInsertedCount++; }
        // An order never changes storefront; the first read that names it is kept for later targeted reads.
        if (order.StoreFrontCode is null && !string.IsNullOrWhiteSpace(remote.StoreFrontCode)) order.StoreFrontCode = remote.StoreFrontCode;
        var isNewOrder = db.Entry(order).State == EntityState.Added;
        if (isNewOrder) await ResolveOrderRefusalsAsync(tenantId, connectionId, remote, cancellationToken);
        var previousAmounts = (order.GrossAmount, order.DiscountAmount, order.NetAmount);
        if (orderIsFresh)
        {
            var customerSnapshot = remote.CustomerSnapshotJson;
            var shipmentAddressSnapshot = remote.ShipmentAddressSnapshotJson;
            var invoiceAddressSnapshot = remote.InvoiceAddressSnapshotJson;
            var grossAmount = remote.GrossAmount;
            var discountAmount = remote.DiscountAmount;
            var netAmount = remote.NetAmount;
            if (isShopify && ShopifyOrderCsvSnapshotPolicy.HasImport(order.CustomerSnapshotJson))
            {
                customerSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(customerSnapshot, order.CustomerSnapshotJson);
                shipmentAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(shipmentAddressSnapshot, order.ShipmentAddressSnapshotJson);
                invoiceAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(invoiceAddressSnapshot, order.InvoiceAddressSnapshotJson);
                (grossAmount, discountAmount, netAmount) = ShopifyOrderCsvSnapshotPolicy.MergeRemoteAmounts(
                    order.CustomerSnapshotJson, remote.GrossAmount, remote.DiscountAmount, remote.NetAmount);
            }
            else if (isHepsiburada)
            {
                // Keep authoritative invoice/cargo observations when an order
                // list read omits the detail-only fields.
                customerSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(customerSnapshot, order.CustomerSnapshotJson);
                shipmentAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(shipmentAddressSnapshot, order.ShipmentAddressSnapshotJson);
                invoiceAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(invoiceAddressSnapshot, order.InvoiceAddressSnapshotJson);
            }
            order.OrderNumber = remote.OrderNumber; order.Currency = remote.Currency; order.GrossAmount = grossAmount; order.DiscountAmount = discountAmount; order.NetAmount = netAmount; order.OrderedAt = remote.OrderedAt; order.ShipmentDueAt = remote.ShipmentDueAt; order.LastRemoteModifiedAt = remote.LastModifiedAt; order.CustomerSnapshotJson = customerSnapshot; order.ShipmentAddressSnapshotJson = shipmentAddressSnapshot; order.InvoiceAddressSnapshotJson = invoiceAddressSnapshot; order.UpdatedAt = now; if (db.Entry(order).State != EntityState.Added) { order.Version++; telemetryUpdatedCount++; }
        }
        if (persistFinancialObservations && orderIsFresh)
            await UpsertShopifyFinancialObservations(tenantId, order.Id, remote, cancellationToken);
        var existingLines = batch is not null
            ? batch.LinesByOrder.GetValueOrDefault(order.Id) ?? []
            : await db.OrderLines.Where(x => x.TenantId == tenantId && x.OrderId == order.Id).ToListAsync(cancellationToken);
        if (!orderIsFresh && isHepsiburada)
            telemetryUpdatedCount += HepsiburadaStaleOrderEnrichmentPolicy.Apply(order, existingLines, remote, now);
        // The order's lines by external line id (ISLEYICI-12: one dictionary, kept up to date as lines are matched).
        var lines = existingLines
            .GroupBy(x => x.ExternalLineId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var linesBySnapshot = existingLines
            .Where(x => !string.IsNullOrWhiteSpace(x.SourceSnapshotJson) && x.SourceSnapshotJson != "{}")
            .GroupBy(x => x.SourceSnapshotJson!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var packagesByExternalId = batch is not null
            ? batch.PackagesByOrder.GetValueOrDefault(order.Id) ?? new Dictionary<string, ShipmentPackage>(StringComparer.Ordinal)
            : await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.OrderId == order.Id).ToDictionaryAsync(x => x.ExternalPackageId, StringComparer.Ordinal, cancellationToken);
        var knownEventIds = batch is not null
            ? batch.EventIdsByOrder.GetValueOrDefault(order.Id) ?? new HashSet<string>(StringComparer.Ordinal)
            : (await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => x.SourceEventId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var localEvent in db.OrderStatusHistory.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id)) knownEventIds.Add(localEvent.SourceEventId);
        var packageIds = packagesByExternalId.Values.Select(x => x.Id).ToArray();
        var allocationsByKey = batch is not null
            ? batch.AllocationsByKey.Where(x => packageIds.Contains(x.Value.PackageId)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
            : AllocationsByLine(await db.PackageLineAllocations.AsNoTracking().Where(x => x.TenantId == tenantId && packageIds.Contains(x.PackageId)).ToListAsync(cancellationToken));
        var variantCandidates = batch?.VariantCandidates ?? await LoadOrderLineVariantCandidates(tenantId, connectionId, OrderLineCodesOf(platformCode, remote.Lines), cancellationToken);
        var lateMatchedLines = batch?.LateMatchedLines ?? [];
        var costSnapshotLines = batch?.CostSnapshotLines ?? [];
        if (batch is not null)
        {
            batch.LinesByOrder[order.Id] = existingLines;
            batch.PackagesByOrder[order.Id] = packagesByExternalId;
            batch.EventIdsByOrder[order.Id] = knownEventIds;
        }
        foreach (var remoteLine in remote.Lines)
        {
            if (!orderIsFresh && (!isHepsiburada
                || !HepsiburadaOrderLineMatcher.CanAddMissingLine(remoteLine, existingLines, remote.Lines)))
                continue;
            var line = lines.GetValueOrDefault(remoteLine.ExternalLineId);
            if (line is null && isHepsiburada)
                line = HepsiburadaOrderLineMatcher.FindExistingLine(remoteLine, existingLines, remote.Lines);
            if (line is null && !string.IsNullOrWhiteSpace(remoteLine.SourceSnapshotJson) && remoteLine.SourceSnapshotJson != "{}") line = linesBySnapshot.GetValueOrDefault(remoteLine.SourceSnapshotJson);
            if (line is null) { line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = remoteLine.ExternalLineId, Sku = remoteLine.Sku, TitleSnapshot = remoteLine.Title, RawStatus = remoteLine.RawStatus, Version = 1 }; db.OrderLines.Add(line); existingLines.Add(line); telemetryInsertedCount++; }

            if (line.VariantId is null
                && variantCandidates.Match(OrderLineVariantMatcher.Codes(platformCode, remoteLine.Sku, remoteLine.Barcode, remoteLine.SourceSnapshotJson)).VariantId is { } matchedVariantId)
            {
                line.VariantId = matchedVariantId;
                costSnapshotLines.Add(line);
                // A line stored by an earlier read takes its stock as a late match (OrderLineLateMatchStockPolicy).
                if (db.Entry(line).State != EntityState.Added) lateMatchedLines.Add(line);
            }
            var orderedQuantity = db.Entry(line).State == EntityState.Added
                ? remoteLine.Quantity
                : Math.Max(line.OrderedQuantity, remoteLine.Quantity);
            var importedUnitPrice = isShopify && remoteLine.UnitPrice == 0 && ShopifyOrderCsvSnapshotPolicy.HasImport(line.SourceSnapshotJson)
                ? ShopifyOrderCsvSnapshotPolicy.ImportedAmount(line.SourceSnapshotJson, "unitPrice")
                : null;
            var sku = isShopify ? ShopifyOrderCsvSnapshotPolicy.PreserveRemoteSku(remoteLine.Sku, line) ?? line.Sku : remoteLine.Sku;
            var title = isShopify ? ShopifyOrderCsvSnapshotPolicy.PreserveRemoteTitle(remoteLine.Title, line) : remoteLine.Title;
            // Hepsiburada order details may omit the image field present in
            // paid-order/package lists. Keep previously observed line data
            // when a later read does not repeat it.
            var sourceSnapshot = isShopify || isHepsiburada
                ? ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(remoteLine.SourceSnapshotJson, line.SourceSnapshotJson)
                : remoteLine.SourceSnapshotJson;
            // A read without the line's VAT rate (a Hepsiburada claim, say) keeps the rate an earlier read reported; a new
            // line without one stays at 0 and the invoice gate stops it (InvoiceIssuePolicy.IsLineVatRateUnknown).
            var previousExternalLineId = line.ExternalLineId;
            if (!string.Equals(previousExternalLineId, remoteLine.ExternalLineId, StringComparison.Ordinal))
                lines.Remove(previousExternalLineId);
            line.ExternalLineId = remoteLine.ExternalLineId;
            line.Sku = sku; line.Barcode = remoteLine.Barcode ?? line.Barcode; line.TitleSnapshot = title; line.SourceSnapshotJson = sourceSnapshot; line.OrderedQuantity = orderedQuantity; line.UnitPrice = importedUnitPrice ?? remoteLine.UnitPrice; line.PlatformDiscount = remoteLine.PlatformDiscount ?? line.PlatformDiscount; line.VatRate = remoteLine.VatRate ?? line.VatRate; line.RawStatus = remoteLine.RawStatus; if (db.Entry(line).State != EntityState.Added) line.Version++; lines[remoteLine.ExternalLineId] = line;
            if (!string.IsNullOrWhiteSpace(remoteLine.SourceSnapshotJson) && remoteLine.SourceSnapshotJson != "{}") linesBySnapshot[remoteLine.SourceSnapshotJson] = line;
        }
        var externalLineIdsById = lines.Values.ToDictionary(line => line.Id, line => line.ExternalLineId);
        foreach (var remotePackage in remote.Packages)
        {
            var target = usesTrendyolPackageStatuses ? TrendyolShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus) : isShopify ? ShopifyShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus) : isHepsiburada ? HepsiburadaShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus) : ShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus); var eventId = PackageIngestionSafety.EventId(remotePackage.ExternalPackageId, remotePackage.OccurredAt); var orderedQuantities = lines.ToDictionary(x => x.Key, x => x.Value.OrderedQuantity, StringComparer.Ordinal);
            var package = packagesByExternalId.GetValueOrDefault(remotePackage.ExternalPackageId);
            // The time a status is ordered by and stored with. Without a status time from the marketplace (PTT AVM),
            // OccurredAt only names the event; the package keeps the time the status was first seen (audit S4).
            var statusAt = PackageIngestionSafety.StatusTime(remotePackage, package?.StatusOccurredAt, now);
            // A package the panel unpacked is history; a late read of it does not bring it back.
            if (package is not null && ShipmentPackageSupersession.IsLocallyUnpacked(package.RawStatus)) continue;
            // Trendyol's package read closing an undelivered (claim-built) package as returned is applied although its
            // modification time is older than the claim time the package holds; the return is dated no earlier than the
            // status it ends, so the history stays in order.
            if (isTrendyol && package is not null && statusAt < package.StatusOccurredAt
                && PackageIngestionSafety.ClosesReturnRegardlessOfTime(package.Status, target, remotePackage.IsStatusObservation))
                statusAt = package.StatusOccurredAt;
            var legacyTrendyolHoldCorrection = isTrendyol
                && package is not null
                && package.Status == ShipmentPackageStatus.OnHold
                && TrendyolShipmentPackageStatusPolicy.HasSameNormalizedStatus(package.RawStatus, remotePackage.RawStatus)
                && target != ShipmentPackageStatus.OnHold
                && TrendyolShipmentPackageStatusPolicy.IsLegacyOnHoldProjection(remotePackage.RawStatus);
            // A Trendyol split parent (UnPacked) carries no quantities of its own: its lines live on in the child
            // packages, and its listed lines can exceed what the order still holds. Its lines are not checked or
            // allocated (the line projection skips split parents too), and a refusal it caused before is resolved.
            var isUnpackedParent = (usesTrendyolPackageStatuses || isHepsiburada) && ShipmentPackageSupersession.IsSplitParentRawStatus(remotePackage.RawStatus);
            var isSplitParent = isUnpackedParent || splitParentIds.Contains(remotePackage.ExternalPackageId);
            if (isSplitParent) await ResolvePackageQuantityIssues(tenantId, connectionId, remotePackage.ExternalPackageId, cancellationToken);
            // A parent known only by its children (O8), not stored: nothing of it is the order's any more; it is not opened.
            if (isSplitParent && !isUnpackedParent && package is null) continue;
            var packageAllocations = isSplitParent ? [] : remotePackage.Allocations;
            if (!isSplitParent && remotePackage.IsStatusObservation && packageAllocations.Count == 0 && package is not null)
            {
                packageAllocations = allocationsByKey.Values
                    .Where(allocation => allocation.PackageId == package.Id && externalLineIdsById.ContainsKey(allocation.OrderLineId))
                    .GroupBy(allocation => allocation.OrderLineId)
                    .Select(group => group.OrderByDescending(allocation => allocation.SourceEventId, StringComparer.Ordinal).First())
                    .Select(allocation =>
                    {
                        var activeQuantity = allocation.AllocatedQuantity;
                        var shippedQuantity = target is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered
                            ? activeQuantity
                            : allocation.ShippedQuantity;
                        var deliveredQuantity = target == ShipmentPackageStatus.Delivered ? activeQuantity : allocation.DeliveredQuantity;
                        return new RemotePackageAllocation(externalLineIdsById[allocation.OrderLineId], activeQuantity, allocation.CancelledQuantity, shippedQuantity, deliveredQuantity, allocation.ReturnedQuantity);
                    })
                    .ToArray();
            }
            IReadOnlyDictionary<string, NormalizedPackageAllocation> safeAllocations = new Dictionary<string, NormalizedPackageAllocation>(StringComparer.Ordinal);
            if (!isSplitParent && (packageAllocations.Count > 0 || !remotePackage.IsStatusObservation))
            {
                if (!PackageIngestionSafety.TryNormalizeAll(orderedQuantities, packageAllocations, target, out safeAllocations))
                {
                    if (isTrendyol || isNewOrder) rereadOrderIds.Add(remote.ExternalOrderId);
                    await RecordIssue(tenantId, $"package-quantity:{connectionId}:{eventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Package miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken);
                    continue;
                }
            }
            var packageObservation = remotePackage with { Allocations = packageAllocations };
            // The amounts are not a status: a fresh detailed read refreshes them also when its status event is
            // already recorded or refused, so the order totals below are rebuilt from what the marketplace says now.
            if (package is not null && usesTrendyolPackageStatuses && orderIsFresh && PackageAmountPolicy.Apply(package, remotePackage))
            {
                package.UpdatedAt = now;
                if (db.Entry(package).State != EntityState.Added) package.Version++;
                telemetryUpdatedCount++;
            }
            // Like the amounts, the package's unreadable lines follow every fresh detailed read, whatever its status event.
            if (package is not null && orderIsFresh && await ApplyUnreadableLines(tenantId, connectionId, platformCode, order.OrderNumber, package, remotePackage, cancellationToken))
            {
                package.UpdatedAt = now;
                if (db.Entry(package).State != EntityState.Added) package.Version++;
                telemetryUpdatedCount++;
            }
            if (package is not null) await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken, isTrendyol ? "stream" : null);
            if (package is not null && package.Status == ShipmentPackageStatus.ManualReview && package.RawStatus == remotePackage.RawStatus && target != ShipmentPackageStatus.ManualReview) { package.Status = target; package.UpdatedAt = now; package.Version++; ApplyReadLineQuantities(tenantId, package, remotePackage, packageObservation.Allocations, safeAllocations, lines, allocationsByKey, eventId); continue; }
            // An observation that left the status unchanged writes no history row (below); its replay is still
            // known as applied by the line allocations it stored under the same event id.
            var eventAlreadyRecorded = knownEventIds.Contains(eventId)
                || package is not null && package.Status == target && string.Equals(package.RawStatus, remotePackage.RawStatus, StringComparison.Ordinal)
                    && allocationsByKey.Values.Any(allocation => allocation.PackageId == package.Id && allocation.SourceEventId == eventId);
            if (eventAlreadyRecorded)
            {
                // Shopify can enrich the same fulfillment event later with
                // delivery information while keeping its original createdAt.
                // Treat that as an idempotent status enrichment, not as a
                // reason to discard the authoritative forward transition.
                var statusChanged = package is not null
                    && (legacyTrendyolHoldCorrection || (isHepsiburada
                        ? HepsiburadaPackageStatusReconciliationPolicy.ShouldAcceptStatusObservation(
                            package.Status,
                            package.StatusOccurredAt,
                            target,
                            statusAt,
                            remotePackage.IsStatusObservation)
                        : PackageIngestionSafety.ShouldAccept(package.Status, package.StatusOccurredAt, target, statusAt, remotePackage.RawStatus)))
                    && package.Status != target;
                var packageMetadataChanged = package is not null
                    && ((!string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) && package.CargoProviderExternalId != remotePackage.CargoProviderExternalId)
                        || (!string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) && package.CargoTrackingNumber != remotePackage.CargoTrackingNumber)
                        || (!string.IsNullOrWhiteSpace(remotePackage.CargoBarcode) && package.CargoBarcode != remotePackage.CargoBarcode.Trim()));
                if (statusChanged && package is not null)
                {
                    package.Status = target;
                    package.RawStatus = remotePackage.RawStatus;
                    package.StatusOccurredAt = statusAt;
                    PackageAmountPolicy.Apply(package, remotePackage);

                    foreach (var remoteAllocation in packageObservation.Allocations)
                    {
                        if (!lines.TryGetValue(remoteAllocation.ExternalLineId, out var line)
                            || !safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)) continue;
                        UpsertLineAllocation(tenantId, package.Id, line.Id, eventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, safe.ShippedQuantity, safe.DeliveredQuantity, safe.ReturnedQuantity, allocationsByKey);
                    }
                }
                // PTT AVM sends no status time, so its package event is named by the package status alone; Pazarama's is its
                // items' latest history entry, and an item can change status with no new entry (live 674544454: status 7 with
                // the history ending at 11). A line cancelled or returned while the package keeps its status (another line
                // still waits or stays delivered) then arrives under the event already recorded. Its lines' quantities
                // follow the read, or the cancelled line kept its stock and the returned one read as kept.
                if (package is not null && !statusChanged && (remotePackage.StatusTimeUnknown || platformCode == "PAZARAMA" || repairedFromReview.Contains(package.Id)) && !remotePackage.IsStatusObservation
                    && package.Status == target && string.Equals(package.RawStatus, remotePackage.RawStatus, StringComparison.Ordinal))
                    ApplyReadLineQuantities(tenantId, package, remotePackage, packageObservation.Allocations, safeAllocations, lines, allocationsByKey, eventId);
                if (package is not null && (statusChanged || packageMetadataChanged))
                {
                    package.OriginExternalPackageId = string.IsNullOrWhiteSpace(remotePackage.OriginExternalPackageId) ? package.OriginExternalPackageId : remotePackage.OriginExternalPackageId;
                    package.CargoProviderExternalId = string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) ? package.CargoProviderExternalId : remotePackage.CargoProviderExternalId;
                    package.CargoTrackingNumber = string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) ? package.CargoTrackingNumber : remotePackage.CargoTrackingNumber;
                    package.CargoBarcode = string.IsNullOrWhiteSpace(remotePackage.CargoBarcode) ? package.CargoBarcode : remotePackage.CargoBarcode.Trim();
                    package.UpdatedAt = now;
                    package.Version++;
                    telemetryUpdatedCount++;
                }
                // The initial projection used shipmentPackageStatus before the
                // authoritative top-level status. When the same package event
                // is replayed after that mapper correction, repair only this
                // known ReadyToShip -> New projection mismatch. This is a
                // local idempotent repair; it does not create a new remote
                // event or perform any marketplace write.
                if (package is not null
                    && package.Status == ShipmentPackageStatus.ReadyToShip
                    && string.Equals(package.RawStatus, "ReadyToShip", StringComparison.OrdinalIgnoreCase)
                    && target == ShipmentPackageStatus.New
                    && string.Equals(remotePackage.RawStatus, "Created", StringComparison.OrdinalIgnoreCase))
                {
                    package.Status = target;
                    package.RawStatus = remotePackage.RawStatus;
                    package.StatusOccurredAt = statusAt;
                    package.UpdatedAt = now;
                    package.Version++;
                }
                if (package is not null && package.Status == ShipmentPackageStatus.ManualReview && package.RawStatus == remotePackage.RawStatus && target != ShipmentPackageStatus.ManualReview) { package.Status = target; package.UpdatedAt = now; package.Version++; ApplyReadLineQuantities(tenantId, package, remotePackage, packageObservation.Allocations, safeAllocations, lines, allocationsByKey, eventId); }
                // The event is known, but a line the detailed read places in this package may have none: an order
                // first built from a return claim held only the returned lines when the event was recorded, and the
                // order read that brought the other lines found the event already recorded.
                if (package is not null && !remotePackage.IsStatusObservation)
                    telemetryInsertedCount += AddMissingLineAllocations(tenantId, package, packageObservation.Allocations, safeAllocations, lines, allocationsByKey, eventId);
                continue;
            }
            var rejectedEventId = PackageIngestionSafety.RejectedEventId(eventId);
            if (package is not null && knownEventIds.Contains(rejectedEventId))
            {
                // This exact event was refused before; it is not evaluated
                // again, only cargo details it carries are still taken.
                if (MergePackageCargoMetadata(package, remotePackage)) { package.UpdatedAt = now; package.Version++; telemetryUpdatedCount++; }
                continue;
            }
            // The panel took the package into work a moment ago; a read that still sees it Created
            // was taken before the marketplace applied the picking. It is neither applied nor
            // reported as a refused transition while the grace window lasts.
            if (package is not null && package.Status == ShipmentPackageStatus.Processing && target == ShipmentPackageStatus.New)
            {
                var pickedAt = db.OrderStatusHistory.Local
                    .Where(x => x.TenantId == tenantId && x.PackageId == package.Id && x.RawStatus == PackageIngestionSafety.InstantPickingRawStatus)
                    .Select(x => (DateTimeOffset?)x.RecordedAt).Max()
                    ?? await db.OrderStatusHistory.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.PackageId == package.Id && x.RawStatus == PackageIngestionSafety.InstantPickingRawStatus)
                        .MaxAsync(x => (DateTimeOffset?)x.RecordedAt, cancellationToken);
                if (PackageIngestionSafety.IsCreatedEchoAfterInstantPicking(package.Status, target, pickedAt, now))
                {
                    if (MergePackageCargoMetadata(package, remotePackage)) { package.UpdatedAt = now; package.Version++; telemetryUpdatedCount++; }
                    continue;
                }
            }
            var accept = package is null || legacyTrendyolHoldCorrection || (isHepsiburada
                ? HepsiburadaPackageStatusReconciliationPolicy.ShouldAcceptStatusObservation(
                    package.Status,
                    package.StatusOccurredAt,
                    target,
                    statusAt,
                    remotePackage.IsStatusObservation)
                : PackageIngestionSafety.ShouldAccept(package.Status, package.StatusOccurredAt, target, statusAt, remotePackage.RawStatus));
            // A newer modification date with the same status is not a status change: no history row for it.
            var statusUnchanged = package is not null && package.Status == target && string.Equals(package.RawStatus, remotePackage.RawStatus, StringComparison.Ordinal);
            if (package is null) { package = new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalPackageId = remotePackage.ExternalPackageId, CreatedBy = remotePackage.CreatedBy, Status = target, RawStatus = remotePackage.RawStatus, StatusOccurredAt = statusAt, CreatedAt = now, Version = 1 }; db.ShipmentPackages.Add(package); packagesByExternalId[remotePackage.ExternalPackageId] = package; telemetryInsertedCount++; await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken, isTrendyol ? "stream" : null); await ApplyUnreadableLines(tenantId, connectionId, platformCode, order.OrderNumber, package, remotePackage, cancellationToken); }
            else if (accept) { package.Status = target; package.RawStatus = remotePackage.RawStatus; package.StatusOccurredAt = statusAt; package.Version++; }
            else if (remotePackage.IsStatusObservation && statusAt >= package.StatusOccurredAt && package.Status != target)
                // Status observations carry the read time, not an event time:
                // recording each refusal would add a history row per read.
                await RecordIssue(tenantId, $"package-transition:{package.Id}:{remotePackage.RawStatus}", "PACKAGE_TRANSITION_REJECTED", "Out-of-order veya izin verilmeyen package geçişi mevcut durumu geriye götürmedi.", cancellationToken);
            else if (statusAt >= package.StatusOccurredAt && package.Status != target)
            {
                // The refused status is not applied, but the cargo details of
                // the event still are, and the event is recorded once so it is
                // not evaluated again on every read. When the marketplace
                // reports an end state the panel refuses, the package waits
                // for an operator instead of staying in an operational tab.
                MergePackageCargoMetadata(package, remotePackage);
                var terminalConflict = PackageIngestionSafety.IsTerminalConflict(package.Status, target, remotePackage.RawStatus);
                if (terminalConflict)
                {
                    package.Status = ShipmentPackageStatus.ManualReview;
                    package.RawStatus = Short(PackageIngestionSafety.ConflictRawStatus(remotePackage.RawStatus), 96);
                }
                package.UpdatedAt = now;
                package.Version++;
                db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = "TRANSITION_REJECTED", RawStatus = Short(remotePackage.RawStatus, 96), SourceEventId = rejectedEventId, OccurredAt = statusAt, RecordedAt = now });
                knownEventIds.Add(rejectedEventId);
                await RecordIssue(tenantId, $"package-transition:{package.Id}:{remotePackage.RawStatus}",
                    terminalConflict ? "PACKAGE_TERMINAL_STATUS_CONFLICT" : "PACKAGE_TRANSITION_REJECTED",
                    terminalConflict
                        ? $"Pazaryeri paket {package.ExternalPackageId} için {remotePackage.RawStatus} bildirdi ancak paneldeki durumdan bu geçiş yapılamıyor; paket manuel incelemeye alındı."
                        : "Out-of-order veya izin verilmeyen package geçişi mevcut durumu geriye götürmedi.", cancellationToken);
            }
            // An older read is not applied, but it still fills the cargo details the package lacks (a claim-built
            // package has no tracking number; its package read does).
            else if (!accept && PackageIngestionSafety.FillMissingCargoMetadata(package, remotePackage))
            {
                package.UpdatedAt = now;
                package.Version++;
                telemetryUpdatedCount++;
            }
            if (package is not null && !remotePackage.IsStatusObservation && !string.IsNullOrWhiteSpace(remotePackage.CreatedBy)) package.CreatedBy = remotePackage.CreatedBy;
            if (accept && package is not null)
            {
                package.OriginExternalPackageId = string.IsNullOrWhiteSpace(remotePackage.OriginExternalPackageId) ? package.OriginExternalPackageId : remotePackage.OriginExternalPackageId; package.CargoProviderExternalId = string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) ? package.CargoProviderExternalId : remotePackage.CargoProviderExternalId; package.CargoTrackingNumber = string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) ? package.CargoTrackingNumber : remotePackage.CargoTrackingNumber; package.CargoBarcode = string.IsNullOrWhiteSpace(remotePackage.CargoBarcode) ? package.CargoBarcode : remotePackage.CargoBarcode.Trim(); PackageAmountPolicy.Apply(package, remotePackage); package.UpdatedAt = now; if (!statusUnchanged) db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = Wire(target), RawStatus = remotePackage.RawStatus, SourceEventId = eventId, OccurredAt = statusAt, RecordedAt = now }); knownEventIds.Add(eventId);
                foreach (var remoteAllocation in packageObservation.Allocations)
                    if (lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) && safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)
                        && UpsertLineAllocation(tenantId, package.Id, line.Id, eventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, safe.ShippedQuantity, safe.DeliveredQuantity, safe.ReturnedQuantity, allocationsByKey))
                        telemetryInsertedCount++;
            }
            // A Hepsiburada package first seen through a status observation (a webhook, a shipping list) was stored
            // without lines; a detailed read of it brings them even when its status time is older than that observation.
            // Only the lines are added: the status stays as it is.
            else if (isHepsiburada && package is not null && !remotePackage.IsStatusObservation && safeAllocations.Count > 0
                && !allocationsByKey.Values.Any(allocation => allocation.PackageId == package.Id))
            {
                foreach (var remoteAllocation in packageObservation.Allocations)
                {
                    if (!lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) || !safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)) continue;
                    // The quantities follow the package's current (newer) status, as a status observation's do.
                    var shipped = package.Status is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered ? safe.ActiveAllocatedQuantity : safe.ShippedQuantity;
                    var delivered = package.Status == ShipmentPackageStatus.Delivered ? safe.ActiveAllocatedQuantity : safe.DeliveredQuantity;
                    if (UpsertLineAllocation(tenantId, package.Id, line.Id, eventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, shipped, delivered, safe.ReturnedQuantity, allocationsByKey))
                        telemetryInsertedCount++;
                }
            }
        }
        if (isTrendyol && remote.Packages.Any(package =>
                !string.IsNullOrWhiteSpace(package.ExternalPackageId)
                && !string.Equals(package.ExternalPackageId.Trim(), "0", StringComparison.Ordinal)))
        {
            foreach (var package in packagesByExternalId.Values)
            {
                // The "0" placeholder Trendyol listed before it created the real package is closed, whatever status it
                // was left in (Awaiting, an old OnHold projection): it never ships and is never reported again.
                if (!TrendyolShipmentPackageStatusPolicy.ShouldClosePlaceholder(package.ExternalPackageId, package.Status, hasAuthoritativePackage: true)) continue;

                package.Status = ShipmentPackageStatus.Cancelled;
                package.UpdatedAt = now;
                package.Version++;
                telemetryUpdatedCount++;
            }
        }
        // n11 does not name the origin of a split package and a stream page can
        // hold the UnPacked parent without its children (or the reverse); the
        // link is made on the stored packages so it survives across pages.
        if (platformCode == "N11")
            foreach (var linked in N11OrderMapper.LinkStoredSplitPackages(packagesByExternalId.Values.ToList()))
            {
                linked.UpdatedAt = now;
                if (db.Entry(linked).State != EntityState.Added) linked.Version++;
            }
        var projectedLineQuantities = PackageLineProjectionPolicy.Recalculate(packagesByExternalId.Values.ToList(), allocationsByKey.Values.ToList(), remote.Packages);
        var cancelledBefore = lines.Values.Distinct().ToDictionary(line => line.Id, line => db.Entry(line).State == EntityState.Added ? (decimal?)null : line.CancelledQuantity);
        foreach (var line in lines.Values)
        {
            if (!projectedLineQuantities.TryGetValue(line.Id, out var projection)) continue;
            line.CancelledQuantity = projection.CancelledQuantity;
            line.ShippedQuantity = projection.ShippedQuantity;
            line.DeliveredQuantity = projection.DeliveredQuantity;
            line.ReturnedQuantity = projection.ReturnedQuantity;
        }
        // A Hepsiburada line cancelled before it was packed has no package to project from: the cancelled quantity
        // Hepsiburada reports is kept (it only grows), also when the read is older than the order.
        if (isHepsiburada)
            foreach (var remoteLine in remote.Lines.Where(x => x.CancelledQuantity > 0))
            {
                var line = lines.GetValueOrDefault(remoteLine.ExternalLineId) ?? HepsiburadaOrderLineMatcher.FindExistingLine(remoteLine, existingLines, remote.Lines);
                if (line is null || projectedLineQuantities.ContainsKey(line.Id)) continue;
                var cancelled = Math.Min(line.OrderedQuantity, remoteLine.CancelledQuantity);
                if (cancelled <= line.CancelledQuantity) continue;
                line.CancelledQuantity = cancelled;
                if (db.Entry(line).State != EntityState.Added) { line.Version++; telemetryUpdatedCount++; }
            }
        foreach (var line in lines.Values.Distinct())
            OrderLineCancelTime.Apply(line, cancelledBefore.GetValueOrDefault(line.Id), OrderLineCancelTime.Reported(line.ExternalLineId, remote), now);
        // A Hepsiburada order with a product line the panel could not read keeps the amount Hepsiburada stated: its known
        // lines are not all of it, so summing them would understate the order (UnreadableOrderLines).
        var hasUnreadableLines = isHepsiburada && (remote.Packages.Any(package => package.UnreadableLineCount > 0)
            || packagesByExternalId.Values.Concat(db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id))
                .Any(package => package.Status != ShipmentPackageStatus.Cancelled && package.UnreadableLineCount > 0));
        if (isHepsiburada && !hasUnreadableLines)
        {
            // Cancelled quantities are not part of the order value.
            var knownLineGross = lines.Values.Sum(line => line.UnitPrice * OrderLinePresentationPolicy.ActiveQuantity(line.OrderedQuantity, line.CancelledQuantity));
            if (knownLineGross > 0)
            {
                order.GrossAmount = knownLineGross;
                // The read's discount belongs to the lines it names; their cancelled share is taken off with it.
                var readLines = remote.Lines
                    .Select(remoteLine => lines.GetValueOrDefault(remoteLine.ExternalLineId))
                    .OfType<OrderLine>()
                    .Distinct()
                    .Select(line => (line.UnitPrice, line.OrderedQuantity, line.CancelledQuantity));
                var remainingDiscount = HepsiburadaOrderDiscountPolicy.RemainingDiscount(remote.DiscountAmount, readLines);
                if (remote.DiscountAmount > 0 || order.DiscountAmount == 0) order.DiscountAmount = Math.Min(knownLineGross, remainingDiscount);
                order.NetAmount = Math.Max(0m, knownLineGross - order.DiscountAmount);
            }
        }
        var orderPackages = packagesByExternalId.Values
            .Concat(db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id))
            .GroupBy(package => package.Id)
            .Select(group => group.First())
            .ToList();
        // Superseded packages (split parents, resent originals) do not decide
        // the order status; while only a split parent is known the order keeps
        // its last status until the child packages are read.
        var statusSources = ShipmentPackageSupersession.StatusSources(orderPackages);
        var acceptedStatuses = statusSources?.ToList() ?? [];
        if (statusSources is not null)
        {
            var derivedStatus = isHepsiburada
                ? HepsiburadaOrderLifecycleStatusPolicy.AggregatePackages(acceptedStatuses)
                : ShipmentPackageStatusPolicy.Aggregate(acceptedStatuses);
            var lifecycleBaseline = acceptedStatuses.Count == 0
                ? HepsiburadaOrderLifecycleStatusPolicy.UnpackagedBaseline(order.DerivedStatus)
                : Wire(derivedStatus);
            // An undelivered package decides the order (Teslim Edilemedi); the order's own lifecycle status does not
            // move it on.
            var hasUndeliveredHepsiburadaPackage = isHepsiburada && acceptedStatuses.Contains(ShipmentPackageStatus.Undelivered);
            if (isHepsiburada && !hasUndeliveredHepsiburadaPackage && HepsiburadaOrderLifecycleStatusPolicy.Reconcile(
                    lifecycleBaseline,
                    remote.LifecycleStatus,
                    lines.Values.Select(line => line.RawStatus)) is { } hepsiburadaStatus)
                derivedStatus = hepsiburadaStatus;
            // Without a package an unknown lifecycle status is reviewed, never left as a new order.
            order.DerivedStatus = isHepsiburada && acceptedStatuses.Count == 0
                && HepsiburadaOrderLifecycleStatusPolicy.UnknownUnpackagedStatus(order.DerivedStatus, isNewOrder, orderIsFresh, remote.LifecycleStatus) is { } unknownStatus
                ? unknownStatus
                : Wire(derivedStatus);
            if (orderIsFresh
                && order.DerivedStatus.Trim().ToUpperInvariant() is not ("DELIVERED" or "CANCELLED" or "RETURNED")
                && OpenOrderLifecyclePolicy.IsHepsiburadaClaimOnlyWithoutPackage(
                    platformCode,
                    acceptedStatuses.Count,
                    lines.Values.Select(line => line.RawStatus),
                    remote.LifecycleStatus))
                order.DerivedStatus = "UNVERIFIED";
        }
        if (usesTrendyolPackageStatuses)
        {
            // A stream page carries one sibling package at a time: the totals
            // are rebuilt from every stored live package, not from the page
            // (also after an older page that still brought a newer package).
            var packagesInRead = remote.Packages.Select(package => package.ExternalPackageId).ToHashSet(StringComparer.Ordinal);
            if (OrderAmountProjectionPolicy.FromPackages(orderPackages, packagesInRead) is { } totals)
                (order.GrossAmount, order.DiscountAmount, order.NetAmount) = totals;
            else if (orderIsFresh && !isNewOrder)
                (order.GrossAmount, order.DiscountAmount, order.NetAmount) = previousAmounts;
        }
        if (isShopify)
        {
            var manualStatus = await db.OrderStatusHistory
                .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.RawStatus.StartsWith(ShopifyManualStatusPolicy.RawStatusPrefix))
                .OrderByDescending(x => x.OccurredAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (manualStatus is not null && ShopifyManualStatusPolicy.StillHolds(manualStatus.OccurredAt, ShopifyManualStatusPolicy.RemoteStatusAt(manualStatus.SourceEventId), order.LastRemoteModifiedAt, order.DerivedStatus))
            {
                order.DerivedStatus = manualStatus.CanonicalStatus;
                // Still valid after this read: the lists treat a manual status as current while it
                // is not older than the order's last marketplace change.
                if (manualStatus.OccurredAt < order.LastRemoteModifiedAt) manualStatus.OccurredAt = order.LastRemoteModifiedAt;
            }
        }
        // Every marketplace's order moves panel stock, Shopify's included: a sale on one channel lowers
        // the stock the others are sent.
        if (batch is null)
        {
            await AdoptLateMatchedLineStock(tenantId, connectionId, lateMatchedLines, cancellationToken);
            await OrderLineCostSnapshot.TakeAsync(db, tenantId, costSnapshotLines, cancellationToken);
            await ProjectOrderReservations(tenantId, connectionId, lines.Values.Select(line => (line, remote.LastModifiedAt)).ToList(), cancellationToken);
        }
        else
            batch.ReservationSources.AddRange(lines.Values.Select(line => (line, remote.LastModifiedAt)));
        // The same order read again (overlap window, status scan) rewrites nothing and sends no event (SyncNoOpWrites).
        SyncNoOpWrites.DropUnchangedOrder(db, order, existingLines.Concat(lines.Values), orderPackages);
        if (saveChanges) await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Cargo details are facts about the parcel, independent of whether the
    // event's status could be applied; known values are never blanked.
    private static bool MergePackageCargoMetadata(ShipmentPackage package, RemotePackage remotePackage)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) && package.CargoProviderExternalId != remotePackage.CargoProviderExternalId) { package.CargoProviderExternalId = remotePackage.CargoProviderExternalId; changed = true; }
        if (!string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) && package.CargoTrackingNumber != remotePackage.CargoTrackingNumber) { package.CargoTrackingNumber = remotePackage.CargoTrackingNumber; changed = true; }
        if (!string.IsNullOrWhiteSpace(remotePackage.CargoBarcode) && package.CargoBarcode != remotePackage.CargoBarcode.Trim()) { package.CargoBarcode = remotePackage.CargoBarcode.Trim(); changed = true; }
        if (!string.IsNullOrWhiteSpace(remotePackage.OriginExternalPackageId) && package.OriginExternalPackageId != remotePackage.OriginExternalPackageId) { package.OriginExternalPackageId = remotePackage.OriginExternalPackageId; changed = true; }
        return changed;
    }

    // Every marketplace: a record the mapper refused while its order number was readable (AdapterPageIssue.RereadOrderId)
    // joins the re-read list (RejectedOrderRereadPolicy), so it is read again by number instead of being passed over.
    // ISLEYICI-12: one order page of a list read (Trendyol stream and reconcile, Shopify, N11, Pazarama): its orders are
    // counted, its contract issues recorded under the read's key prefix, its refused orders noted for a re-read, and its
    // orders stored. Hepsiburada's paid-order page stores only the orders it may import, so it keeps its own body.
    private async Task IngestOrderPageAsync(Guid tenantId, Guid connectionId, AdapterPageResult<RemoteOrder> page, string issueKeyPrefix, CancellationToken cancellationToken)
    {
        foreach (var _ in page.Items) TrackReceived();
        foreach (var issue in page.Issues ?? [])
            await RecordIssue(tenantId, $"{issueKeyPrefix}:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
        NoteRefusedOrders(page.Issues);
        await UpsertOrders(tenantId, connectionId, page.Items, cancellationToken);
    }

    private void NoteRefusedOrders(IReadOnlyList<AdapterPageIssue>? issues)
    {
        foreach (var issue in issues ?? [])
            if (!string.IsNullOrWhiteSpace(issue.RereadOrderId)) rereadOrderIds.Add(issue.RereadOrderId.Trim());
    }

    // The split parents of an order (O8): the packages that a package of this read, or one the order already stores,
    // names as its origin.
    private async Task<HashSet<string>> KnownSplitParentIdsAsync(Guid tenantId, Guid connectionId, Order? order, IReadOnlyList<RemotePackage> remotePackages, OrderIngestionBatch? batch, CancellationToken cancellationToken)
    {
        IEnumerable<(string ExternalPackageId, string? OriginExternalPackageId)> stored = [];
        if (order is not null)
            stored = batch?.PackagesByOrder.GetValueOrDefault(order.Id) is { } batchPackages
                ? batchPackages.Values.Select(package => (package.ExternalPackageId, package.OriginExternalPackageId)).ToList()
                : (await db.ShipmentPackages.AsNoTracking()
                    .Where(package => package.TenantId == tenantId && package.ConnectionId == connectionId && package.OrderId == order.Id && package.OriginExternalPackageId != null)
                    .Select(package => new { package.ExternalPackageId, package.OriginExternalPackageId })
                    .ToListAsync(cancellationToken))
                    .Select(package => (package.ExternalPackageId, package.OriginExternalPackageId));
        return SplitParentIds(remotePackages.Select(package => (package.ExternalPackageId, package.OriginExternalPackageId)).Concat(stored));
    }

    internal static HashSet<string> SplitParentIds(IEnumerable<(string ExternalPackageId, string? OriginExternalPackageId)> packages) => packages
        .Where(package => !string.IsNullOrWhiteSpace(package.OriginExternalPackageId) && !string.Equals(package.OriginExternalPackageId, package.ExternalPackageId, StringComparison.Ordinal))
        .Select(package => package.OriginExternalPackageId!)
        .ToHashSet(StringComparer.Ordinal);

    // The package's unreadable product lines as a detailed read found them (RemotePackage.UnreadableLineCount; null: the
    // read does not judge them), the same for every marketplace (UnreadableOrderLines). While lines are unreadable the
    // order carries an open issue that says so; a read that finds every line again clears the count and resolves it, and
    // the line issues the adapters raised for the package (whatever the read: order poll, notification, package list).
    // True when the count changed.
    private async Task<bool> ApplyUnreadableLines(Guid tenantId, Guid connectionId, string? platformCode, string orderNumber, ShipmentPackage package, RemotePackage remote, CancellationToken cancellationToken)
    {
        if (remote.UnreadableLineCount is not { } count) return false;
        var issueKey = UnreadableOrderLines.PackageIssueKey(connectionId, package.ExternalPackageId);
        if (count > 0)
            await RecordIssue(tenantId, issueKey, UnreadableOrderLines.IssueCode, UnreadableOrderLines.PackageIssueSummary(platformCode, orderNumber, package.ExternalPackageId, count), cancellationToken);
        if (package.UnreadableLineCount == count) return false;
        var cleared = package.UnreadableLineCount > 0 && count == 0;
        package.UnreadableLineCount = count;
        if (!cleared) return true;
        // The adapters' line issues are keyed {source}:{connection}:{package}[:{line}]:{code}.
        var packageKey = $":{connectionId}:{package.ExternalPackageId}:";
        var now = timeProvider.GetUtcNow();
        foreach (var issue in await db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved
                && (x.DedupeKey == issueKey || (x.Code == UnreadableOrderLines.IssueCode || x.Code == UnreadableOrderLines.AmountIssueCode || x.Code == MissingLinesIssueCode) && x.DedupeKey.Contains(packageKey))).ToListAsync(cancellationToken))
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
        return true;
    }

    private const string MissingLinesIssueCode = "ORDER_PACKAGE_LINES_MISSING";

    // Every open quantity refusal of the package, whatever event it was raised for.
    private async Task ResolvePackageQuantityIssues(Guid tenantId, Guid connectionId, string externalPackageId, CancellationToken cancellationToken)
    {
        var prefix = OperationalIssueRetentionService.PackageQuantityIssuePrefix(connectionId, externalPackageId);
        foreach (var issue in await db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(prefix)).ToListAsync(cancellationToken))
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = timeProvider.GetUtcNow();
        }
    }
}
