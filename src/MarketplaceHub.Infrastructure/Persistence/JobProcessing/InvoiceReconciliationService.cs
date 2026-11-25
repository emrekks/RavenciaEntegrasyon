using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 5 step 1 (16 Nov 2026): the order invoice checks (ORDER_INVOICE_RECONCILIATION and its Shopify and
// Hepsiburada types), moved out of the job processor as they were: Trendyol's package reads with the six-hourly sweep,
// the Order V2 batches, the claim-order hydration and the slice budget; Hepsiburada's order details and missing-invoice
// list; the order details of the others. It works for one run of one job (run) on the job's AppDbContext and stores
// what it reads through that run's OrderIngestionService. Reads only: nothing is sent to a marketplace, no invoice is
// made, and a missing invoice field is never taken as "not invoiced" (unknown stays unknown).
internal sealed partial class InvoiceReconciliationService(
    AppDbContext db,
    IssueRecorder issues,
    SyncCursorStore cursors,
    IOrderPort orders,
    IHepsiburadaInvoiceStatusPort hepsiburadaInvoiceStatus,
    IConfiguration configuration,
    TimeProvider timeProvider,
    OrderIngestionService orderIngestion,
    OrderReadService orderRead,
    JobRunContext run)
{
    // What the moved code used from the processor: the same classes and helpers the processor calls.
    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    private Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) =>
        issues.ResolveAsync(tenantId, key, timeProvider.GetUtcNow(), cancellationToken);

    private Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) =>
        cursors.GetAsync(tenantId, connectionId, resource, cancellationToken);

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));

    private void TrackRequest() => run.TrackRequest();

    private void TrackReceived() => run.TrackReceived();

    private void TrackResultFailure(AdapterError? error) => run.TrackResultFailure(error);

    private static int ReadBoundedInt(string payloadJson, string propertyName, int fallback, int minimum, int maximum) =>
        JobPayloadReader.BoundedInt(payloadJson, propertyName, fallback, minimum, maximum);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    private Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionService.OrderIngestionBatch? batch = null, bool saveChanges = true, bool persistFinancialObservations = false) =>
        orderIngestion.UpsertOrder(tenantId, connectionId, remote, cancellationToken, batch, saveChanges, persistFinancialObservations);

    private Task MergeMarketplaceInvoiceState(ShipmentPackage package, RemotePackage remotePackage, CancellationToken cancellationToken, string? discoverySource = null) =>
        orderIngestion.MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken, discoverySource);

    private Task<OrderLookupHint?> OrderHint(Guid tenantId, Guid connectionId, string externalOrderId, CancellationToken cancellationToken) =>
        orderRead.OrderHint(tenantId, connectionId, externalOrderId, cancellationToken);

    private Task ReadMissingHepsiburadaOrdersAsync(Guid tenantId, Guid connectionId, string correlationId, IEnumerable<(string OrderNumber, string? PackageNumber)> references, CancellationToken cancellationToken) =>
        orderRead.ReadMissingHepsiburadaOrdersAsync(tenantId, connectionId, correlationId, references, cancellationToken);

    private static string? TrendyolWebhookStorefront(string rawPackageJson) => OrderReadService.TrendyolWebhookStorefront(rawPackageJson);

    // A package the invoice check read on its own (Trendyol): its number, the marketplace invoice number and state.
    private void RecordInvoiceRead(ShipmentPackage package, MarketplaceInvoiceStatus before, string? numberBefore)
    {
        if (!run.ReadRecordingEnabled || run.ReadRecordingKind != "INVOICE") return;
        var changed = before != package.MarketplaceInvoiceStatus || !string.Equals(numberBefore, package.MarketplaceInvoiceNumber, StringComparison.Ordinal);
        run.ReadRecorder.Add("INVOICE", new JobReadRecord(package.ExternalPackageId, package.MarketplaceInvoiceNumber, package.MarketplaceInvoiceStatus.ToString(), changed ? JobReadOutcomes.Updated : JobReadOutcomes.Unchanged));
    }

    // Unfinished: a Trendyol run stopped at its slice budget with orders left; it continues shortly.
    internal async Task<(bool Succeeded, bool Unfinished)> ReconcileOrderInvoices(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var isHepsiburada = platformCode == "HEPSIBURADA";
        var isTrendyol = platformCode == "TRENDYOL";
        var batchSize = ReadBoundedInt(payloadJson, "batchSize", 50, 1, 250);
        var externalOrderIds = new List<string>();
        var hepsiburadaInvoiceStatusUnavailable = new HashSet<string>(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        Guid? targetedOrderId = null;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.TryGetProperty("targetOrderId", out var targetOrderElement)
                && targetOrderElement.ValueKind == JsonValueKind.String
                && targetOrderElement.TryGetGuid(out var parsedTargetOrderId))
                targetedOrderId = parsedTargetOrderId;
        }
        catch (JsonException)
        {
            // The normal scheduled payload is valid and has no targetOrderId.
        }
        var eligibleOrders = targetedOrderId is { } requestedOrderId
            ? db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId
                && order.ConnectionId == connectionId
                && order.Id == requestedOrderId
                && order.ExternalOrderId != null
                && !DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus)
                && db.ShipmentPackages.Any(package => package.TenantId == tenantId
                    && package.ConnectionId == connectionId
                    && package.OrderId == order.Id
                    && package.Status != ShipmentPackageStatus.Cancelled))
            : DueInvoiceReconciliationOrders(tenantId, connectionId, now);
        SyncCursor? invoiceCursor = null;
        List<string?> cursorAfterOrder = [];
        SyncCursor? unresolvedCursor = null;
        List<string> unresolvedCursorAfterOrder = [];
        var sweepOrderIds = new HashSet<string>(StringComparer.Ordinal);
        var sweepFinishesWithBatch = false;
        if (targetedOrderId is not null)
        {
            externalOrderIds = await eligibleOrders.OrderBy(order => order.Id)
                .Select(order => order.ExternalOrderId)
                .Take(1)
                .ToListAsync(cancellationToken);
        }
        else if (isHepsiburada)
        {
            // Order detail is the only documented source for hasInvoice. Rotate
            // by immutable order ID: UpdatedAt changes during every upsert and
            // would move reconciled orders ahead of the cursor indefinitely.
            invoiceCursor = await Cursor(tenantId, connectionId, "ORDER_INVOICE_RECONCILIATION", cancellationToken);
            var afterOrderId = HepsiburadaInvoiceReconciliationBatchPolicy.ReadCursor(invoiceCursor.OpaqueCursor)?.OrderId;
            var afterCursorQuery = eligibleOrders;
            if (afterOrderId is { } cursorOrderId)
                afterCursorQuery = afterCursorQuery.Where(order => order.Id.CompareTo(cursorOrderId) > 0);
            var afterCursor = await afterCursorQuery
                .OrderBy(order => order.Id)
                .Select(order => new HepsiburadaInvoiceOrderCandidate(order.Id, order.ExternalOrderId))
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            IReadOnlyCollection<HepsiburadaInvoiceOrderCandidate> wrapped = [];
            if (afterOrderId is { } wrapOrderId && afterCursor.Count < batchSize)
            {
                wrapped = await eligibleOrders
                    .Where(order => order.Id.CompareTo(wrapOrderId) <= 0)
                    .OrderBy(order => order.Id)
                    .Select(order => new HepsiburadaInvoiceOrderCandidate(order.Id, order.ExternalOrderId))
                    .Take(batchSize - afterCursor.Count)
                    .ToListAsync(cancellationToken);
            }

            var selected = HepsiburadaInvoiceReconciliationBatchPolicy.Select(afterCursor, wrapped, batchSize);
            externalOrderIds = selected.Select(candidate => candidate.ExternalOrderId).ToList();
            cursorAfterOrder = selected.Select(HepsiburadaInvoiceReconciliationBatchPolicy.WriteCursor).ToList<string?>();
        }
        else if (isTrendyol)
        {
            // Orders whose local invoice still waits for its confirmation are read on every run. The other
            // shipped or delivered packages still uninvoiced on Trendyol are read in one sweep
            // every six hours; the sweep keeps its position, so a cut-short run resumes it.
            // Scan 25 Nov 2026 (A-3): the waiting orders rotate on their own cursor (the run's
            // ORDER_INVOICE_RECONCILIATION row, as on the other platforms) and wrap at the end, so more than
            // a batch of them no longer keeps the same first orders and starves the rest and the sweep.
            unresolvedCursor = await Cursor(tenantId, connectionId, "ORDER_INVOICE_RECONCILIATION", cancellationToken);
            var unresolvedAfterOrderId = OrderInvoiceReconciliationBatchPolicy.ReadCursor(unresolvedCursor.OpaqueCursor);
            var unresolvedQuery = TrendyolUnresolvedInvoiceOrders(tenantId, connectionId);
            var unresolvedAfterCursorQuery = unresolvedQuery;
            if (unresolvedAfterOrderId is { } unresolvedCursorOrderId)
                unresolvedAfterCursorQuery = unresolvedAfterCursorQuery.Where(order => order.Id.CompareTo(unresolvedCursorOrderId) > 0);
            var unresolvedAfterCursor = await unresolvedAfterCursorQuery
                .OrderBy(order => order.Id)
                .Select(order => new OrderInvoiceReconciliationCandidate(order.Id, order.ExternalOrderId))
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            IReadOnlyCollection<OrderInvoiceReconciliationCandidate> unresolvedWrapped = [];
            if (unresolvedAfterOrderId is { } unresolvedWrapOrderId && unresolvedAfterCursor.Count < batchSize)
            {
                unresolvedWrapped = await unresolvedQuery
                    .Where(order => order.Id.CompareTo(unresolvedWrapOrderId) <= 0)
                    .OrderBy(order => order.Id)
                    .Select(order => new OrderInvoiceReconciliationCandidate(order.Id, order.ExternalOrderId))
                    .Take(batchSize - unresolvedAfterCursor.Count)
                    .ToListAsync(cancellationToken);
            }
            var unresolved = OrderInvoiceReconciliationBatchPolicy.Select(unresolvedAfterCursor, unresolvedWrapped, batchSize);
            externalOrderIds = unresolved.Select(candidate => candidate.ExternalOrderId).ToList();
            cursorAfterOrder = unresolved.Select(_ => (string?)null).ToList();
            unresolvedCursorAfterOrder = unresolved.Select(candidate => OrderInvoiceReconciliationBatchPolicy.WriteCursor(candidate.OrderId)).ToList();
            invoiceCursor = await Cursor(tenantId, connectionId, TrendyolInvoiceReconciliationPolicy.SweepCursorResource, cancellationToken);
            var sweepRoom = batchSize - unresolved.Count;
            if (sweepRoom > 0 && TrendyolInvoiceReconciliationPolicy.SweepDue(invoiceCursor.OpaqueCursor, invoiceCursor.LastSuccessAt, now))
            {
                var unresolvedIds = unresolved.Select(candidate => candidate.OrderId).ToList();
                var sweepQuery = TrendyolInvoiceSweepOrders(tenantId, connectionId, now).Where(order => !unresolvedIds.Contains(order.Id));
                if (OrderInvoiceReconciliationBatchPolicy.ReadCursor(invoiceCursor.OpaqueCursor) is { } sweepPosition)
                    sweepQuery = sweepQuery.Where(order => order.Id.CompareTo(sweepPosition) > 0);
                var sweep = await sweepQuery
                    .OrderBy(order => order.Id)
                    .Select(order => new OrderInvoiceReconciliationCandidate(order.Id, order.ExternalOrderId))
                    .Take(sweepRoom)
                    .ToListAsync(cancellationToken);
                foreach (var candidate in sweep)
                {
                    externalOrderIds.Add(candidate.ExternalOrderId);
                    cursorAfterOrder.Add(OrderInvoiceReconciliationBatchPolicy.WriteCursor(candidate.OrderId));
                    sweepOrderIds.Add(candidate.ExternalOrderId);
                }
                sweepFinishesWithBatch = sweep.Count < sweepRoom;
            }
        }
        else
        {
            // Unknown Trendyol/Shopify invoice payloads cannot be advanced by
            // MarketplaceInvoiceObservedAt. Rotate on stable order IDs so one
            // unchanged first page cannot starve the rest of the catalog.
            invoiceCursor = await Cursor(tenantId, connectionId, "ORDER_INVOICE_RECONCILIATION", cancellationToken);
            var afterOrderId = OrderInvoiceReconciliationBatchPolicy.ReadCursor(invoiceCursor.OpaqueCursor);
            var afterCursorQuery = eligibleOrders;
            if (afterOrderId is { } cursorOrderId)
                afterCursorQuery = afterCursorQuery.Where(order => order.Id.CompareTo(cursorOrderId) > 0);
            var afterCursor = await afterCursorQuery
                .OrderBy(order => order.Id)
                .Select(order => new OrderInvoiceReconciliationCandidate(order.Id, order.ExternalOrderId))
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            IReadOnlyCollection<OrderInvoiceReconciliationCandidate> wrapped = [];
            if (afterOrderId is { } wrapOrderId && afterCursor.Count < batchSize)
            {
                wrapped = await eligibleOrders
                    .Where(order => order.Id.CompareTo(wrapOrderId) <= 0)
                    .OrderBy(order => order.Id)
                    .Select(order => new OrderInvoiceReconciliationCandidate(order.Id, order.ExternalOrderId))
                    .Take(batchSize - afterCursor.Count)
                    .ToListAsync(cancellationToken);
            }

            var selected = OrderInvoiceReconciliationBatchPolicy.Select(afterCursor, wrapped, batchSize);
            externalOrderIds = selected.Select(candidate => candidate.ExternalOrderId).ToList();
            cursorAfterOrder = selected.Select(candidate => OrderInvoiceReconciliationBatchPolicy.WriteCursor(candidate.OrderId)).ToList<string?>();
        }

        // A rolled-back checkpoint keeps the positions it moved, so the retry and the next save carry them.
        object[] keptCursors = [.. new object?[] { invoiceCursor, unresolvedCursor }.OfType<object>()];
        var batchObservedPackages = isTrendyol && sweepOrderIds.Count > 0
            ? await ReadTrendyolSweepPackageBatches(tenantId, connectionId, correlationId, sweepOrderIds, now, cancellationToken)
            : [];
        var totalOrders = externalOrderIds.Count;
        // A Trendyol package lookup can scan the order stream page by page, so one order may take
        // minutes. The batch stops within its time budget and every finished order is checkpointed,
        // so a long batch neither hits the worker timeout nor repeats the same orders forever.
        var timeBudget = isTrendyol
            ? TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:TrendyolSliceSeconds", 30), 10, 840))
            : TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:OrderInvoiceReconciliation:TimeBudgetSeconds", 600), 30, 840));
        var batchStartedAt = timeProvider.GetUtcNow();
        await UpdateOrderInvoiceReconciliationProgressAsync(
            tenantId,
            jobId,
            0,
            totalOrders,
            totalOrders == 0 ? 100 : 0,
            totalOrders == 0 ? "Kontrol edilecek sipariş bulunamadı" : $"0 / {totalOrders:N0} sipariş kontrol edildi",
            cancellationToken);

        var changedOrders = 0;
        async Task ReconcileOrderInvoiceAsync(string externalOrderId)
        {
            if (isTrendyol)
            {
                // Historical return claims can create a deliberately partial
                // order projection. Hydrate those rows from the full read-only
                // order contract before reconciling package invoice data.
                if (!await HydrateTrendyolReturnClaimOrder(tenantId, connectionId, externalOrderId, correlationId, cancellationToken))
                    return;
                await ReconcileTrendyolPackageInvoices(tenantId, connectionId, externalOrderId, correlationId, sweepOrderIds.Contains(externalOrderId), batchObservedPackages, cancellationToken);
                return;
            }

            TrackRequest();
            var result = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-invoice-reconciliation:{externalOrderId}"), externalOrderId, cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error?.Class != AdapterErrorClass.NotFound) TrackResultFailure(result.Error);
                await RecordIssue(tenantId, $"order-invoice-reconciliation:{connectionId}:{externalOrderId}", result.Error!.Code,
                    $"Siparişin pazaryeri fatura durumu yenilenemedi; sonraki otomatik taramada tekrar denenecek. {result.Error.SafeMessage}", cancellationToken);
                return;
            }

            TrackReceived();
            var updatedBefore = run.UpdatedCount;
            if (isHepsiburada && !await MergeHepsiburadaOrderInvoiceState(tenantId, connectionId, result.Value!, cancellationToken))
                hepsiburadaInvoiceStatusUnavailable.Add(externalOrderId);
            await UpsertOrder(tenantId, connectionId, result.Value!, cancellationToken, persistFinancialObservations: isShopify);
            // "İşlenen" counts orders, not rows: one order read once is processed once, however many of its rows moved.
            if (run.UpdatedCount > updatedBefore) changedOrders++;
            await ResolveIssue(tenantId, $"order-invoice-reconciliation:{connectionId}:{externalOrderId}", cancellationToken);
        }

        var processedOrders = 0;
        var stoppedByTimeBudget = false;
        for (var index = 0; index < externalOrderIds.Count; index++)
        {
            if (processedOrders > 0 && timeProvider.GetUtcNow() - batchStartedAt >= timeBudget)
            {
                stoppedByTimeBudget = true;
                break;
            }

            try
            {
                await ReconcileOrderInvoiceAsync(externalOrderIds[index]);
            }
            finally
            {
                processedOrders++;
                if (!cancellationToken.IsCancellationRequested)
                {
                    var percent = totalOrders == 0 ? 100 : Math.Min(99, (int)Math.Floor(processedOrders * 100d / totalOrders));
                    await UpdateOrderInvoiceReconciliationProgressAsync(
                        tenantId,
                        jobId,
                        processedOrders,
                        totalOrders,
                        percent,
                        $"{processedOrders:N0} / {totalOrders:N0} sipariş kontrol edildi",
                        cancellationToken,
                        isTrendyol ? null : changedOrders);
                }
            }

            // Checkpoint: the finished order's changes and the cursor position are saved together.
            var checkpointSaved = true;
            var cursorMoved = false;
            if (invoiceCursor is not null && index < cursorAfterOrder.Count && cursorAfterOrder[index] is { } position)
            {
                invoiceCursor.OpaqueCursor = position;
                invoiceCursor.Version++;
                cursorMoved = true;
            }
            if (unresolvedCursor is not null && index < unresolvedCursorAfterOrder.Count)
            {
                unresolvedCursor.OpaqueCursor = unresolvedCursorAfterOrder[index];
                unresolvedCursor.Version++;
                cursorMoved = true;
            }
            if (cursorMoved || isTrendyol)
                checkpointSaved = await SaveOrderInvoiceCheckpointAsync(isTrendyol, cancellationToken, keptCursors);

            // A newer write landed on this order's invoice rows meanwhile: read the order once more against it.
            if (!checkpointSaved && !cancellationToken.IsCancellationRequested)
            {
                await ReconcileOrderInvoiceAsync(externalOrderIds[index]);
                await SaveOrderInvoiceCheckpointAsync(isTrendyol, cancellationToken, keptCursors);
            }
        }

        if (isHepsiburada)
        {
            if (hepsiburadaInvoiceStatusUnavailable.Count == 0)
                await ResolveIssue(tenantId, $"order-invoice-reconciliation:{connectionId}:invoice-status-contract", cancellationToken);
            // The missing-invoice list is read every run: it also names orders the panel lacks.
            await ReconcileHepsiburadaMissingInvoiceStatuses(tenantId, connectionId, correlationId, hepsiburadaInvoiceStatusUnavailable, cancellationToken);
        }

        // The sweep is complete once its last orders are read; the next one starts six hours later.
        if (isTrendyol && invoiceCursor is not null && sweepFinishesWithBatch && !stoppedByTimeBudget && !cancellationToken.IsCancellationRequested)
        {
            invoiceCursor.OpaqueCursor = null;
            invoiceCursor.LastSuccessAt = timeProvider.GetUtcNow();
            invoiceCursor.Version++;
        }

        if (invoiceCursor is not null || isTrendyol)
            await SaveOrderInvoiceCheckpointAsync(isTrendyol, cancellationToken, keptCursors);

        await UpdateOrderInvoiceReconciliationProgressAsync(
            tenantId,
            jobId,
            processedOrders,
            totalOrders,
            100,
            totalOrders == 0
                ? "Kontrol edilecek sipariş bulunamadı"
                : stoppedByTimeBudget
                    ? $"{processedOrders:N0} / {totalOrders:N0} sipariş kontrol edildi; süre sınırı nedeniyle kalanlar sonraki taramada"
                    : $"{totalOrders:N0} / {totalOrders:N0} sipariş kontrol edildi",
            cancellationToken,
            isTrendyol ? null : changedOrders);

        return (true, isTrendyol && stoppedByTimeBudget);
    }

    private IQueryable<Order> DueInvoiceReconciliationOrders(Guid tenantId, Guid connectionId, DateTimeOffset now)
    {
        var recentOrderCutoff = now - MarketplaceInvoiceReconciliationCadencePolicy.RecentOrderWindow;
        var recentCheckCutoff = now - MarketplaceInvoiceReconciliationCadencePolicy.RecentInterval;
        var olderCheckCutoff = now - MarketplaceInvoiceReconciliationCadencePolicy.OlderInterval;

        return db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.ConnectionId == connectionId
                && !DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus))
            .Where(order => db.ShipmentPackages.Any(package => package.TenantId == tenantId
                && package.ConnectionId == connectionId
                && package.OrderId == order.Id
                && package.Status != ShipmentPackageStatus.Cancelled
                && (package.MarketplaceInvoiceStatus != MarketplaceInvoiceStatus.Invoiced
                    || db.Invoices.Any(invoice => invoice.TenantId == tenantId
                        && invoice.OrderId == order.Id
                        && invoice.OriginalInvoiceId == null
                        && invoice.SequencePurpose == "SALE"
                        && (invoice.Status != InvoiceStatus.Completed
                            || db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId
                                && state.InvoiceId == invoice.Id
                                && state.PackageId == package.Id
                                && state.Status != "CONFIRMED")
                            || !db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId
                                && state.InvoiceId == invoice.Id
                                && state.PackageId == package.Id)
                                && !db.MarketplaceDeliveries.Any(attempt => attempt.TenantId == tenantId
                                    && attempt.InvoiceId == invoice.Id
                                    && attempt.PackageId == package.Id
                                    && attempt.Status == "CONFIRMED")))
                    || (order.OrderedAt >= recentOrderCutoff
                        ? package.MarketplaceInvoiceObservedAt == null || package.MarketplaceInvoiceObservedAt <= recentCheckCutoff
                        : package.MarketplaceInvoiceObservedAt == null || package.MarketplaceInvoiceObservedAt <= olderCheckCutoff))));
    }

    // The sweep's packages of the last month are read through Order V2 fifty at a time; the
    // per-order pass then reads only what the batches did not return (and older packages).
    private async Task<HashSet<Guid>> ReadTrendyolSweepPackageBatches(Guid tenantId, Guid connectionId, string correlationId, IReadOnlySet<string> sweepOrderIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var observed = new HashSet<Guid>();
        var orderIds = sweepOrderIds.ToList();
        var v2Cutoff = now - TrendyolLookupPolicy.V2History;
        var packages = await (from package in db.ShipmentPackages.AsNoTracking()
                              join order in db.Orders.AsNoTracking()
                                  on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                              where package.TenantId == tenantId && package.ConnectionId == connectionId
                                  && orderIds.Contains(order.ExternalOrderId)
                                  && order.OrderedAt >= v2Cutoff
                                  && TrendyolInvoiceReconciliationPolicy.SweepStatuses.Contains(package.Status)
                                  && package.MarketplaceInvoiceStatus != MarketplaceInvoiceStatus.Invoiced
                                  // A "0" placeholder cannot be read by its number and several orders hold it.
                                  && package.ExternalPackageId != ShipmentPackageSupersession.PlaceholderExternalId
                              select new { package.Id, package.ExternalPackageId, order.OrderedAt, order.StoreFrontCode })
            .ToListAsync(cancellationToken);
        var localIds = packages.ToDictionary(package => package.ExternalPackageId, package => package.Id, StringComparer.Ordinal);
        foreach (var (packageIds, hint) in TrendyolLookupPolicy.PackageBatches(packages.Select(package => new TrendyolPackageRef(package.ExternalPackageId, package.OrderedAt, package.StoreFrontCode)), now))
        {
            TrackRequest();
            var batch = await orders.GetShipmentPackagesAsync(Context(tenantId, connectionId, correlationId, $"trendyol-invoice-package-batch:{packageIds[0]}"), packageIds, hint, cancellationToken);
            if (!batch.IsSuccess)
            {
                // The per-order pass reads these packages one by one.
                TrackResultFailure(batch.Error);
                if (batch.Error?.Class == AdapterErrorClass.RateLimit) break;
                continue;
            }
            foreach (var remotePackage in batch.Value!.SelectMany(order => order.Packages))
            {
                if (!localIds.TryGetValue(remotePackage.ExternalPackageId, out var localId)) continue;
                var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == localId, cancellationToken);
                if (package is null) continue;
                TrackReceived();
                var invoiceBefore = (package.MarketplaceInvoiceStatus, package.MarketplaceInvoiceNumber);
                await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken, "reconciliation");
                RecordInvoiceRead(package, invoiceBefore.MarketplaceInvoiceStatus, invoiceBefore.MarketplaceInvoiceNumber);
                await ResolveIssue(tenantId, $"trendyol-invoice-package-read:{connectionId}:{localId}", cancellationToken);
                // Saved package by package, so the batch's answer is not held while later batches are read; a
                // package another writer changed meanwhile is left to the per-order pass, which reads it again.
                if (await SaveOrderInvoiceCheckpointAsync(isTrendyol: true, cancellationToken))
                    observed.Add(localId);
            }
        }
        await SaveOrderInvoiceCheckpointAsync(isTrendyol: true, cancellationToken);
        return observed;
    }

    // Trendyol orders with a package whose local sale invoice still waits for its confirmation on the platform: sent and
    // not confirmed yet (TrendyolInvoiceReconciliationPolicy.AwaitingConfirmationStatuses), or Completed without a
    // confirmed delivery to this package. A cancelled, rejected, draft or in-review invoice is not read here: a read
    // never moves it (scan 25 Nov 2026, A-3); its package is still read by the six-hourly sweep.
    private IQueryable<Order> TrendyolUnresolvedInvoiceOrders(Guid tenantId, Guid connectionId) =>
        db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.ConnectionId == connectionId
                && !DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus))
            .Where(order => db.ShipmentPackages.Any(package => package.TenantId == tenantId
                && package.ConnectionId == connectionId
                && package.OrderId == order.Id
                && package.Status != ShipmentPackageStatus.Cancelled
                && db.Invoices.Any(invoice => invoice.TenantId == tenantId
                    && invoice.OrderId == order.Id
                    && invoice.OriginalInvoiceId == null
                    && invoice.SequencePurpose == "SALE"
                    && (TrendyolInvoiceReconciliationPolicy.AwaitingConfirmationStatuses.Contains(invoice.Status)
                        || invoice.Status == InvoiceStatus.Completed
                            && (db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId
                                    && state.InvoiceId == invoice.Id
                                    && state.PackageId == package.Id
                                    && state.Status != "CONFIRMED")
                                || !db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId
                                    && state.InvoiceId == invoice.Id
                                    && state.PackageId == package.Id)
                                    && !db.MarketplaceDeliveries.Any(attempt => attempt.TenantId == tenantId
                                        && attempt.InvoiceId == invoice.Id
                                        && attempt.PackageId == package.Id
                                        && attempt.Status == "CONFIRMED"))))));

    // Trendyol orders of the last 90 days with a shipped or delivered package still uninvoiced on the platform.
    private IQueryable<Order> TrendyolInvoiceSweepOrders(Guid tenantId, Guid connectionId, DateTimeOffset now)
    {
        var orderedAfter = now - TrendyolInvoiceReconciliationPolicy.SweepOrderWindow;
        return db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.ConnectionId == connectionId
                && order.OrderedAt >= orderedAfter
                && !DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus))
            .Where(order => db.ShipmentPackages.Any(package => package.TenantId == tenantId
                && package.ConnectionId == connectionId
                && package.OrderId == order.Id
                && TrendyolInvoiceReconciliationPolicy.SweepStatuses.Contains(package.Status)
                && package.MarketplaceInvoiceStatus != MarketplaceInvoiceStatus.Invoiced));
    }

    private Task<int> UpdateOrderInvoiceReconciliationProgressAsync(
        Guid tenantId,
        Guid? jobId,
        int current,
        int total,
        int percent,
        string label,
        CancellationToken cancellationToken,
        int? processedCount = null)
    {
        if (jobId is null) return Task.FromResult(0);
        var processed = processedCount ?? run.UpdatedCount;

        return db.IntegrationJobs
            .Where(job => job.TenantId == tenantId
                && job.Id == jobId.Value
                && (job.JobType == MarketplaceJobTypes.OrderInvoiceReconciliation
                    || job.JobType == MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation
                    || job.JobType == MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.ProgressCurrent, current)
                .SetProperty(job => job.ProgressTotal, total)
                .SetProperty(job => job.ProgressPercent, percent)
                .SetProperty(job => job.ProgressLabel, label)
                .SetProperty(job => job.ProgressReceived, run.ReceivedCount)
                .SetProperty(job => job.ProgressProcessed, processed)
                .SetProperty(job => job.ProgressSkipped, run.SkippedCount)
                .SetProperty(job => job.ProgressFailed, run.FailedCount), cancellationToken);
    }

    // The columns the Trendyol invoice check may change on a package (invoice state) and on an order (the
    // snapshots and the storefront of a partial return-claim order). Version is listed because the shared merge bumps it.
    internal static readonly IReadOnlySet<string> PackageInvoiceColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ShipmentPackage.MarketplaceInvoiceNumber), nameof(ShipmentPackage.MarketplaceInvoiceUrl), nameof(ShipmentPackage.MarketplaceInvoiceStatus),
        nameof(ShipmentPackage.MarketplaceInvoiceRawStatus), nameof(ShipmentPackage.MarketplaceInvoiceSourceUpdatedAt), nameof(ShipmentPackage.MarketplaceInvoiceObservedAt),
        nameof(ShipmentPackage.UpdatedAt), nameof(ShipmentPackage.Version)
    };

    internal static readonly IReadOnlySet<string> OrderSnapshotColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Order.CustomerSnapshotJson), nameof(Order.ShipmentAddressSnapshotJson), nameof(Order.InvoiceAddressSnapshotJson),
        nameof(Order.LastRemoteModifiedAt), nameof(Order.UpdatedAt), nameof(Order.Version), nameof(Order.StoreFrontCode), nameof(Order.BuyerType)
    };

    internal static bool OnlyChanges(IEnumerable<string> modifiedProperties, IReadOnlySet<string> allowed) => modifiedProperties.All(allowed.Contains);

    // The Trendyol invoice check runs under its own lock group ("order-invoices"), next to the order reads.
    // It writes the package invoice columns and the order snapshots it changed without bumping their versions
    // (ExecuteUpdate), so an order read that changes other columns of the same rows meanwhile neither fails this
    // check nor is failed by it. A package invoice write only lands when the stored invoice columns are still the
    // ones this check read: a newer observation written meanwhile (by an order read or another job) is never
    // overwritten with the older one held here. On such a conflict, or when an invoice / delivery state changed
    // under it (version conflict), the checkpoint's changes are rolled back and dropped (the cursor given as
    // keep stays) and false is returned, so the caller reads that order again instead of failing the batch.
    // Everything else it changed (invoice delivery states, history, its cursor) is saved in the same transaction.
    private async Task<bool> SaveOrderInvoiceCheckpointAsync(bool isTrendyol, CancellationToken cancellationToken, IReadOnlyCollection<object>? keep = null)
    {
        if (!isTrendyol) { await db.SaveChangesAsync(cancellationToken); return true; }
        const string savepoint = "order_invoice_checkpoint";
        var outerTransaction = db.Database.CurrentTransaction;
        await using var transaction = outerTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (outerTransaction is not null) await outerTransaction.CreateSavepointAsync(savepoint, cancellationToken);
        var written = new List<EntityEntry>();
        try
        {
            if (await WriteOrderInvoiceCheckpointAsync(written, cancellationToken))
            {
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                else await outerTransaction!.ReleaseSavepointAsync(savepoint, cancellationToken);
                // Written rows are read again from the database the next time they are needed.
                foreach (var entry in written) entry.State = EntityState.Detached;
                return true;
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // An invoice or delivery state changed under this check: handled like a newer package observation.
        }

        if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
        else await outerTransaction!.RollbackToSavepointAsync(savepoint, cancellationToken);
        DiscardOrderInvoiceChanges(written, keep);
        run.SkippedCount++;
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
        return false;
    }

    private async Task<bool> WriteOrderInvoiceCheckpointAsync(List<EntityEntry> written, CancellationToken cancellationToken)
    {
        var packages = db.ChangeTracker.Entries<ShipmentPackage>()
            .Where(entry => entry.State == EntityState.Modified && OnlyChanges(entry.Properties.Where(property => property.IsModified).Select(property => property.Metadata.Name), PackageInvoiceColumns))
            .ToList();
        var orderEntries = db.ChangeTracker.Entries<Order>()
            .Where(entry => entry.State == EntityState.Modified && OnlyChanges(entry.Properties.Where(property => property.IsModified).Select(property => property.Metadata.Name), OrderSnapshotColumns))
            .ToList();
        var now = timeProvider.GetUtcNow();
        foreach (var entry in packages)
        {
            var package = entry.Entity;
            // A check that found the invoice as it was stores only its "last checked" time: no new UpdatedAt, no event.
            var realChanges = SyncNoOpWrites.RealChanges(entry);
            if (entry.State == EntityState.Unchanged) continue;
            var checkOnly = realChanges.All(name => SyncNoOpWrites.IsBookkeeping(typeof(ShipmentPackage), name));
            if (checkOnly)
            {
                package.UpdatedAt = entry.Property(x => x.UpdatedAt).OriginalValue;
                if (!realChanges.Contains(nameof(ShipmentPackage.MarketplaceInvoiceObservedAt)))
                {
                    package.Version = entry.Property(x => x.Version).OriginalValue;
                    entry.State = EntityState.Unchanged;
                    continue;
                }
            }
            var readNumber = entry.Property(x => x.MarketplaceInvoiceNumber).OriginalValue;
            var readUrl = entry.Property(x => x.MarketplaceInvoiceUrl).OriginalValue;
            var readStatus = entry.Property(x => x.MarketplaceInvoiceStatus).OriginalValue;
            var readSourceUpdatedAt = entry.Property(x => x.MarketplaceInvoiceSourceUpdatedAt).OriginalValue;
            var readObservedAt = entry.Property(x => x.MarketplaceInvoiceObservedAt).OriginalValue;
            var affected = await db.ShipmentPackages
                .Where(x => x.TenantId == package.TenantId && x.Id == package.Id
                    && x.MarketplaceInvoiceStatus == readStatus
                    && x.MarketplaceInvoiceNumber == readNumber
                    && x.MarketplaceInvoiceUrl == readUrl
                    && x.MarketplaceInvoiceSourceUpdatedAt == readSourceUpdatedAt
                    && x.MarketplaceInvoiceObservedAt == readObservedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.MarketplaceInvoiceNumber, package.MarketplaceInvoiceNumber)
                    .SetProperty(x => x.MarketplaceInvoiceUrl, package.MarketplaceInvoiceUrl)
                    .SetProperty(x => x.MarketplaceInvoiceStatus, package.MarketplaceInvoiceStatus)
                    .SetProperty(x => x.MarketplaceInvoiceRawStatus, package.MarketplaceInvoiceRawStatus)
                    .SetProperty(x => x.MarketplaceInvoiceSourceUpdatedAt, package.MarketplaceInvoiceSourceUpdatedAt)
                    .SetProperty(x => x.MarketplaceInvoiceObservedAt, package.MarketplaceInvoiceObservedAt)
                    .SetProperty(x => x.UpdatedAt, package.UpdatedAt), cancellationToken);
            // Another writer stored a newer invoice observation after this check read the package.
            if (affected == 0) return false;
            package.Version = entry.Property(x => x.Version).OriginalValue;
            entry.OriginalValues.SetValues(entry.CurrentValues);
            entry.State = EntityState.Unchanged;
            written.Add(entry);
            if (!checkOnly) AddOrderDataChangeEvent(package.TenantId, "ShipmentPackage", package.Id, package.Version, now);
        }
        foreach (var entry in orderEntries)
        {
            var order = entry.Entity;
            // The same snapshots read again (equal JSON, same change time) are not written.
            if (SyncNoOpWrites.RealChanges(entry).All(name => SyncNoOpWrites.IsBookkeeping(typeof(Order), name)))
            {
                order.UpdatedAt = entry.Property(x => x.UpdatedAt).OriginalValue;
                order.Version = entry.Property(x => x.Version).OriginalValue;
                entry.State = EntityState.Unchanged;
                continue;
            }
            var storeFrontCode = order.StoreFrontCode;
            // Written outside the change tracker, so the buyer kind (OrderBuyerTypes) is set here from the new snapshots.
            order.BuyerType = OrderBuyerTypes.Resolve(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson);
            // SIPARIS-STOK-9: the customer search name goes with the snapshots it is read from; the trigger keeps a name
            // that arrives with a new stamp (it would empty it until the hourly backfill otherwise).
            order.CustomerSearchName = OrderCustomerSearchNames.Resolve(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
            order.CustomerSearchStamp = Guid.CreateVersion7();
            var customerSearchName = order.CustomerSearchName;
            var customerSearchStamp = order.CustomerSearchStamp;
            await db.Orders.Where(x => x.TenantId == order.TenantId && x.Id == order.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CustomerSearchName, customerSearchName)
                .SetProperty(x => x.CustomerSearchStamp, customerSearchStamp)
                // An order never changes storefront: a storefront an order read stored meanwhile stays.
                .SetProperty(x => x.StoreFrontCode, x => x.StoreFrontCode ?? storeFrontCode)
                .SetProperty(x => x.CustomerSnapshotJson, order.CustomerSnapshotJson)
                .SetProperty(x => x.ShipmentAddressSnapshotJson, order.ShipmentAddressSnapshotJson)
                .SetProperty(x => x.InvoiceAddressSnapshotJson, order.InvoiceAddressSnapshotJson)
                .SetProperty(x => x.BuyerType, order.BuyerType)
                .SetProperty(x => x.LastRemoteModifiedAt, order.LastRemoteModifiedAt)
                .SetProperty(x => x.UpdatedAt, order.UpdatedAt), cancellationToken);
            order.Version = entry.Property(x => x.Version).OriginalValue;
            entry.OriginalValues.SetValues(entry.CurrentValues);
            entry.State = EntityState.Unchanged;
            written.Add(entry);
            AddOrderDataChangeEvent(order.TenantId, "Order", order.Id, order.Version, now);
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // After a rolled-back checkpoint: forget its changes and the invoice rows it read, so a retry reads them fresh.
    private void DiscardOrderInvoiceChanges(IEnumerable<EntityEntry> written, IReadOnlyCollection<object>? keep)
    {
        foreach (var entry in written) entry.State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (keep is not null && keep.Any(kept => ReferenceEquals(entry.Entity, kept))) continue;
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                || entry.Entity is ShipmentPackage or Order or Invoice or MarketplaceDeliveryState or MarketplaceDelivery)
                entry.State = EntityState.Detached;
        }
    }

    // The screens learn about a row written outside the change tracker the same way as about a saved one.
    private void AddOrderDataChangeEvent(Guid tenantId, string aggregateType, Guid aggregateId, long version, DateTimeOffset now) =>
        db.IntegrationOutboxEvents.Add(new IntegrationOutboxEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ResourceType = "orders",
            OperationType = "updated",
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            AggregateVersion = version,
            PayloadJson = JsonSerializer.Serialize(new { aggregateType, aggregateId, version }),
            CreatedAt = now,
            NextAttemptAt = now
        });

    private async Task<bool> HydrateTrendyolReturnClaimOrder(Guid tenantId, Guid connectionId, string externalOrderId, string correlationId, CancellationToken cancellationToken)
    {
        var customerSnapshot = await db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.ConnectionId == connectionId
                && order.ExternalOrderId == externalOrderId)
            .Select(order => order.CustomerSnapshotJson)
            .SingleOrDefaultAsync(cancellationToken);
        var issueKey = $"trendyol-return-order-hydration:{connectionId}:{externalOrderId}";
        if (!TrendyolJsonMapper.IsReturnClaimReadModelSnapshot(customerSnapshot))
        {
            await ResolveIssue(tenantId, issueKey, cancellationToken);
            return true;
        }
        var permanentlyUnreachable = await db.OperationalIssues.AsNoTracking()
            .AnyAsync(issue => issue.TenantId == tenantId
                && issue.Status == IssueStatus.Open
                && (issue.Code == "REMOTE_ORDER_NOT_FOUND" || issue.Code == "RETURN_ORDER_HYDRATION_INVALID")
                && issue.DedupeKey == issueKey, cancellationToken);
        if (permanentlyUnreachable) return true;

        // Hydration is opportunistic. A busy order lane must defer only this
        // candidate; the invoice cursor still advances and will wrap around.
        // The invoice check holds its own "order-invoices" lock, so this "orders" lock is a real guard: the
        // full order projection below is a versioned write that must not race the order reads.
        await using var orderSyncLock = await MarketplaceSyncExecutionLock.TryAcquireAsync(
            db, connectionId, MarketplaceJobTypes.OrderSync, cancellationToken);
        if (orderSyncLock is null)
        {
            run.SkippedCount++;
            await RecordIssue(tenantId, issueKey, "RETURN_ORDER_HYDRATION_BUSY", "Sipariş eşitlemesi sürdüğü için bu kayıt atlandı; fatura taraması ilerledi ve sonraki döngüde yeniden denenecek.", cancellationToken);
            return false;
        }

        TrackRequest();
        var result = await orders.GetAsync(
            Context(tenantId, connectionId, correlationId, $"trendyol-return-order-hydration:{externalOrderId}"),
            externalOrderId,
            await OrderHint(tenantId, connectionId, externalOrderId, cancellationToken),
            cancellationToken);
        if (!result.IsSuccess)
        {
            TrackResultFailure(result.Error);
            await RecordIssue(
                tenantId,
                issueKey,
                result.Error!.Code,
                $"İade kaydından oluşturulan kısmi sipariş Trendyol'dan tamamlanamadı; otomatik taramada tekrar denenecek. {result.Error.SafeMessage}",
                cancellationToken);
            return true;
        }

        TrackReceived();
        if (await UpsertOrder(tenantId, connectionId, result.Value!, cancellationToken))
            await ResolveIssue(tenantId, issueKey, cancellationToken);
        else
            await RecordIssue(
                tenantId,
                issueKey,
                "RETURN_ORDER_HYDRATION_INVALID",
                "Trendyol sipariş yanıtı eksik satır veya paket verisi içerdiği için kısmi iade siparişi güncellenmedi.",
                cancellationToken);
        return true;
    }

    // ADAPTOR-TY-HB-3: a Trendyol package read that sent no request because the package is outside every readable window.
    // The adapter keeps its NotFound/404 class; callers that would take NotFound as "Trendyol has no such package" check
    // this first, since the package's state is unknown.
    internal static bool TrendyolPackageNotAsked(AdapterError? error) =>
        string.Equals(error?.Code, TrendyolHttpClient.PackageNotAddressableCode, StringComparison.Ordinal);

    // A sweep reads only the order's packages still uninvoiced on Trendyol; an unconfirmed local
    // invoice or a requested refresh reads every package of the order.
    private async Task ReconcileTrendyolPackageInvoices(Guid tenantId, Guid connectionId, string externalOrderId, string correlationId, bool sweep, IReadOnlySet<Guid> alreadyRead, CancellationToken cancellationToken)
    {
        var packages = from package in db.ShipmentPackages.AsNoTracking()
                       join order in db.Orders.AsNoTracking()
                           on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                       where package.TenantId == tenantId
                           && package.ConnectionId == connectionId
                           && package.Status != ShipmentPackageStatus.Cancelled
                           && order.ExternalOrderId == externalOrderId
                       select package;
        if (sweep)
            packages = packages.Where(package => TrendyolInvoiceReconciliationPolicy.SweepStatuses.Contains(package.Status)
                && package.MarketplaceInvoiceStatus != MarketplaceInvoiceStatus.Invoiced);
        var hint = await OrderHint(tenantId, connectionId, externalOrderId, cancellationToken);
        var candidates = await packages
            .Select(package => new { package.Id, package.ExternalPackageId, package.StatusOccurredAt })
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates.Where(candidate => !alreadyRead.Contains(candidate.Id)))
        {
            TrackRequest();
            var readback = await orders.GetShipmentPackageAsync(
                Context(tenantId, connectionId, correlationId, $"trendyol-invoice-package-read:{candidate.ExternalPackageId}"),
                candidate.ExternalPackageId,
                new PackageLookup(candidate.StatusOccurredAt, hint, InvoiceOnly: true),
                cancellationToken);
            if (!readback.IsSuccess)
            {
                var issueKey = $"trendyol-invoice-package-read:{connectionId}:{candidate.Id}";
                if (TrendyolPackageNotAsked(readback.Error))
                {
                    // ADAPTOR-TY-HB-3: the package lies outside every window Trendyol can be asked in, so nothing was
                    // asked and its invoice state is unknown, not "removed by Trendyol". The issue stays on the invoice
                    // screen (InvoiceWorkspaceReadIssuePolicy shows this code) and the last known state is kept.
                    await RecordIssue(tenantId, issueKey, readback.Error!.Code,
                        $"Paket fatura durumu Trendyol'dan doğrulanamadı. {readback.Error.SafeMessage}", cancellationToken);
                    continue;
                }
                if (readback.Error?.Class == AdapterErrorClass.NotFound || readback.Error?.HttpStatus == 404)
                {
                    // Trendyol removes older package records from the package
                    // endpoint. Keep our last known invoice state, but do not
                    // surface an unavailable historical package as a live
                    // refresh failure in the invoice workspace.
                    await ResolveIssue(tenantId, issueKey, cancellationToken);
                    continue;
                }
                TrackResultFailure(readback.Error);
                await RecordIssue(
                    tenantId,
                    issueKey,
                    readback.Error!.Code,
                    $"Paket fatura durumu Trendyol'dan doğrulanamadı; sonraki otomatik taramada tekrar denenecek. {readback.Error.SafeMessage}",
                    cancellationToken);
                continue;
            }

            TrackReceived();
            var package = await db.ShipmentPackages.SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == candidate.Id,
                cancellationToken);
            if (package is null) continue;

            await MergeMarketplaceInvoiceState(package, readback.Value!.Package, cancellationToken, "reconciliation");
            await ResolveIssue(tenantId, $"trendyol-invoice-package-read:{connectionId}:{candidate.Id}", cancellationToken);
            var orderSnapshot = readback.Value.OrderSnapshot;
            if (orderSnapshot is not null)
            {
                var order = await db.Orders.SingleOrDefaultAsync(
                    x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == package.OrderId,
                    cancellationToken);
                if (order is not null && TrendyolJsonMapper.IsReturnClaimReadModelSnapshot(order.CustomerSnapshotJson))
                {
                    // The package read fills a claim-built order's customer and addresses, and names its storefront: the one
                    // the package was found under, else the package's orderCountryCode. The order keeps its RETURN_CLAIM mark:
                    // its lines are still the claim's, so only the hydration's full order read completes it (and removes the
                    // mark); without the mark the hydration never ran again and the order kept no storefront.
                    var storeFrontCode = order.StoreFrontCode
                        ?? (string.IsNullOrWhiteSpace(orderSnapshot.StoreFrontCode) ? TrendyolWebhookStorefront(orderSnapshot.RawJson) : orderSnapshot.StoreFrontCode.Trim().ToUpperInvariant());
                    var customerSnapshot = TrendyolJsonMapper.WithReturnClaimReadModelMark(orderSnapshot.CustomerSnapshotJson);
                    var shipmentAddressSnapshot = !string.IsNullOrWhiteSpace(orderSnapshot.ShipmentAddressSnapshotJson) && orderSnapshot.ShipmentAddressSnapshotJson != "{}"
                        ? orderSnapshot.ShipmentAddressSnapshotJson
                        : order.ShipmentAddressSnapshotJson;
                    var invoiceAddressSnapshot = !string.IsNullOrWhiteSpace(orderSnapshot.InvoiceAddressSnapshotJson) && orderSnapshot.InvoiceAddressSnapshotJson != "{}"
                        ? orderSnapshot.InvoiceAddressSnapshotJson
                        : order.InvoiceAddressSnapshotJson;
                    var lastRemoteModifiedAt = orderSnapshot.LastModifiedAt > order.LastRemoteModifiedAt ? orderSnapshot.LastModifiedAt : order.LastRemoteModifiedAt;
                    // The mark stays, so the same package read comes back on later sweeps: write only what changed.
                    if (storeFrontCode != order.StoreFrontCode || customerSnapshot != order.CustomerSnapshotJson || shipmentAddressSnapshot != order.ShipmentAddressSnapshotJson
                        || invoiceAddressSnapshot != order.InvoiceAddressSnapshotJson || lastRemoteModifiedAt != order.LastRemoteModifiedAt)
                    {
                        order.StoreFrontCode = storeFrontCode;
                        order.CustomerSnapshotJson = customerSnapshot;
                        order.ShipmentAddressSnapshotJson = shipmentAddressSnapshot;
                        order.InvoiceAddressSnapshotJson = invoiceAddressSnapshot;
                        order.LastRemoteModifiedAt = lastRemoteModifiedAt;
                        order.UpdatedAt = timeProvider.GetUtcNow();
                        order.Version++;
                        run.UpdatedCount++;
                    }
                }
            }
        }
    }

    private async Task<bool> MergeHepsiburadaOrderInvoiceState(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken)
    {
        string? orderInvoiceStatus;
        IReadOnlyDictionary<string, string> packageInvoiceStatuses;
        try
        {
            using var snapshot = JsonDocument.Parse(remote.RawJson);
            orderInvoiceStatus = HepsiburadaJsonMapper.InvoiceStatus(snapshot.RootElement);
            packageInvoiceStatuses = HepsiburadaJsonMapper.PackageInvoiceStatuses(snapshot.RootElement);
        }
        catch (JsonException)
        {
            orderInvoiceStatus = null;
            packageInvoiceStatuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var order = await db.Orders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == remote.ExternalOrderId, cancellationToken);
        if (order is null) return false;

        var packages = await db.ShipmentPackages
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.OrderId == order.Id && x.Status != ShipmentPackageStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var allPackagesObserved = packages.Count > 0;
        foreach (var package in packages)
        {
            var packageSnapshot = remote.Packages.FirstOrDefault(candidate =>
                string.Equals(candidate.ExternalPackageId, package.ExternalPackageId, StringComparison.OrdinalIgnoreCase));
            var invoiceObservation = packageSnapshot?.Invoice;
            var rawStatus = invoiceObservation?.RawStatus
                ?? packageInvoiceStatuses.GetValueOrDefault(package.ExternalPackageId)
                ?? orderInvoiceStatus;
            if (rawStatus is null && invoiceObservation?.InvoiceNumber is null && invoiceObservation?.InvoiceUrl is null)
            {
                allPackagesObserved = false;
                continue;
            }

            if (rawStatus is not null && string.IsNullOrWhiteSpace(invoiceObservation?.RawStatus))
                invoiceObservation = new RemotePackageInvoiceObservation(
                    rawStatus,
                    invoiceObservation?.InvoiceNumber,
                    invoiceObservation?.InvoiceUrl,
                    invoiceObservation?.SourceUpdatedAt ?? remote.LastModifiedAt);
            if (rawStatus is null) allPackagesObserved = false;
            var remotePackage = new RemotePackage(
                package.ExternalPackageId,
                null,
                package.RawStatus,
                remote.LastModifiedAt,
                package.CargoProviderExternalId,
                package.CargoTrackingNumber,
                [],
                Invoice: invoiceObservation);
            await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken);
        }

        return allPackagesObserved;
    }

    private async Task ReconcileHepsiburadaMissingInvoiceStatuses(
        Guid tenantId,
        Guid connectionId,
        string correlationId,
        IReadOnlyCollection<string> externalOrderIds,
        CancellationToken cancellationToken)
    {
        const int pageSize = 50;
        const int maxPages = 100;
        const string issueKeySuffix = "invoice-status-contract";
        var issueKey = $"order-invoice-reconciliation:{connectionId}:{issueKeySuffix}";
        var missingInvoicePackageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingInvoiceOrderNumbers = new List<(string OrderNumber, string? PackageNumber)>();
        var completeFeed = false;
        // The list is read by offset and shrinks while it is read (an invoice uploaded between two pages moves every
        // later package one place up, so one is never seen), and a package missing from it counts as invoiced. It is
        // therefore read twice, every page of a pass must give the pass's first total, and a package counts as listed
        // when either pass saw it (before 6 Nov 2026: one pass, totals not compared).
        for (var pass = 0; pass < HepsiburadaMissingInvoiceStatusPolicy.Passes; pass++)
        {
            var cursor = "0";
            int? firstTotal = null;
            completeFeed = false;
            for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
            {
                TrackRequest();
                var result = await hepsiburadaInvoiceStatus.ListMissingInvoicePackagesAsync(
                    Context(tenantId, connectionId, correlationId, $"hepsiburada-missing-invoices:{cursor}"),
                    new AdapterPageRequest(cursor, pageSize),
                    cancellationToken);
                if (!result.IsSuccess)
                {
                    TrackResultFailure(result.Error);
                    var error = result.Error!;
                    var status = error.HttpStatus is { } httpStatus ? $" (HTTP {(int)httpStatus})" : string.Empty;
                    await RecordIssue(tenantId, issueKey, error.Code,
                        $"Hepsiburada faturasız sipariş listesi okunamadı{status}: {error.SafeMessage} Eksik alanlı siparişlerin durumu değiştirilmedi.", cancellationToken);
                    return;
                }

                TrackReceived();
                var page = result.Value!;
                foreach (var item in page.Items)
                {
                    if (missingInvoicePackageKeys.Add(HepsiburadaMissingInvoiceStatusPolicy.Key(item.OrderNumber, item.PackageNumber)))
                        missingInvoiceOrderNumbers.Add((item.OrderNumber, item.PackageNumber));
                }
                if (!HepsiburadaMissingInvoiceStatusPolicy.SameTotal(ref firstTotal, page.TotalCount, pageIndex))
                {
                    await RecordIssue(tenantId, issueKey, "HEPSIBURADA_MISSING_INVOICE_FEED_MOVED",
                        "Hepsiburada faturasız sipariş listesi okunurken değişti (sayfaların toplamı farklı); eksik alanlı siparişlerin durumu değiştirilmedi, sonraki turda yeniden okunacak.", cancellationToken);
                    return;
                }

                if (!page.HasMore)
                {
                    completeFeed = true;
                    break;
                }

                if (string.IsNullOrWhiteSpace(page.NextCursor) || string.Equals(cursor, page.NextCursor, StringComparison.Ordinal))
                {
                    await RecordIssue(tenantId, issueKey, "HEPSIBURADA_MISSING_INVOICE_FEED_INCOMPLETE",
                        "Hepsiburada faturasız sipariş listesinin sayfaları tamamlanamadı; eksik alanlı siparişlerin durumu değiştirilmedi.", cancellationToken);
                    return;
                }
                cursor = page.NextCursor;
            }

            if (!completeFeed)
            {
                await RecordIssue(tenantId, issueKey, "HEPSIBURADA_MISSING_INVOICE_FEED_INCOMPLETE",
                    "Hepsiburada faturasız sipariş listesi güvenli sayfa sınırına kadar tamamlanmadı; eksik alanlı siparişlerin durumu değiştirilmedi.", cancellationToken);
                return;
            }
        }

        // Orders Hepsiburada lists without an invoice but the panel does not have (packaged before
        // the paid-order feed saw them) are read now.
        await ReadMissingHepsiburadaOrdersAsync(tenantId, connectionId, correlationId, missingInvoiceOrderNumbers, cancellationToken);
        if (externalOrderIds.Count == 0) return;

        var ordersToReconcile = await db.Orders
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalOrderIds.Contains(x.ExternalOrderId))
            .Select(x => new { x.Id, x.ExternalOrderId, x.OrderedAt })
            .ToListAsync(cancellationToken);
        if (ordersToReconcile.Count == 0)
        {
            await RecordIssue(tenantId, issueKey, "HEPSIBURADA_INVOICE_STATUS_UNAVAILABLE",
                "Hepsiburada sipariş detayında fatura durumu yok ve eşleşen yerel sipariş bulunamadı.", cancellationToken);
            return;
        }

        var orderById = ordersToReconcile.ToDictionary(x => x.Id);
        var orderIds = orderById.Keys.ToArray();
        var packages = await db.ShipmentPackages
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && orderIds.Contains(x.OrderId) && x.Status != ShipmentPackageStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var unresolved = false;
        foreach (var package in packages)
        {
            if (!orderById.TryGetValue(package.OrderId, out var order))
            {
                unresolved = true;
                continue;
            }

            var status = HepsiburadaMissingInvoiceStatusPolicy.Resolve(
                order.ExternalOrderId,
                package.ExternalPackageId,
                order.OrderedAt,
                now,
                missingInvoicePackageKeys,
                completeFeed);
            if (status is null)
            {
                unresolved = true;
                continue;
            }

            var remotePackage = new RemotePackage(
                package.ExternalPackageId,
                null,
                package.RawStatus,
                package.StatusOccurredAt,
                package.CargoProviderExternalId,
                package.CargoTrackingNumber,
                [],
                Invoice: new RemotePackageInvoiceObservation(status, null, null, null));
            await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken);
        }

        if (unresolved)
            await RecordIssue(tenantId, issueKey, "HEPSIBURADA_INVOICE_STATUS_UNAVAILABLE",
                "Sipariş detayı fatura durumu sağlamadı; faturasız liste yalnızca son 1 ayı kapsadığı için bu dönemin dışındaki kayıtların durumu korunuyor.", cancellationToken);
        else
            await ResolveIssue(tenantId, issueKey, cancellationToken);
    }
}
