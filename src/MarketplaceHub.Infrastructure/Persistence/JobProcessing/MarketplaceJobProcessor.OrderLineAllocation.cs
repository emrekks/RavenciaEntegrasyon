using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // The variant links and panel variants an order read's lines may name, read once per read.
    private sealed record OrderLineVariantCandidates(IReadOnlyCollection<OrderLineLinkCandidate> Links, IReadOnlyCollection<OrderLinePanelCandidate> Variants)
    {
        public static readonly OrderLineVariantCandidates Empty = new([], []);
        public OrderLineVariantMatch Match(OrderLineCodes codes) => OrderLineVariantMatcher.Match(codes, Links, Variants);
    }

    private static List<OrderLineCodes> OrderLineCodesOf(string? platformCode, IEnumerable<RemoteOrderLine> lines) =>
        lines.Select(line => OrderLineVariantMatcher.Codes(platformCode, line.Sku, line.Barcode, line.SourceSnapshotJson)).ToList();

    private async Task<OrderLineVariantCandidates> LoadOrderLineVariantCandidates(Guid tenantId, Guid connectionId, IReadOnlyCollection<OrderLineCodes> lines, CancellationToken cancellationToken)
    {
        // Links are compared trimmed and upper-cased (OrderLineVariantMatcher.LinkKey); panel keys in every stored
        // form, since a stored key may still be in a legacy form until the variant is saved again (K1).
        var linkCodes = OrderLineVariantMatcher.LinkLookupCodes(lines);
        var panelKeys = OrderLineVariantMatcher.PanelLookupKeys(lines);
        if (linkCodes.Length == 0 && panelKeys.Length == 0) return OrderLineVariantCandidates.Empty;
        List<OrderLineLinkCandidate> links = linkCodes.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                    && ((x.ExternalBarcode != null && linkCodes.Contains(x.ExternalBarcode.Trim().ToUpper()))
                        || (x.ExternalSku != null && linkCodes.Contains(x.ExternalSku.Trim().ToUpper()))
                        || linkCodes.Contains(x.ExternalId.Trim().ToUpper())))
                .OrderBy(x => x.Id)
                .Select(x => new OrderLineLinkCandidate(x.VariantId, x.ExternalId, x.ExternalBarcode, x.ExternalSku))
                .ToListAsync(cancellationToken);
        List<OrderLinePanelCandidate> variants = panelKeys.Length == 0
            ? []
            : await db.ProductVariants.AsNoTracking()
                .Where(x => x.TenantId == tenantId && (panelKeys.Contains(x.SkuNormalized) || (x.BarcodeNormalized != null && panelKeys.Contains(x.BarcodeNormalized))))
                .OrderBy(x => x.Id)
                .Select(x => new OrderLinePanelCandidate(x.Id, x.SkuNormalized, x.BarcodeNormalized))
                .ToListAsync(cancellationToken);
        return new OrderLineVariantCandidates(links, variants);
    }

    // A line that finds its variant only after it was stored (OrderLineLateMatchStockPolicy): when its order has
    // already shipped, been delivered, returned or cancelled, the line's stock baseline is what it commits, so its first
    // projection takes nothing from stock and later changes move stock from there. A line that already has stock
    // movements, a reservation or the carry rows of a reset under its marketplace ids is left to ProjectOrderReservations.
    private async Task AdoptLateMatchedLineStock(Guid tenantId, Guid connectionId, IReadOnlyCollection<OrderLine> lines, CancellationToken cancellationToken)
    {
        var candidates = lines.Where(line => line.VariantId is not null).DistinctBy(line => line.Id).ToList();
        if (candidates.Count == 0) return;
        var orderIds = candidates.Select(line => line.OrderId).Distinct().ToArray();
        var orders = db.Orders.Local.Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id))
            .ToDictionary(x => x.Id, x => (x.DerivedStatus, x.ExternalOrderId));
        var unloaded = orderIds.Where(id => !orders.ContainsKey(id)).ToArray();
        if (unloaded.Length > 0)
            foreach (var row in await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && unloaded.Contains(x.Id)).Select(x => new { x.Id, x.DerivedStatus, x.ExternalOrderId }).ToListAsync(cancellationToken))
                orders[row.Id] = (row.DerivedStatus, row.ExternalOrderId);
        var settled = candidates.Where(line => orders.TryGetValue(line.OrderId, out var order) && OrderLineLateMatchStockPolicy.OrderHasSettled(order.DerivedStatus)).ToList();
        if (settled.Count == 0) return;
        var variantIds = settled.Select(line => line.VariantId!.Value).Distinct().ToArray();
        var itemByVariant = (await db.InventoryItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
                .Select(x => new { x.Id, x.VariantId })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.VariantId, x => x.Id);
        foreach (var item in db.InventoryItems.Local.Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))) itemByVariant.TryAdd(item.VariantId, item.Id);
        var sourceKeys = settled.ToDictionary(line => line.Id, line => new[] { line.Id.ToString("D"), OrderInventoryStockPolicy.ResetCarryKey(connectionId, orders[line.OrderId].ExternalOrderId, line.ExternalLineId) });
        var sourceIds = sourceKeys.Values.SelectMany(keys => keys).Distinct(StringComparer.Ordinal).ToArray();
        var recorded = (await db.StockLedgerEntries.AsNoTracking().Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && sourceIds.Contains(x.SourceId)).Select(x => x.SourceId).Distinct().ToListAsync(cancellationToken))
            .Concat(await db.StockReservations.AsNoTracking().Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && sourceIds.Contains(x.SourceId)).Select(x => x.SourceId).Distinct().ToListAsync(cancellationToken))
            .Concat(db.StockLedgerEntries.Local.Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && sourceIds.Contains(x.SourceId)).Select(x => x.SourceId))
            .ToHashSet(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        foreach (var line in settled)
        {
            if (!itemByVariant.TryGetValue(line.VariantId!.Value, out var itemId) || sourceKeys[line.Id].Any(recorded.Contains)) continue;
            var sourceId = line.Id.ToString("D");
            StockMovementWriter.Record(db, tenantId, itemId, OrderLineLateMatchStockPolicy.AdoptedBaseline(line.OrderedQuantity, line.CancelledQuantity, line.ShippedQuantity, line.DeliveredQuantity), BaselineSource(itemId, sourceId, line.OrderId, now, now));
            recorded.Add(sourceId);
        }
    }

    internal const string UnmatchedOrderLineIssueCode = "ORDER_LINE_VARIANT_UNMATCHED";
    private const int OrderLineRematchBatchSize = 2000;

    internal sealed record OrderLineRematchOutcome(int Examined, int Matched, int Unmatched);

    // An order line is matched when a read stores it, and a delivered order is rarely read again: a line imported
    // before its panel variant or variant link existed (a catalog built after the orders, a variant added later) would
    // stay without stock effect for good. Every order sync of the connection matches its unmatched lines again against
    // the panel as it is now (the same OrderLineVariantMatcher), takes their stock like a late read would
    // (OrderLineLateMatchStockPolicy, ProjectOrderReservations) and keeps one open issue per code no variant answers,
    // so the owner can create or map it. Idempotent: a matched line is never looked at again.
    // SIPARIS-STOK-2: a connection with more unmatched lines than one batch is read in turns. Each run reads the next
    // batch after the last line Id the cursor keeps (fully cancelled lines are left out: they take no stock); a batch of
    // fewer lines ends the pass and the next run starts again from the first. The issues of codes no longer unmatched are
    // resolved when a whole pass has been read: the cursor's LastModifiedWatermark keeps when the pass started, every
    // batch of a pass marks the issues it still sees, and the last batch resolves the ones no batch saw.
    internal const string OrderLineRematchCursor = "ORDER_LINE_REMATCH";

    internal async Task<OrderLineRematchOutcome> RematchUnmatchedOrderLines(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, int batchSize = OrderLineRematchBatchSize)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (platformCode is null) return new(0, 0, 0);
        var cursor = await Cursor(tenantId, connectionId, OrderLineRematchCursor, cancellationToken);
        var afterId = Guid.TryParse(cursor.OpaqueCursor, out var lastId) ? lastId : Guid.Empty;
        var passStartedAt = afterId == Guid.Empty ? timeProvider.GetUtcNow() : cursor.LastModifiedWatermark ?? timeProvider.GetUtcNow();
        var unmatchedLines = from line in db.OrderLines
                             join order in db.Orders on new { line.TenantId, line.OrderId } equals new { order.TenantId, OrderId = order.Id }
                             where line.TenantId == tenantId && order.ConnectionId == connectionId && line.VariantId == null
                                 && (line.OrderedQuantity <= 0 || line.CancelledQuantity < line.OrderedQuantity)
                             select new { Line = line, order.OrderNumber, order.LastRemoteModifiedAt };
        if (afterId != Guid.Empty) unmatchedLines = unmatchedLines.Where(x => x.Line.Id.CompareTo(afterId) > 0);
        var rows = await unmatchedLines.OrderBy(x => x.Line.Id).Take(batchSize).ToListAsync(cancellationToken);
        var passEnded = rows.Count < batchSize;
        // A pass read in one batch knows every open code at once; only a pass of several batches marks what it saw.
        var multiBatchPass = afterId != Guid.Empty || !passEnded;
        cursor.OpaqueCursor = passEnded ? null : rows[^1].Line.Id.ToString("D");
        cursor.LastModifiedWatermark = passEnded ? null : passStartedAt;
        cursor.LastAttemptAt = timeProvider.GetUtcNow();
        var issuePrefix = $"order-line-variant-unmatched:{connectionId:N}:";
        var codes = rows.ToDictionary(row => row.Line.Id, row => OrderLineVariantMatcher.Codes(platformCode, row.Line.Sku, row.Line.Barcode, row.Line.SourceSnapshotJson));
        var candidates = rows.Count == 0 ? OrderLineVariantCandidates.Empty : await LoadOrderLineVariantCandidates(tenantId, connectionId, codes.Values, cancellationToken);
        var matched = new List<(OrderLine Line, DateTimeOffset ModifiedAt)>();
        var unmatched = new List<(OrderLine Line, string OrderNumber, OrderLineMatchKind Kind)>();
        foreach (var row in rows)
        {
            var match = candidates.Match(codes[row.Line.Id]);
            if (match.VariantId is { } variantId)
            {
                row.Line.VariantId = variantId;
                row.Line.Version++;
                matched.Add((row.Line, row.LastRemoteModifiedAt));
            }
            else unmatched.Add((row.Line, row.OrderNumber, match.Kind));
        }
        if (matched.Count > 0)
        {
            await AdoptLateMatchedLineStock(tenantId, connectionId, matched.Select(x => x.Line).ToList(), cancellationToken);
            await OrderLineCostSnapshot.TakeAsync(db, tenantId, matched.Select(x => x.Line), cancellationToken);
            await ProjectOrderReservations(tenantId, connectionId, matched, cancellationToken);
        }

        var openKeys = new HashSet<string>(StringComparer.Ordinal);
        // A line cancelled in full takes no stock whether it matches or not: it is not read here (SIPARIS-STOK-2: such
        // lines filled the first batch for good) and raises no notice; the notice it raised before is resolved below
        // (live 5 Nov 2026: Shopify #R1013's two cancelled lines kept two open notices).
        foreach (var group in unmatched.Where(x => !OrderLineFullyCancelled(x.Line)).GroupBy(x => UnmatchedOrderLineIssueKey(codes[x.Line.Id]), StringComparer.Ordinal))
        {
            var key = issuePrefix + group.Key;
            openKeys.Add(key);
            await RecordUnmatchedOrderLineIssue(tenantId, key, UnmatchedOrderLineSummary(platformCode, codes[group.First().Line.Id], group.First().Line.TitleSnapshot, group.Select(x => x.OrderNumber).ToList(), group.Any(x => x.Kind == OrderLineMatchKind.Ambiguous)), cancellationToken, multiBatchPass ? passStartedAt : null);
        }
        // Issues of codes that now match (or whose lines are gone) are resolved once a whole pass was read: a pass of one
        // batch resolves every issue it did not see; the last batch of a longer pass those no batch of it saw
        // (LastSeenAt before the pass started). A batch in the middle resolves only the codes it matched.
        var resolvable = !passEnded
            ? await QueryResolvableIssues(matched.Select(x => issuePrefix + UnmatchedOrderLineIssueKey(codes[x.Line.Id])).Distinct(StringComparer.Ordinal).ToArray())
            : multiBatchPass
                ? await db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Code == UnmatchedOrderLineIssueCode && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(issuePrefix) && x.LastSeenAt < passStartedAt).ToListAsync(cancellationToken)
                : await db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Code == UnmatchedOrderLineIssueCode && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(issuePrefix)).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var issue in resolvable.Where(issue => !openKeys.Contains(issue.DedupeKey)))
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(rows.Count, matched.Count, unmatched.Count);

        Task<List<OperationalIssue>> QueryResolvableIssues(string[] keys) => keys.Length == 0
            ? Task.FromResult(new List<OperationalIssue>())
            : db.OperationalIssues.Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && keys.Contains(x.DedupeKey)).ToListAsync(cancellationToken);
    }

    internal static bool OrderLineFullyCancelled(OrderLine line) => line.OrderedQuantity > 0 && line.CancelledQuantity >= line.OrderedQuantity;

    private static string UnmatchedOrderLineIssueKey(OrderLineCodes codes) =>
        codes.DisplayCode is { } code && CatalogKeyNormalizer.Key(code, 120) is { Length: > 0 } key ? key : "-";

    internal static string UnmatchedOrderLineSummary(string platformCode, OrderLineCodes codes, string? title, IReadOnlyList<string> orderNumbers, bool ambiguous)
    {
        var numbers = orderNumbers.Where(number => !string.IsNullOrWhiteSpace(number)).Distinct(StringComparer.Ordinal).ToList();
        var orders = numbers.Count <= 5 ? string.Join(", ", numbers) : $"{string.Join(", ", numbers.Take(5))} ve {numbers.Count - 5} sipariş daha";
        var code = codes.DisplayCode is { } displayCode ? $"{displayCode} kodlu" : "kodsuz";
        var product = string.IsNullOrWhiteSpace(title) ? "" : $" ({title.Trim()})";
        var marketplace = IssueMarketplaceName(platformCode);
        return ambiguous
            ? $"Eşleşmeyen sipariş satırı: {marketplace} siparişi {orders} içindeki {code} ürün{product} paneldeki birden fazla varyanta uyuyor; hangisi olduğu belirsiz olduğu için satır stoğu etkilemiyor. Kodların bir varyanta özgü olmasını sağlayın."
            : $"Eşleşmeyen sipariş satırı: {marketplace} siparişi {orders} içindeki {code} ürün{product} paneldeki hiçbir varyanta eşlenemedi; satır stoğu etkilemiyor. Bu kodla bir varyant oluşturun ya da pazaryeri ürününü bir varyanta eşleyin.";
    }

    private static string IssueMarketplaceName(string? platformCode) => platformCode switch
    {
        "TRENDYOL" => "Trendyol",
        "HEPSIBURADA" => "Hepsiburada",
        "N11" => "N11",
        "PAZARAMA" => "Pazarama",
        "PTTAVM" => "PttAVM",
        "SHOPIFY" => "Shopify",
        null or "" => "Pazaryeri",
        _ => platformCode
    };

    // The notice of an order line stock could not fully give names the order and the code, so the owner knows which
    // count to check (live 5 Nov 2026: 29 notices that said only "1 adet stoktan düşülemedi").
    internal static string StockShortfallSummary(string? platformCode, string? orderNumber, string? code, decimal shortfall)
    {
        var order = string.IsNullOrWhiteSpace(orderNumber) ? $"{IssueMarketplaceName(platformCode)} siparişindeki" : $"{IssueMarketplaceName(platformCode)} siparişi {orderNumber.Trim()} içindeki";
        var product = string.IsNullOrWhiteSpace(code) ? "kodsuz ürünün" : $"{code.Trim()} kodlu ürünün";
        var units = shortfall.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        return $"Sipariş stoğu kısmen düşürüldü: {order} {product} paneldeki fiziksel stoğu yetersizdi, {units} adet stoktan düşülemedi. Paneldeki stok sayısını kontrol edin. Rezervasyon tutulmaz; stok sıfırın altına inmedi (bilgi amaçlı).";
    }

    // Read once per connection for the processor's life (ISLEYICI-3/8, 15 Nov 2026): a connection never changes its
    // marketplace. A connection not found is not remembered (null).
    private readonly Dictionary<(Guid TenantId, Guid ConnectionId), string> connectionPlatformCodes = [];

    private async Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (connectionPlatformCodes.TryGetValue((tenantId, connectionId), out var known)) return known;
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platformCode is not null) connectionPlatformCodes[(tenantId, connectionId)] = platformCode;
        return platformCode;
    }

    private async Task<string?> OrderNumberOf(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        db.Orders.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Id == orderId)?.OrderNumber
        ?? await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == orderId).Select(x => x.OrderNumber).SingleOrDefaultAsync(cancellationToken);

    // One issue per unmatched code of a connection. A pass that finds the same lines again only keeps it alive (the
    // retention resolves issues not seen for days) at most hourly; a new order with the code, or an issue resolved
    // meanwhile, reopens it.
    // passStartedAt: the start of a rematch pass read in several batches; an issue this pass sees is marked seen in it,
    // so the pass's last batch does not resolve it.
    private async Task RecordUnmatchedOrderLineIssue(Guid tenantId, string key, string summary, CancellationToken cancellationToken, DateTimeOffset? passStartedAt = null)
    {
        var now = timeProvider.GetUtcNow();
        var issue = db.OperationalIssues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.DedupeKey == key)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = UnmatchedOrderLineIssueCode, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            return;
        }
        if (issue.Status == IssueStatus.Resolved || !string.Equals(issue.Summary, summary, StringComparison.Ordinal))
        {
            issue.Status = IssueStatus.Open;
            issue.ResolvedAt = null;
            issue.Code = UnmatchedOrderLineIssueCode;
            issue.Summary = summary;
            issue.LastSeenAt = now;
            issue.OccurrenceCount++;
        }
        else if (now - issue.LastSeenAt >= TimeSpan.FromHours(1) || passStartedAt is { } started && issue.LastSeenAt < started) issue.LastSeenAt = now;
    }

    // A package line has one allocation (T2-DO1): every later event of the package updates it in place. Before, each event
    // added a row of its own (live 5 Nov 2026: 874 extra rows on 371 package lines, up to 7 for a one-unit line), so a sum
    // over the rows counted a line several times.
    private static string AllocationKey(Guid packageId, Guid orderLineId) => $"{packageId:D}:{orderLineId:D}";

    // The allocation of each package line, the latest event's where rows from before the one-row rule remain.
    internal static Dictionary<string, PackageLineAllocation> AllocationsByLine(IEnumerable<PackageLineAllocation> allocations) => allocations
        .GroupBy(allocation => AllocationKey(allocation.PackageId, allocation.OrderLineId), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group
            .OrderByDescending(allocation => PackageEventInstant(allocation.SourceEventId))
            .ThenByDescending(allocation => allocation.SourceEventId, StringComparer.Ordinal)
            .ThenByDescending(allocation => allocation.Id)
            .First(), StringComparer.Ordinal);

    // Package events are "{externalPackageId}:{unix ms}" (PackageIngestionSafety.EventId); anything else sorts first.
    internal static long PackageEventInstant(string? sourceEventId)
    {
        if (string.IsNullOrEmpty(sourceEventId)) return long.MinValue;
        var separator = sourceEventId.LastIndexOf(':');
        return separator >= 0 && long.TryParse(sourceEventId.AsSpan(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds) ? milliseconds : long.MinValue;
    }

    // Writes an event's quantities to the package line's one allocation, adding it when the line has none. Callers
    // write the event the package's status was just set to (or one the line has no allocation for yet), so the row
    // always names the package's current event, the one the line projection reads (PackageLineProjectionPolicy).
    // Returns true when a row was added.
    private bool UpsertLineAllocation(Guid tenantId, Guid packageId, Guid orderLineId, string eventId, decimal allocated, decimal cancelled, decimal shipped, decimal delivered, decimal returned, Dictionary<string, PackageLineAllocation> allocationsByKey)
    {
        var key = AllocationKey(packageId, orderLineId);
        if (allocationsByKey.GetValueOrDefault(key) is not { } held)
        {
            var added = new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = packageId, OrderLineId = orderLineId, SourceEventId = eventId, AllocatedQuantity = allocated, CancelledQuantity = cancelled, ShippedQuantity = shipped, DeliveredQuantity = delivered, ReturnedQuantity = returned };
            db.PackageLineAllocations.Add(added);
            allocationsByKey[key] = added;
            return true;
        }
        // Rows are read without tracking; the one written is attached (or its tracked twin taken) so the update is saved.
        var allocation = db.PackageLineAllocations.Local.FirstOrDefault(x => x.Id == held.Id) ?? db.PackageLineAllocations.Attach(held).Entity;
        allocationsByKey[key] = allocation;
        if (allocation.SourceEventId == eventId && allocation.AllocatedQuantity == allocated && allocation.CancelledQuantity == cancelled
            && allocation.ShippedQuantity == shipped && allocation.DeliveredQuantity == delivered && allocation.ReturnedQuantity == returned) return false;
        allocation.SourceEventId = eventId;
        allocation.AllocatedQuantity = allocated;
        allocation.CancelledQuantity = cancelled;
        allocation.ShippedQuantity = shipped;
        allocation.DeliveredQuantity = delivered;
        allocation.ReturnedQuantity = returned;
        return false;
    }

    // Adds the allocation of each line the read places in the package that has no allocation in any package of the
    // order; a line already allocated somewhere is never allocated again, and an existing allocation is not changed.
    // The quantities are the read's normalized ones (quantity integrity was checked for the whole package), shipped and
    // delivered following the package's stored status. Stock follows the lines' ordered and cancelled quantities, so
    // the allocation does not take the units from stock again; it only lets the line's shipped and delivered
    // quantities be projected.
    // The read's line quantities for a package whose status they were not written with: one leaving ManualReview for the
    // status its read names (a raw status the panel now knows; a package that turns Returned has its lines returned, or
    // the reports and the sale invoice kept them as sold), or a PTT AVM / Pazarama line that changed under a known event.
    // They are stored under the event the package's status is projected from (PackageLineProjectionPolicy: the read's own
    // event without a status time, else the package's status time), so the lines follow them.
    private void ApplyReadLineQuantities(
        Guid tenantId,
        ShipmentPackage package,
        RemotePackage remote,
        IReadOnlyList<RemotePackageAllocation> remoteAllocations,
        IReadOnlyDictionary<string, NormalizedPackageAllocation> safeAllocations,
        IReadOnlyDictionary<string, OrderLine> lines,
        Dictionary<string, PackageLineAllocation> allocationsByKey,
        string eventId)
    {
        var projectedEventId = remote.StatusTimeUnknown ? eventId : PackageIngestionSafety.EventId(package.ExternalPackageId, package.StatusOccurredAt);
        foreach (var remoteAllocation in remoteAllocations)
            if (lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) && safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe))
                UpsertLineAllocation(tenantId, package.Id, line.Id, projectedEventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, safe.ShippedQuantity, safe.DeliveredQuantity, safe.ReturnedQuantity, allocationsByKey);
    }

    private int AddMissingLineAllocations(
        Guid tenantId,
        ShipmentPackage package,
        IReadOnlyList<RemotePackageAllocation> remoteAllocations,
        IReadOnlyDictionary<string, NormalizedPackageAllocation> safeAllocations,
        IReadOnlyDictionary<string, OrderLine> lines,
        Dictionary<string, PackageLineAllocation> allocationsByKey,
        string eventId)
    {
        var allocatedLineIds = allocationsByKey.Values.Select(allocation => allocation.OrderLineId).ToHashSet();
        var added = 0;
        foreach (var remoteAllocation in remoteAllocations)
        {
            if (!lines.TryGetValue(remoteAllocation.ExternalLineId, out var line)
                || !safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)
                || !allocatedLineIds.Add(line.Id)) continue;
            var shipped = package.Status is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered ? safe.ActiveAllocatedQuantity : safe.ShippedQuantity;
            var delivered = package.Status == ShipmentPackageStatus.Delivered ? safe.ActiveAllocatedQuantity : safe.DeliveredQuantity;
            if (UpsertLineAllocation(tenantId, package.Id, line.Id, eventId, safe.ActiveAllocatedQuantity, safe.CancelledQuantity, shipped, delivered, safe.ReturnedQuantity, allocationsByKey)) added++;
        }
        return added;
    }

    private async Task ProjectOrderReservations(Guid tenantId, Guid connectionId, IEnumerable<(OrderLine Line, DateTimeOffset ModifiedAt)> sourceLines, CancellationToken cancellationToken)
    {
        var sources = sourceLines.Where(source => source.Line.VariantId is not null).GroupBy(source => source.Line.Id).Select(group => group.Last()).ToList();
        var lines = sources.Select(source => source.Line).ToList();
        if (lines.Count == 0) return;
        var inventoryPolicy = await db.ConnectionInventoryPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var modifiedAtByLine = sources.ToDictionary(source => source.Line.Id, source => source.ModifiedAt);
        var variantIds = lines.Select(line => line.VariantId!.Value).Distinct().ToArray();
        var items = await db.InventoryItems
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
            .ToListAsync(cancellationToken);
        var itemsByVariant = items.ToDictionary(x => x.VariantId);
        foreach (var item in db.InventoryItems.Local.Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))) itemsByVariant.TryAdd(item.VariantId, item);
        var sourceIds = lines.Select(line => line.Id.ToString("D")).ToArray();
        var itemIds = itemsByVariant.Values.Select(item => item.Id).ToArray();
        var reservations = itemIds.Length == 0
            ? []
            : await db.StockReservations
                .Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && itemIds.Contains(x.InventoryItemId) && sourceIds.Contains(x.SourceId))
                .ToListAsync(cancellationToken);
        var reservationsByKey = reservations.ToDictionary(x => (x.InventoryItemId, x.SourceId), x => x);
        List<StockLedgerEntry> orderLedgerRows = itemIds.Length == 0
            ? []
            : await db.StockLedgerEntries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && itemIds.Contains(x.InventoryItemId) && sourceIds.Contains(x.SourceId)
                    && (x.MovementType == "ORDER_SHIPPED"
                        || x.MovementType == "ORDER_SHIPMENT_PROGRESS"
                        || x.MovementType == "ORDER_RECEIVED"
                        || x.MovementType == "ORDER_STOCK_BASELINE"
                        || x.MovementType == OrderInventoryStockPolicy.ShortfallMovementType))
                .ToListAsync(cancellationToken);
        var persistedLedgerIds = orderLedgerRows.Select(x => x.Id).ToHashSet();
        orderLedgerRows.AddRange(db.StockLedgerEntries.Local.Where(x => x.TenantId == tenantId
            && itemIds.Contains(x.InventoryItemId) && sourceIds.Contains(x.SourceId) && persistedLedgerIds.Add(x.Id)
            && (x.MovementType == "ORDER_SHIPPED" || x.MovementType == "ORDER_SHIPMENT_PROGRESS"
                || x.MovementType == "ORDER_RECEIVED" || x.MovementType == "ORDER_STOCK_BASELINE"
                || x.MovementType == OrderInventoryStockPolicy.ShortfallMovementType)));
        await AdoptResetOrderLineStock(tenantId, connectionId, lines, itemsByVariant, orderLedgerRows, reservationsByKey, cancellationToken);
        var legacyShippedLedger = orderLedgerRows
            .Where(x => x.MovementType == "ORDER_SHIPPED")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => Math.Max(0m, -group.Sum(x => x.QuantityDelta)), StringComparer.Ordinal);
        var shippedLedger = orderLedgerRows
            .Where(x => x.MovementType is "ORDER_SHIPPED" or "ORDER_SHIPMENT_PROGRESS")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => Math.Max(0m, -group.Sum(x => x.QuantityDelta)), StringComparer.Ordinal);
        var receivedLedgerDeltas = orderLedgerRows
            .Where(x => x.MovementType == "ORDER_RECEIVED")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityDelta), StringComparer.Ordinal);
        var stockBaselines = orderLedgerRows
            .Where(x => x.MovementType == "ORDER_STOCK_BASELINE")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityDelta), StringComparer.Ordinal);
        var shortfalls = orderLedgerRows
            .Where(x => x.MovementType == OrderInventoryStockPolicy.ShortfallMovementType)
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityDelta), StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        var outbox = new List<StockProjectionChange>();
        string? platformCode = null;
        foreach (var line in lines)
        {
            var variantId = line.VariantId!.Value;
            if (!itemsByVariant.TryGetValue(variantId, out var item)) continue;
            var sourceId = line.Id.ToString("D");
            reservationsByKey.TryGetValue((item.Id, sourceId), out var reservation);
            var targetShipped = Math.Min(
                Math.Max(0m, line.OrderedQuantity),
                Math.Max(line.ShippedQuantity, line.DeliveredQuantity));
            var consumedShipped = shippedLedger.GetValueOrDefault(sourceId);
            var shipmentDelta = Math.Max(0m, targetShipped - consumedShipped);
            var eventId = $"{line.Id:N}:{modifiedAtByLine[line.Id].ToUnixTimeMilliseconds()}";

            // Owner decision (30 Oct 2026): the panel holds no reservations. A reservation an earlier projection left (the
            // units an order could not take from stock) is released and kept as the line's shortfall instead, without
            // touching on-hand stock, so the units are neither taken later nor held from what the marketplaces are sent.
            if (reservation is { Status: ReservationStatus.Active } held)
            {
                var heldQuantity = held.Quantity;
                held.Status = ReservationStatus.Released; held.ReleasedAt = now; held.ExpiresAt = null; held.Version++;
                var releaseEventId = $"{eventId}:reservation-to-shortfall:{held.Id:N}";
                outbox.Add(StockMovementWriter.ReleaseReserved(item, heldQuantity, releaseEventId));
                StockMovementWriter.Record(db, tenantId, item.Id, heldQuantity, OrderLineSource("ORDER_RESERVATION_RELEASED", sourceId, releaseEventId, $"order-reservation:{releaseEventId}", line.OrderId, modifiedAtByLine[line.Id], now));
                RecordShortfall(tenantId, item.Id, sourceId, line.OrderId, heldQuantity, releaseEventId, modifiedAtByLine[line.Id], now);
                shortfalls[sourceId] = shortfalls.GetValueOrDefault(sourceId) + heldQuantity;
            }

            if (!stockBaselines.TryGetValue(sourceId, out var baselineQuantity))
            {
                // A line read for the first time takes its units from stock whatever its status, shipped and delivered
                // included (owner decision, 30 Oct 2026). Only units a shipment of the time before order-time deduction
                // already took (ORDER_SHIPPED rows) are its opening baseline; a line of a reset or a late match brings its
                // own baseline (AdoptResetOrderLineStock, AdoptLateMatchedLineStock).
                baselineQuantity = OrderInventoryStockPolicy.InitialBaselineQuantity(legacyShippedLedger.GetValueOrDefault(sourceId));
                stockBaselines[sourceId] = baselineQuantity;
                StockMovementWriter.Record(db, tenantId, item.Id, baselineQuantity, BaselineSource(item.Id, sourceId, line.OrderId, modifiedAtByLine[line.Id], now));
            }

            var targetCommitted = Math.Max(
                OrderInventoryStockPolicy.TargetCommittedQuantity(line.OrderedQuantity, line.CancelledQuantity, targetShipped),
                consumedShipped);
            var receivedLedgerDelta = receivedLedgerDeltas.GetValueOrDefault(sourceId);
            var shortfall = shortfalls.GetValueOrDefault(sourceId);
            // Physical stock leaves OnHand as soon as the order is ingested; never below zero unless the connection allows
            // negative stock. What stock could not give is the line's shortfall: reported, not reserved, never taken later.
            // A cancellation gives back what the line took (its shortfall first, which took nothing). Shipment progress is
            // tracked separately so a later fulfillment update cannot deduct the same unit for a second time.
            var movement = OrderInventoryStockPolicy.Plan(baselineQuantity, shortfall, receivedLedgerDelta, targetCommitted, item.OnHand, inventoryPolicy?.NegativeStockAllowed == true);
            var committedBeforeMovement = OrderInventoryStockPolicy.Committed(baselineQuantity, shortfall, receivedLedgerDelta);
            if (movement.OnHandDelta != 0)
            {
                var committedAfterStockDelta = Math.Max(0m, committedBeforeMovement - movement.OnHandDelta);
                var stockEventId = $"{eventId}:committed:{committedBeforeMovement.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}:{committedAfterStockDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";
                outbox.Add(StockMovementWriter.Move(db, item, movement.OnHandDelta, StockMovementRounding.OnHand,
                    OrderLineSource("ORDER_RECEIVED", sourceId, stockEventId, $"order-stock-received:{item.Id:N}:{stockEventId}", line.OrderId, modifiedAtByLine[line.Id], now)));
                receivedLedgerDeltas[sourceId] = receivedLedgerDelta + movement.OnHandDelta;
            }
            if (movement.ShortfallDelta != 0)
            {
                var shortfallAfter = shortfall + movement.ShortfallDelta;
                var shortfallEventId = $"{eventId}:shortfall:{shortfall.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}:{shortfallAfter.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";
                RecordShortfall(tenantId, item.Id, sourceId, line.OrderId, movement.ShortfallDelta, shortfallEventId, modifiedAtByLine[line.Id], now);
                shortfalls[sourceId] = shortfallAfter;
                if (movement.ShortfallDelta > 0)
                    await RecordIssue(
                        tenantId,
                        StockShortfallNotices.DedupeKey(line.Id),
                        StockShortfallNotices.IssueCode,
                        StockShortfallSummary(platformCode ??= await ConnectionPlatformCode(tenantId, connectionId, cancellationToken), await OrderNumberOf(tenantId, line.OrderId, cancellationToken), line.Sku ?? line.Barcode, shortfallAfter),
                        cancellationToken);
                // The units that could not be taken are no longer ordered (a cancellation), so the notice closes; a stock count
                // set by hand closes it too (StockShortfallNotices).
                else if (shortfallAfter <= 0) await ResolveIssue(tenantId, StockShortfallNotices.DedupeKey(line.Id), cancellationToken);
            }

            if (shipmentDelta > 0)
            {
                var shipmentEventId = $"{line.Id:N}:shipment-progress:{targetShipped.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";
                StockMovementWriter.Record(db, tenantId, item.Id, -shipmentDelta, OrderLineSource("ORDER_SHIPMENT_PROGRESS", sourceId, shipmentEventId, $"order-shipment-progress:{item.Id:N}:{shipmentEventId}", line.OrderId, modifiedAtByLine[line.Id], now));
                shippedLedger[sourceId] = consumedShipped + shipmentDelta;
            }
        }
        // Every connection selling the variant gets the new stock, not only the one that sold it.
        await StockProjectionOutbox.EnqueueAsync(db, tenantId, outbox, now, cancellationToken);
    }

    // The units of an order line that stock could not give (positive) or that are no longer owed (negative). A marker, not
    // a stock movement: on-hand does not change with it, and the panel's ledger view leaves it out.
    private void RecordShortfall(Guid tenantId, Guid itemId, string sourceId, Guid orderId, decimal quantity, string eventId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        StockMovementWriter.Record(db, tenantId, itemId, quantity, OrderLineSource(OrderInventoryStockPolicy.ShortfallMovementType, sourceId, eventId, $"order-stock-shortfall:{itemId:N}:{eventId}", orderId, occurredAt, now));

    // An order line's stock ledger row (SIPARIS-STOK-8: written through StockMovementWriter).
    private static StockLedgerSource OrderLineSource(string movementType, string sourceId, string sourceEventId, string idempotencyKey, Guid orderId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        new(movementType, "ORDER_LINE", sourceId, sourceEventId, idempotencyKey, occurredAt, now, $"order:{orderId:N}");

    // A catalog read's stock row (an opening number or a marketplace-authoritative change): the event id is the key.
    private static StockLedgerSource CatalogStockSource(string movementType, Guid connectionId, string eventId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        new(movementType, MarketplaceStockImportPolicy.SourceType, connectionId.ToString("D"), eventId, eventId, occurredAt, now, $"catalog-import:{connectionId:N}");

    private static StockLedgerSource BaselineSource(Guid itemId, string sourceId, Guid orderId, DateTimeOffset occurredAt, DateTimeOffset now) =>
        OrderLineSource("ORDER_STOCK_BASELINE", sourceId, $"baseline:{sourceId}", $"order-stock-baseline:{itemId:N}:{sourceId}", orderId, occurredAt, now);

    // A connection's order reset leaves the stock movements and reservation of a deleted line under a carry key
    // (OrderInventoryStockPolicy.ResetCarryKey). The line a later sync imports under the same marketplace order and
    // line ids takes them over before its first projection, so the units the order already took from stock are not
    // taken a second time and its reservation is not held twice. Only rows of the line's own inventory item move.
    private async Task AdoptResetOrderLineStock(
        Guid tenantId,
        Guid connectionId,
        List<OrderLine> lines,
        Dictionary<Guid, InventoryItem> itemsByVariant,
        List<StockLedgerEntry> orderLedgerRows,
        Dictionary<(Guid InventoryItemId, string SourceId), StockReservation> reservationsByKey,
        CancellationToken cancellationToken)
    {
        var projectedSourceIds = orderLedgerRows.Select(x => x.SourceId).Concat(reservationsByKey.Keys.Select(key => key.SourceId)).ToHashSet(StringComparer.Ordinal);
        var unprojected = lines.Where(line => itemsByVariant.ContainsKey(line.VariantId!.Value) && !projectedSourceIds.Contains(line.Id.ToString("D"))).ToList();
        if (unprojected.Count == 0) return;
        var orderIds = unprojected.Select(line => line.OrderId).Distinct().ToArray();
        var externalOrderIds = db.Orders.Local.Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionary(x => x.Id, x => x.ExternalOrderId);
        var unloaded = orderIds.Where(id => !externalOrderIds.ContainsKey(id)).ToArray();
        if (unloaded.Length > 0)
        {
            var rows = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && unloaded.Contains(x.Id)).Select(x => new { x.Id, x.ExternalOrderId }).ToListAsync(cancellationToken);
            foreach (var row in rows) externalOrderIds[row.Id] = row.ExternalOrderId;
        }
        var carryKeys = unprojected
            .Where(line => externalOrderIds.ContainsKey(line.OrderId))
            .ToDictionary(line => line.Id, line => OrderInventoryStockPolicy.ResetCarryKey(connectionId, externalOrderIds[line.OrderId], line.ExternalLineId));
        if (carryKeys.Count == 0) return;
        var keys = carryKeys.Values.Distinct(StringComparer.Ordinal).ToArray();
        var itemIds = unprojected.Select(line => itemsByVariant[line.VariantId!.Value].Id).Distinct().ToArray();
        var carriedLedger = await db.StockLedgerEntries
            .Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && itemIds.Contains(x.InventoryItemId) && keys.Contains(x.SourceId))
            .ToListAsync(cancellationToken);
        var carriedReservations = await db.StockReservations
            .Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && itemIds.Contains(x.InventoryItemId) && keys.Contains(x.SourceId))
            .ToListAsync(cancellationToken);
        if (carriedLedger.Count == 0 && carriedReservations.Count == 0) return;
        foreach (var line in unprojected)
        {
            if (!carryKeys.TryGetValue(line.Id, out var key)) continue;
            var itemId = itemsByVariant[line.VariantId!.Value].Id;
            var sourceId = line.Id.ToString("D");
            foreach (var row in carriedLedger.Where(x => x.InventoryItemId == itemId && x.SourceId == key))
            {
                row.SourceId = sourceId;
                orderLedgerRows.Add(row);
            }
            foreach (var reservation in carriedReservations.Where(x => x.InventoryItemId == itemId && x.SourceId == key))
            {
                reservation.SourceId = sourceId;
                reservation.Version++;
                reservationsByKey[(itemId, sourceId)] = reservation;
            }
        }
    }
}
