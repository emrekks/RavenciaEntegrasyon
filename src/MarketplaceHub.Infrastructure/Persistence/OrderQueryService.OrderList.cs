using System.Linq.Expressions;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class OrderQueryService
{
    public async Task<PageResult<OrderListView>> OrdersAsync(Guid tenantId, int limit, int page, string? after, OrderListQuery queryOptions, CancellationToken cancellationToken)
    {
        // The panel is a local read model. Remote reads belong to the scheduled worker.
        var sort = NormalizeOrderSort(queryOptions.Sort);
        var query = FilteredOrders(tenantId, queryOptions);
        // Count the filtered result set before applying the page cursor. Counting
        // after the cursor made the total shrink on every subsequent page and
        // caused the frontend to hide valid later pages.
        var totalCount = await query.CountAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!cursors.TryDecodeOrder(after, out var cursorSort, out var cursorDueAt, out var cursorOrderedAt, out var cursorId) || cursorSort != sort)
                throw new ArgumentException("Cursor geçersiz veya seçili sıralamayla uyuşmuyor.", nameof(after));
            if (sort is "SHIP_ASC" or "SHIP_DESC")
            {
                // The cursor's first date is the order's shipped time; orders never shipped come last.
                var shipped = ShippedAtQuery(tenantId);
                if (cursorDueAt is null)
                    query = query.Where(x => shipped.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt) == null && (sort == "SHIP_ASC"
                        ? x.OrderedAt > cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) > 0
                        : x.OrderedAt < cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) < 0));
                else if (sort == "SHIP_ASC")
                    query = query.Where(x => shipped.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt) == null
                        || shipped.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt) > cursorDueAt
                        || shipped.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt) == cursorDueAt && (x.OrderedAt > cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) > 0));
                else
                    query = query.Where(x => shipped.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt) == null
                        || shipped.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt) < cursorDueAt
                        || shipped.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt) == cursorDueAt && (x.OrderedAt < cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) < 0));
            }
            else if (sort is "DUE_ASC" or "DUE_DESC")
            {
                if (cursorDueAt is null)
                    query = query.Where(x => x.ShipmentDueAt == null && (sort == "DUE_ASC"
                        ? x.OrderedAt > cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) > 0
                        : x.OrderedAt < cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) < 0));
                else if (sort == "DUE_ASC")
                    query = query.Where(x => x.ShipmentDueAt == null || x.ShipmentDueAt > cursorDueAt || x.ShipmentDueAt == cursorDueAt && (x.OrderedAt > cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) > 0));
                else
                    query = query.Where(x => x.ShipmentDueAt == null || x.ShipmentDueAt < cursorDueAt || x.ShipmentDueAt == cursorDueAt && (x.OrderedAt < cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) < 0));
            }
            else
                query = sort == "DATE_ASC"
                    ? query.Where(x => x.OrderedAt > cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) > 0)
                    : query.Where(x => x.OrderedAt < cursorOrderedAt || x.OrderedAt == cursorOrderedAt && x.Id.CompareTo(cursorId) < 0);
        }
        var shippedHistory = ShippedAtQuery(tenantId);
        IQueryable<Order> orderedQuery = sort switch
        {
            "SHIP_ASC" => query.OrderBy(x => shippedHistory.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt) == null)
                .ThenBy(x => shippedHistory.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt)).ThenBy(x => x.OrderedAt).ThenBy(x => x.Id),
            "SHIP_DESC" => query.OrderBy(x => shippedHistory.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt) == null)
                .ThenByDescending(x => shippedHistory.Where(h => h.OrderId == x.Id).Min(h => (DateTimeOffset?)h.OccurredAt)).ThenByDescending(x => x.OrderedAt).ThenByDescending(x => x.Id),
            "DUE_ASC" => query.OrderBy(x => x.ShipmentDueAt == null).ThenBy(x => x.ShipmentDueAt).ThenBy(x => x.OrderedAt).ThenBy(x => x.Id),
            "DUE_DESC" => query.OrderBy(x => x.ShipmentDueAt == null).ThenByDescending(x => x.ShipmentDueAt).ThenByDescending(x => x.OrderedAt).ThenByDescending(x => x.Id),
            "DATE_ASC" => query.OrderBy(x => x.OrderedAt).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.OrderedAt).ThenByDescending(x => x.Id)
        };
        if (string.IsNullOrWhiteSpace(after) && page > 1)
            orderedQuery = orderedQuery.Skip(checked((page - 1) * limit));
        var orders = await orderedQuery.Take(limit + 1).ToListAsync(cancellationToken);
        var orderIds = orders.Select(x => x.Id).ToArray();
        var connectionIds = orders.Select(x => x.ConnectionId).Distinct().ToArray();
        var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
        var shippedAtByOrder = await shippedHistory.Where(h => orderIds.Contains(h.OrderId))
            .GroupBy(h => h.OrderId).Select(group => new { OrderId = group.Key, At = group.Min(h => h.OccurredAt) })
            .ToDictionaryAsync(row => row.OrderId, row => row.At, cancellationToken);
        var packages = await CurrentShipmentPackages().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).OrderByDescending(x => x.StatusOccurredAt).ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var invoiceIds = invoices.Select(invoice => invoice.Id).ToArray();
        var deliveryStates = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(delivery => delivery.TenantId == tenantId && invoiceIds.Contains(delivery.InvoiceId))
                .Select(delivery => new { delivery.InvoiceId, delivery.PackageId, delivery.Status, delivery.UpdatedAt })
                .ToListAsync(cancellationToken);
        var deliveryAttempts = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveries.AsNoTracking()
                .Where(delivery => delivery.TenantId == tenantId && invoiceIds.Contains(delivery.InvoiceId))
                .Select(delivery => new { delivery.InvoiceId, delivery.PackageId, delivery.Status })
                .ToListAsync(cancellationToken);
        var confirmedDeliveryInvoiceIds = new HashSet<Guid>();
        foreach (var candidateInvoice in invoices.Where(invoice => invoice.OriginalInvoiceId == null && invoice.SequencePurpose == "SALE"))
        {
            var targetPackages = candidateInvoice.PackageId is { } targetPackageId
                ? packages.Where(package => package.Id == targetPackageId).ToArray()
                : packages.Where(package => package.OrderId == candidateInvoice.OrderId && package.Status != ShipmentPackageStatus.Cancelled).ToArray();
            var confirmedPackageIds = targetPackages.Where(package => MarketplaceInvoiceDeliveryEvidencePolicy.HasConfirmedDelivery(
                    deliveryStates.Where(state => state.InvoiceId == candidateInvoice.Id && state.PackageId == package.Id)
                        .OrderByDescending(state => state.UpdatedAt).Select(state => state.Status).FirstOrDefault(),
                    deliveryAttempts.Any(attempt => attempt.InvoiceId == candidateInvoice.Id && attempt.PackageId == package.Id && attempt.Status == "CONFIRMED")))
                .Select(package => package.Id).ToArray();
            if (MarketplaceInvoiceDeliveryEvidencePolicy.AllTargetsConfirmed(targetPackages.Select(package => package.Id), confirmedPackageIds))
                confirmedDeliveryInvoiceIds.Add(candidateInvoice.Id);
        }
        var linesByOrder = lines.GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.ToList());
        var packagesByOrder = packages.GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.ToList());
        var invoicesByOrder = invoices.Where(x => x.OriginalInvoiceId == null).GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.First());
        var invoicesByPackage = invoices.Where(x => x.OriginalInvoiceId == null && x.PackageId != null).GroupBy(x => x.PackageId!.Value).ToDictionary(x => x.Key, x => x.First());
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var connectionByOrder = orders.ToDictionary(order => order.Id, order => order.ConnectionId);
        var catalog = await OrderLineCatalog.LoadAsync(db, tenantId, lines, line => connectionByOrder.TryGetValue(line.OrderId, out var lineConnectionId) ? lineConnectionId : null, cancellationToken);
        // The orders' exchange resends (ExchangeResendClaims: the Yeniden Gönderimler tab's rule), one per order.
        var pageOrderIds = orders.Select(order => order.Id).ToArray();
        var exchangeResends = (await db.ReturnClaims.AsNoTracking()
                .Where(claim => claim.TenantId == tenantId && pageOrderIds.Contains(claim.OrderId) && claim.Status != ReturnClaimStatus.Cancelled
                    && claim.CustomerRequest == ReturnCustomerRequestPolicy.Change
                    && (claim.ReplacementPackageId != null || claim.ResolvedWith == ReturnCustomerRequestPolicy.Change))
                .Select(claim => new
                {
                    claim.Id,
                    claim.Version,
                    claim.OrderId,
                    claim.ReplacementPackageId,
                    claim.ReplacementCargoProviderName,
                    claim.ReplacementCargoTrackingNumber,
                    claim.ReplacementCargoTrackingLink,
                    claim.ReplacementShippedAt,
                    ExchangeLineShipped = db.OrderLines.Any(line => line.TenantId == claim.TenantId && line.OrderId == claim.OrderId && line.ShippedQuantity > 0
                        && line.SourceSnapshotJson != null && line.SourceSnapshotJson.Contains(ExchangeResendRules.HepsiburadaExchangeLineReason))
                })
                .ToListAsync(cancellationToken))
            .GroupBy(claim => claim.OrderId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(claim => claim.ReplacementPackageId != null).Select(claim =>
            {
                // Handed to the carrier: Trendyol's tracking link, Hepsiburada's shipped exchange line, or the owner's mark (ExchangeResendRules).
                var shippedSource = ExchangeResendRules.ShippedSource(claim.ReplacementCargoTrackingLink, claim.ReplacementShippedAt, claim.ExchangeLineShipped);
                return new OrderExchangeResendView(claim.ReplacementPackageId, claim.ReplacementCargoProviderName, claim.ReplacementCargoTrackingNumber, shippedSource is not null, claim.ReplacementCargoTrackingLink,
                    claim.Id, claim.Version, shippedSource);
            }).First());
        var now = timeProvider.GetUtcNow();
        var rows = orders.Select(order =>
        {
            // Every line goes to the list, cancelled ones too: the panel strikes them through, and the İptal tab shows
            // only those. Counts, the card image and the product total stay with the lines still ordered.
            var allLines = linesByOrder.GetValueOrDefault(order.Id) ?? [];
            var orderLines = allLines
                .Where(line => OrderLinePresentationPolicy.ShouldShowInOrderList(order.DerivedStatus, line.OrderedQuantity, line.CancelledQuantity))
                .ToList();
            var connection = connections.GetValueOrDefault(order.ConnectionId);
            var orderPackages = (packagesByOrder.GetValueOrDefault(order.Id) ?? [])
                .Where(package => !IsShopifyRemainderPackage(connection?.PlatformCode, package.ExternalPackageId))
                .ToList();
            var package = SelectDisplayPackage(orderPackages, connection?.PlatformCode);
            // The displayed package is ordered by the latest operational event,
            // while an invoice may have been created against another package of
            // the same order (especially for older/split-package orders). The
            // order card must still expose that invoice instead of showing a
            // created state without a document action.
            var invoice = (package is not null ? invoicesByPackage.GetValueOrDefault(package.Id) : null)
                ?? orderPackages.Select(x => invoicesByPackage.GetValueOrDefault(x.Id)).FirstOrDefault(x => x is not null)
                ?? invoicesByOrder.GetValueOrDefault(order.Id);
            var invoiceDocumentUrl = orderPackages
                .Select(x => ValidInvoiceDocumentUrl(x.MarketplaceInvoiceUrl))
                .FirstOrDefault(x => x is not null)
                ?? InvoiceDocumentUrl(order.CustomerSnapshotJson);
            var customer = Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
            var buyer = BuyerType(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, customer.IsMicroExport, order.BuyerType);
            var dueAt = order.ShipmentDueAt ?? OperationalDueAt(order.CustomerSnapshotJson);
            var unverifiedWithoutPackage = OpenOrderLifecyclePolicy.IsHepsiburadaOrderUnverifiedWithoutPackage(
                connection?.PlatformCode,
                order.DerivedStatus,
                orderPackages.Count(candidate => candidate.Status != ShipmentPackageStatus.Cancelled),
                order.OrderedAt,
                now);
            var displayStatus = unverifiedWithoutPackage ? "UNVERIFIED" : order.DerivedStatus;
            var lineViews = allLines.Select(x =>
            {
                var variant = catalog.Variant(x);
                var source = SourceLine(x.SourceSnapshotJson);
                var images = catalog.Images(x, variant);
                var barcode = string.IsNullOrWhiteSpace(x.Barcode) ? variant?.Barcode : x.Barcode;
                return new OrderLineView(x.Id, x.Sku, barcode, x.TitleSnapshot, x.OrderedQuantity, x.CancelledQuantity, x.ShippedQuantity, x.DeliveredQuantity, x.ReturnedQuantity, x.UnitPrice, x.VatRate, x.RawStatus, x.VariantId ?? variant?.Id, variant?.ModelCode ?? source.ModelCode, variant?.OptionSignature ?? source.OptionSignature, images.ImageUrl, FallbackImageUrl: images.FallbackImageUrl);
            }).ToList();
            // Live packages first (each still by its latest event): the card, its label and its
            // actions take the first package, which must not be a cancelled split package.
            var packageViews = orderPackages.OrderBy(x => x.Status == ShipmentPackageStatus.Cancelled ? 1 : 0).Select(x => Map(x, order.OrderNumber)).ToList();
            return new OrderListView(
                order.Id, order.OrderNumber, displayStatus, order.Currency, order.NetAmount, order.OrderedAt,
                orderLines.Count, orderPackages.Count, order.Version,
                order.ConnectionId, connection?.PlatformCode ?? "TRENDYOL", connection?.DisplayName ?? "Trendyol",
                customer.Name, buyer.OrderType, customer.IsMicroExport, dueAt,
                OpenOrderLifecyclePolicy.ShouldShowShipmentDeadlineWarning(connection?.PlatformCode, displayStatus, dueAt, now, unverifiedWithoutPackage), PackageInvoiceLabel(package, invoice, order.CustomerSnapshotJson, orderPackages.Select(x => x.RawStatus), connection?.PlatformCode, invoice is not null && confirmedDeliveryInvoiceIds.Contains(invoice.Id)),
                package?.CargoProviderExternalId ?? JsonText(order.CustomerSnapshotJson, "marketplaceCargoProviderName"), package?.CargoTrackingNumber,
                orderLines.Select(x => catalog.CatalogImageUrl(catalog.Variant(x))).FirstOrDefault(x => x is not null),
                orderLines.Sum(x => OrderLinePresentationPolicy.ActiveQuantity(x.OrderedQuantity, x.CancelledQuantity)), MaskIdentityNumber(customer.TaxOrIdentityNumber),
                ListAddress(order.ShipmentAddressSnapshotJson), order.GrossAmount, order.DiscountAmount,
                lineViews, packageViews, invoice?.Id, invoiceDocumentUrl,
                connection?.PlatformCode == "N11" ? N11OrderInfo(orderLines.Select(x => x.SourceSnapshotJson), order.CustomerSnapshotJson) : null,
                shippedAtByOrder.TryGetValue(order.Id, out var shippedAt) ? shippedAt : null,
                order.LabelPrintedAt,
                RowMarks(connection?.PlatformCode, order.StoreFrontCode, order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson),
                UnreadableLineCount(packagesByOrder.GetValueOrDefault(order.Id) ?? []),
                AmountMayBeIncomplete(connection?.PlatformCode, UnreadableLineCount(packagesByOrder.GetValueOrDefault(order.Id) ?? [])),
                buyer.BuyerType,
                exchangeResends.GetValueOrDefault(order.Id));
        }).ToList();
        var hasMore = orders.Count > limit;
        var pageRows = rows.Take(limit).ToList();
        var last = orders.Take(limit).LastOrDefault();
        var next = hasMore && last is not null
            ? cursors.EncodeOrder(sort, sort is "SHIP_ASC" or "SHIP_DESC" ? shippedAtByOrder.TryGetValue(last.Id, out var lastShippedAt) ? lastShippedAt : null : last.ShipmentDueAt, last.OrderedAt, last.Id)
            : null;
        return new(pageRows, next, hasMore, totalCount, OrderListSupportedFilters);
    }

    private static readonly string[] OrderListSupportedFilters = ["labelPrinted"];

    // The order list's rows for these options: orders of active connections, without the stale
    // unpackaged Hepsiburada ones, through every list filter (status tab included). The tab
    // counters run the same query per tab, so a counter always equals its tab's list total.
    internal IQueryable<Order> FilteredOrders(Guid tenantId, OrderListQuery options)
    {
        var query = db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")));
        query = ExcludeStaleUnpackagedHepsiburadaOrders(query, tenantId);
        ApplyOrderFilters(ref query, options, tenantId);
        return query;
    }

    internal void ApplyOrderFilters(ref IQueryable<Order> query, OrderListQuery options, Guid tenantId)
    {
        if (options.LabelPrinted is { } printed)
            query = printed ? query.Where(x => x.LabelPrintedAt != null) : query.Where(x => x.LabelPrintedAt == null);
        // Superseded packages (split parents, resent originals, the zero-ID
        // placeholder) neither match a tab nor count as invoice targets.
        var currentPackages = CurrentShipmentPackages();
        var resendCreators = new[] { "transfer", "resend", "replacement" };
        var resentNotShipped = options.ResentNotShipped == true;
        var exchangeClaims = resentNotShipped ? db.ReturnClaims.Where(ExchangeResendRules.NotShipped(db)) : db.ReturnClaims;
        var invoice = options.Invoice?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(invoice) && invoice != "ALL")
        {
            var fullyDeliveredInvoiceOrderIds = db.Invoices.AsNoTracking()
                .Where(candidate => candidate.TenantId == tenantId && candidate.OriginalInvoiceId == null
                    && candidate.SequencePurpose == "SALE" && candidate.Status == InvoiceStatus.Completed
                    && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == candidate.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && (candidate.PackageId.HasValue
                        ? currentPackages.Any(package => package.TenantId == tenantId && package.Id == candidate.PackageId.Value
                            && package.Status != ShipmentPackageStatus.Cancelled
                            && (db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId && state.InvoiceId == candidate.Id && state.PackageId == package.Id && state.Status == "CONFIRMED")
                                || (!db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId && state.InvoiceId == candidate.Id && state.PackageId == package.Id)
                                    && db.MarketplaceDeliveries.Any(delivery => delivery.TenantId == tenantId && delivery.InvoiceId == candidate.Id && delivery.PackageId == package.Id && delivery.Status == "CONFIRMED"))))
                        : currentPackages.Any(package => package.TenantId == tenantId && package.OrderId == candidate.OrderId && package.Status != ShipmentPackageStatus.Cancelled)
                            && !currentPackages.Any(package => package.TenantId == tenantId && package.OrderId == candidate.OrderId && package.Status != ShipmentPackageStatus.Cancelled
                                && !((db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId && state.InvoiceId == candidate.Id && state.PackageId == package.Id && state.Status == "CONFIRMED"))
                                    || (!db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId && state.InvoiceId == candidate.Id && state.PackageId == package.Id)
                                        && db.MarketplaceDeliveries.Any(delivery => delivery.TenantId == tenantId && delivery.InvoiceId == candidate.Id && delivery.PackageId == package.Id && delivery.Status == "CONFIRMED"))))))
                .Select(candidate => candidate.OrderId);
            var completedInvoiceOrderIdsMissingDelivery = db.Invoices.AsNoTracking()
                .Where(candidate => candidate.TenantId == tenantId && candidate.OriginalInvoiceId == null
                    && candidate.SequencePurpose == "SALE" && candidate.Status == InvoiceStatus.Completed
                    && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == candidate.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && (candidate.PackageId.HasValue
                        ? !currentPackages.Any(package => package.TenantId == tenantId && package.Id == candidate.PackageId.Value
                            && package.Status != ShipmentPackageStatus.Cancelled
                            && (db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId && state.InvoiceId == candidate.Id && state.PackageId == package.Id && state.Status == "CONFIRMED")
                                || (!db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId && state.InvoiceId == candidate.Id && state.PackageId == package.Id)
                                    && db.MarketplaceDeliveries.Any(delivery => delivery.TenantId == tenantId && delivery.InvoiceId == candidate.Id && delivery.PackageId == package.Id && delivery.Status == "CONFIRMED"))))
                        : !currentPackages.Any(package => package.TenantId == tenantId && package.OrderId == candidate.OrderId && package.Status != ShipmentPackageStatus.Cancelled)
                            || currentPackages.Any(package => package.TenantId == tenantId && package.OrderId == candidate.OrderId && package.Status != ShipmentPackageStatus.Cancelled
                                && !((db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId && state.InvoiceId == candidate.Id && state.PackageId == package.Id && state.Status == "CONFIRMED"))
                                    || (!db.MarketplaceDeliveryStates.Any(state => state.TenantId == tenantId && state.InvoiceId == candidate.Id && state.PackageId == package.Id)
                                        && db.MarketplaceDeliveries.Any(delivery => delivery.TenantId == tenantId && delivery.InvoiceId == candidate.Id && delivery.PackageId == package.Id && delivery.Status == "CONFIRMED"))))))
                .Select(candidate => candidate.OrderId);
            // The same rules as the row label (IssuedInvoiceNotOnPlatform / IssuedInvoiceNeedsReview).
            var activeProviderInvoices = db.Invoices.AsNoTracking()
                .Where(candidate => candidate.TenantId == tenantId && candidate.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == candidate.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")));
            var notOnPlatformInvoiceOrderIds = activeProviderInvoices.Where(IssuedInvoiceNotOnPlatform).Select(candidate => candidate.OrderId);
            var needsReviewInvoiceOrderIds = activeProviderInvoices.Where(IssuedInvoiceNeedsReview).Select(candidate => candidate.OrderId);
            query = invoice switch
            {
                "FATURA_KESILDI" => query.Where(x => fullyDeliveredInvoiceOrderIds.Contains(x.Id)),
                "FATURA_KONTROLDE" => query.Where(x =>
                    fullyDeliveredInvoiceOrderIds.Contains(x.Id)
                    || completedInvoiceOrderIdsMissingDelivery.Contains(x.Id)
                    || currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id
                        && (package.ManualInvoiceStatus == "UPLOADED"
                            || package.ManualInvoiceStatus == null
                                && (package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Received || package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Invoiced)))),
                "FATURA_PLATFORMA_AKTARILMADI" => query.Where(x => notOnPlatformInvoiceOrderIds.Contains(x.Id)),
                "FATURA_INCELEME_GEREKLI" => query.Where(x => needsReviewInvoiceOrderIds.Contains(x.Id)),
                "FATURA_REDDEDILDI" => query.Where(x => !needsReviewInvoiceOrderIds.Contains(x.Id) && (
                    db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                        && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                        && (i.Status == InvoiceStatus.Rejected || i.Status == InvoiceStatus.ValidationFailed || i.Status == InvoiceStatus.ManualReview))
                    || currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.ManualInvoiceStatus == null && package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Rejected))),
                // The provider may hold the invoice: neither rejected nor issued until it is checked.
                "FATURA_BELIRSIZ" => query.Where(x => db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && i.Status == InvoiceStatus.UnknownResult)),
                "FATURA_IPTAL" => query.Where(x => db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && (i.Status == InvoiceStatus.Cancelled || i.Status == InvoiceStatus.CancelledLocal))),
                "FATURA_PLATFORMA_BEKLIYOR" => query.Where(x => db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null && i.SequencePurpose == "SALE"
                    && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && (i.Status == InvoiceStatus.Submitted || i.Status == InvoiceStatus.Accepted || i.Status == InvoiceStatus.MarketplacePending))),
                // A status set by hand decides here as on the label: "Faturası yüklendi" never waits, "Fatura bekliyor" does.
                // A cancelled order (its row reads "İptal edildi") and a cancelled package wait for nothing; an order whose
                // sale invoice was issued (any state but cancelled or a draft) carries that invoice's label instead.
                "FATURA_BEKLIYOR" => query.Where(x => x.DerivedStatus != "CANCELLED" && (
                    currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.Status != ShipmentPackageStatus.Cancelled && package.ManualInvoiceStatus == "PENDING")
                    || !currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.ManualInvoiceStatus == "UPLOADED")
                        && !db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null && i.SequencePurpose == "SALE"
                            && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.CancelledLocal && i.Status != InvoiceStatus.Draft
                            && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
                        && (currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.Status != ShipmentPackageStatus.Cancelled && package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.NotInvoiced)
                            || (x.CustomerSnapshotJson.Contains("NOTINVOICED") || x.CustomerSnapshotJson.Contains("NOT_INVOICED"))
                                // Read from the order, it still needs a package that is not cancelled (or none recorded yet).
                                && (currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.Status != ShipmentPackageStatus.Cancelled)
                                    || !currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id))
                            // n11, PTT AVM and Pazarama do not report the state: no panel invoice and no observed one is waiting,
                            // as the label reads it (an n11 order with an invoice link is shown for review instead).
                            || db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == x.ConnectionId
                                    && UnverifiableInvoicePlatforms.Contains(connection.PlatformCode)
                                    && (connection.PlatformCode != "N11"
                                        || !EF.Functions.Like(x.CustomerSnapshotJson, "%\"invoiceLink\":\"_%")
                                            && !EF.Functions.Like(x.CustomerSnapshotJson, "%\"invoiceLink\": \"_%")))
                                && currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id
                                    && package.Status != ShipmentPackageStatus.Cancelled
                                    && package.ManualInvoiceStatus == null && package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Unknown)
                                && !db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                                    && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))))),
                "FATURA_ISLENIYOR" => query.Where(x => db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && (i.Status == InvoiceStatus.Draft || i.Status == InvoiceStatus.Validating || i.Status == InvoiceStatus.Ready || i.Status == InvoiceStatus.Submitting || i.Status == InvoiceStatus.Submitted
                        || ((i.Status == InvoiceStatus.Accepted || i.Status == InvoiceStatus.MarketplacePending) && i.LastErrorCode == null)))),
                _ => query.Where(_ => false)
            };
        }

        var invoiceType = options.InvoiceType?.Trim().ToUpperInvariant();
        // The buyer kind stored on the order by the invoice's own rule (OrderBuyerTypes), the value the row label shows:
        // Kurumsal is a corporate buyer with or without its tax number, "Kurumsal · Vergi No Yok" only the latter. An
        // order the backfill has not reached yet (BuyerType null, minutes after the column is added) matches no kind.
        if (invoiceType == "KURUMSAL")
            query = query.Where(x => x.BuyerType == OrderBuyerTypeView.Corporate || x.BuyerType == OrderBuyerTypeView.CorporateTaxIdMissing);
        else if (invoiceType == "KURUMSAL_VERGI_NO_YOK")
            query = query.Where(x => x.BuyerType == OrderBuyerTypeView.CorporateTaxIdMissing);
        else if (invoiceType == "BIREYSEL")
            query = query.Where(x => x.BuyerType == OrderBuyerTypeView.Individual);

        var invoiceRegion = options.InvoiceRegion?.Trim().ToUpperInvariant();
        // The region follows the "Mikro İhracat" label (OrderMicroExportRules, also the row flag and the card), not a text search over the
        // whole package: a company named "... İthalat İhracat Ltd." or a "MICRO FIBER" product is no export (SIPARIS-STOK-3).
        if (invoiceRegion == "MICRO_EXPORT")
            query = query.Where(OrderMicroExportRules.InDatabase);
        else if (invoiceRegion == "TR")
            query = query.Where(OrderMicroExportRules.NotInDatabase);

        var status = options.Status?.Trim().ToUpperInvariant();
        // The dashboard's "Geciken Siparişler" card opens this view: exactly the orders that card counts.
        if (status == "LATE")
        {
            var late = DashboardReadService.LateOrderMetricsQuery(db, tenantId, timeProvider.GetUtcNow());
            query = query.Where(order => late.Any(lateOrder => lateOrder.Id == order.Id));
        }
        else if (!string.IsNullOrWhiteSpace(status) && status != "ALL")
        {
            var derivedStatuses = DerivedStatusesForOrderTab(status);
            var packageStatuses = PackageStatusesForOrderTab(status);
            var unpackagedNew = HepsiburadaUnpackagedNewOrders(tenantId);
            var unpackagedOnHold = HepsiburadaUnpackagedOnHoldOrders(tenantId);
            var unpackagedDelivered = HepsiburadaUnpackagedDeliveredOrders(tenantId);
            Expression<Func<Order, bool>> marketplaceRule = status == "CANCELLED"
                // Orders cancelled whole, and the cancelled products of orders that stay open (a cancelled split
                // package, or a line with a cancelled quantity): the list shows only their cancelled lines here.
                ? order => order.DerivedStatus == "CANCELLED"
                    || (!db.PlatformConnections.Any(connection => connection.TenantId == order.TenantId && connection.Id == order.ConnectionId && connection.PlatformCode == "SHOPIFY")
                        && currentPackages.Any(package => package.TenantId == order.TenantId && package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Cancelled))
                    || db.OrderLines.Any(line => line.TenantId == order.TenantId && line.OrderId == order.Id && line.CancelledQuantity > 0)
                // Every order with some products cancelled but not cancelled whole, delivered ones too (owner's call,
                // 1 Nov 2026); an open one stays in its own status tab as well.
                : status == "PARTIALLY_CANCELLED"
                ? order => order.DerivedStatus != "CANCELLED"
                    && (currentPackages.Any(package => package.TenantId == order.TenantId && package.OrderId == order.Id && package.Status == ShipmentPackageStatus.PartiallyCancelled)
                        || db.OrderLines.Any(line => line.TenantId == order.TenantId && line.OrderId == order.Id && line.CancelledQuantity > 0))
                : derivedStatuses is not null
                ? order => derivedStatuses.Contains(order.DerivedStatus)
                : packageStatuses is not null
                ? status switch
                {
                    "NEW" => order => currentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && packageStatuses.Contains(package.Status))
                        || unpackagedNew.Any(newOrder => newOrder.Id == order.Id),
                    // A Hepsiburada undelivered package is Teslim Edilemedi like every other marketplace's (it was
                    // listed under Askıda instead of Kargoda before 5 Nov 2026).
                    "ON_HOLD" => order => currentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && packageStatuses.Contains(package.Status))
                        || unpackagedOnHold.Any(holdOrder => holdOrder.Id == order.Id),
                    "DELIVERED" => order => currentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && packageStatuses.Contains(package.Status))
                        || unpackagedDelivered.Any(deliveredOrder => deliveredOrder.Id == order.Id),
                    // An order without a live package that is reviewed because its marketplace status is unknown
                    // (a Hepsiburada lifecycle status the panel cannot read) is listed here too.
                    "MANUAL_REVIEW" => order => currentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && packageStatuses.Contains(package.Status))
                        || order.DerivedStatus == "MANUAL_REVIEW"
                            && !currentPackages.Any(package => package.TenantId == order.TenantId
                                && package.OrderId == order.Id
                                && package.Status != ShipmentPackageStatus.Cancelled),
                    // A parcel the carrier could not deliver and brought back is Teslim Edilemedi too, while it stays
                    // under İade Edildi (owner, 17 Nov 2026); the tabs are counted one by one, never summed.
                    "UNDELIVERED" => Either(
                        order => currentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && packageStatuses.Contains(package.Status)),
                        UndeliveredReturnInDatabase(currentPackages)),
                    _ => order => currentPackages.Any(package => package.TenantId == order.TenantId
                        && package.OrderId == order.Id
                        && packageStatuses.Contains(package.Status))
                }
                : status switch
                {
                    // originPackageIds is also present for split/cancel packages;
                    // only Trendyol's explicit creator marker identifies a resend.
                    "RESENT" => x => currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.OriginExternalPackageId != null && package.Status != ShipmentPackageStatus.Cancelled && package.CreatedBy != null && resendCreators.Contains(package.CreatedBy)
                        && (!resentNotShipped || ResentNotShippedStatuses.Contains(package.Status)))
                        // An exchange (owner, 18 Nov 2026): Trendyol's order reads never return its new package, so an order
                        // is a resend too when its exchange claim names the package (getClaims replacementOutboundpackageinfo)
                        // or the owner marked the exchange made; it waits for the carrier until the package's tracking link comes
                        // or the owner marks it handed to the carrier (ExchangeResendRules; a tracking number alone does not).
                        // Not handed over: ExchangeResendRules.NotShipped (a Hepsiburada exchange closes when its exchange line ships).
                        || exchangeClaims.Any(claim => claim.TenantId == x.TenantId && claim.OrderId == x.Id && claim.Status != ReturnClaimStatus.Cancelled
                            && claim.CustomerRequest == ReturnCustomerRequestPolicy.Change
                            && (claim.ReplacementPackageId != null || claim.ResolvedWith == ReturnCustomerRequestPolicy.Change)),
                    // UNVERIFIED (a Hepsiburada order known only from a claim) and any other order status.
                    _ => x => x.DerivedStatus == status
                };
            // A Shopify order with a current panel status (O13) is in the tabs of that status only,
            // never in the tabs of its packages: its badge shows the panel status.
            var manualStatuses = ManualShopifyStatusesForOrderTab(status);
            query = query.Where(WithManualShopifyStatus(HasCurrentManualShopifyStatus(tenantId), order => manualStatuses.Contains(order.DerivedStatus), marketplaceRule));
        }

        var search = options.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            // The customer snapshot holds identity, tax and phone numbers: a
            // term without letters (a number) is only matched against order,
            // product and barcode fields, never against the snapshot.
            // Nor against its e-mail fields: the list no longer shows the e-mail.
            var searchCustomer = SearchesCustomerSnapshot(search);
            var customerPattern = CustomerSnapshotSearchPattern(search);
            // Case-insensitive, the Turkish way too: the product title and the term are both folded
            // (TurkishSearchText), so "ışık" finds "Işıklı" on the en_US database.
            var (asTyped, folded) = SearchPatterns(search);
            var titleMatches = db.OrderLines.Where(TurkishSearchText.FoldedILike<OrderLine>(line => line.TitleSnapshot, folded, LikeEscape));
            query = query.Where(x => EF.Functions.ILike(x.OrderNumber, asTyped, LikeEscape) || EF.Functions.ILike(x.ExternalOrderId, asTyped, LikeEscape)
                || (searchCustomer && Regex.IsMatch(x.CustomerSnapshotJson, customerPattern, RegexOptions.IgnoreCase))
                || titleMatches.Any(line => line.TenantId == x.TenantId && line.OrderId == x.Id)
                || db.OrderLines.Any(line => line.TenantId == x.TenantId && line.OrderId == x.Id
                    && (EF.Functions.ILike(line.Sku, asTyped, LikeEscape) || (line.Barcode != null && EF.Functions.ILike(line.Barcode, asTyped, LikeEscape)))));
        }

        var platforms = options.Platforms?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().ToUpperInvariant()).Distinct().ToArray() ?? [];
        var platform = options.Platform?.Trim().ToUpperInvariant();
        if (platforms.Length > 0)
            query = query.Where(x => db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED") && platforms.Contains(connection.PlatformCode)));
        else if (!string.IsNullOrWhiteSpace(platform) && platform != "ALL")
            query = query.Where(x => db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED") && connection.PlatformCode == platform));

        var listing = options.Listing?.Trim().ToUpperInvariant();
        if (listing == "OPEN") query = query.Where(x => x.DerivedStatus != "DELIVERED" && x.DerivedStatus != "CANCELLED" && x.DerivedStatus != "RETURNED");
        // Micro-export orders: the "Mikro İhracat" label's rule (OrderMicroExportRules), as the row flag and the card read it.
        if (options.MicroExport == true)
            query = query.Where(OrderMicroExportRules.InDatabase);
        // Orders with some products cancelled that were not cancelled whole, open or closed.
        if (options.PartialCancel == true)
            query = query.Where(x => x.DerivedStatus != "CANCELLED" && db.OrderLines.Any(line => line.TenantId == x.TenantId && line.OrderId == x.Id && line.CancelledQuantity > 0));
        if (listing == "CLOSED") query = query.Where(x => x.DerivedStatus == "DELIVERED" || x.DerivedStatus == "CANCELLED" || x.DerivedStatus == "RETURNED");

        var cargo = options.Cargo?.Trim();
        if (!string.IsNullOrWhiteSpace(cargo) && cargo != "ALL")
            query = query.Where(x => currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.CargoProviderExternalId == cargo));

        // Sipariş Tarihi is the real order time on every platform (Trendyol's orderDate is read from its GMT+3 epoch).
        if (options.DateFrom?.ToUniversalTime() is { } dateFrom)
            query = query.Where(x => x.OrderedAt >= dateFrom);
        if (options.DateTo?.ToUniversalTime() is { } dateTo)
            query = query.Where(x => x.OrderedAt <= dateTo);
        // A line cancelled in the range: at the marketplace's time, or when the panel saw it if that time is unknown.
        if (options.CancelFrom is not null || options.CancelTo is not null)
        {
            var cancelFrom = options.CancelFrom?.ToUniversalTime();
            var cancelTo = options.CancelTo?.ToUniversalTime();
            query = query.Where(x => db.OrderLines.Any(line => line.TenantId == x.TenantId && line.OrderId == x.Id && line.CancelledQuantity > 0
                && (line.CancelledAt ?? line.CancelSeenAt) != null
                && (cancelFrom == null || (line.CancelledAt ?? line.CancelSeenAt) >= cancelFrom)
                && (cancelTo == null || (line.CancelledAt ?? line.CancelSeenAt) <= cancelTo)));
        }
    }

    // Status counters are package-based because a marketplace order may be
    // split. Use the same rule for each tab so a cancelled split package can
    // never increase the "İptal" counter while hiding its parent order.
    // A resent package the carrier has not taken yet: created, being picked, or ready to ship.
    internal static readonly ShipmentPackageStatus[] ResentNotShippedStatuses = [ShipmentPackageStatus.New, ShipmentPackageStatus.Processing, ShipmentPackageStatus.ReadyToShip];

    internal static ShipmentPackageStatus[]? PackageStatusesForOrderTab(string status) => status switch
    {
        "NEW" => [ShipmentPackageStatus.New],
        "PROCESSING" => [ShipmentPackageStatus.Processing, ShipmentPackageStatus.ReadyToShip],
        // An undelivered package is listed under Teslim Edilemedi only, not also under Kargoda.
        "SHIPPED" => [ShipmentPackageStatus.Shipped],
        "DELIVERED" => [ShipmentPackageStatus.Delivered],
        "ON_HOLD" => [ShipmentPackageStatus.OnHold],
        "CANCELLED" => [ShipmentPackageStatus.Cancelled],
        "RETURNED" => [ShipmentPackageStatus.Returned],
        "RETURN_IN_TRANSIT" => [ShipmentPackageStatus.ReturnInTransit],
        "PARTIALLY_CANCELLED" => [ShipmentPackageStatus.PartiallyCancelled],
        "MANUAL_REVIEW" => [ShipmentPackageStatus.ManualReview],
        "PAYMENT_PENDING" => [ShipmentPackageStatus.PaymentPending],
        "UNDELIVERED" => [ShipmentPackageStatus.Undelivered],
        _ => null
    };

    internal static string[]? DerivedStatusesForOrderTab(string status) => status switch
    {
        "PENDING" => DashboardMetricPolicy.PendingOrderStatuses,
        _ => null
    };

    // The panel statuses (SetShopifyOrderStatusAsync) a Shopify order is listed under in a tab.
    internal static string[] ManualShopifyStatusesForOrderTab(string status) => status switch
    {
        "PROCESSING" => ["PROCESSING", "READY_TO_SHIP"],
        "PENDING" => DashboardMetricPolicy.PendingOrderStatuses,
        "RESENT" => [],
        _ => [status]
    };

    // The raw status of a parcel the carrier could not deliver and brought back to the seller: Trendyol's
    // UnDeliveredAndReturned, and PTT AVM's gondericisine_teslim_edildi, which its mapper stores as UndeliveredAndReturned.
    // The order itself is RETURNED. Compared upper-cased, exactly as the web's undeliveredReturn does.
    internal const string UndeliveredAndReturnedRawStatus = "UNDELIVEREDANDRETURNED";

    // An undelivered-and-returned order: RETURNED with a line or a current package of that raw status (the lines and
    // packages the order list sends the web). One rule for the Teslim Edilemedi list and its count, which both read
    // FilteredOrders; the order keeps its DerivedStatus, so it stays under İade Edildi as well.
    // The packages are the ones the row lists (scan, 23 Nov 2026: the tab read every current package, the row and the web's
    // undeliveredReturn the listed ones, without Shopify's remainder placeholder; IsShopifyRemainderPackage is the one rule).
    internal Expression<Func<Order, bool>> UndeliveredReturnInDatabase(IQueryable<ShipmentPackage> currentPackages) =>
        order => order.DerivedStatus == "RETURNED"
            && (db.OrderLines.Any(line => line.TenantId == order.TenantId && line.OrderId == order.Id
                    && line.RawStatus.ToUpper() == UndeliveredAndReturnedRawStatus)
                || currentPackages.Any(package => package.TenantId == order.TenantId && package.OrderId == order.Id
                    && package.RawStatus.ToUpper() == UndeliveredAndReturnedRawStatus
                    && !(package.ExternalPackageId.ToLower().StartsWith(ShopifyRemainderPrefix) && package.ExternalPackageId.ToLower().EndsWith(ShopifyRemainderSuffix)
                        && db.PlatformConnections.Any(connection => connection.TenantId == order.TenantId && connection.Id == order.ConnectionId && connection.PlatformCode == "SHOPIFY"))));

    // Shopify's "order:{id}:remainder" placeholder package: never listed on an order row, nor read by its row rules.
    private const string ShopifyRemainderPrefix = "order:";
    private const string ShopifyRemainderSuffix = ":remainder";

    internal static bool IsShopifyRemainderPackage(string? platformCode, string externalPackageId) =>
        string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
        && externalPackageId.StartsWith(ShopifyRemainderPrefix, StringComparison.OrdinalIgnoreCase)
        && externalPackageId.EndsWith(ShopifyRemainderSuffix, StringComparison.OrdinalIgnoreCase);

    // left(order) || right(order), as one predicate.
    private static Expression<Func<Order, bool>> Either(Expression<Func<Order, bool>> left, Expression<Func<Order, bool>> right)
    {
        var order = left.Parameters[0];
        return Expression.Lambda<Func<Order, bool>>(
            Expression.OrElse(left.Body, ReplacingExpressionVisitor.Replace(right.Parameters[0], order, right.Body)), order);
    }

    // order => isManual(order) ? manualRule(order) : marketplaceRule(order), as one predicate.
    private static Expression<Func<Order, bool>> WithManualShopifyStatus(Expression<Func<Order, bool>> isManual, Expression<Func<Order, bool>> manualRule, Expression<Func<Order, bool>> marketplaceRule)
    {
        var order = marketplaceRule.Parameters[0];
        Expression Body(Expression<Func<Order, bool>> rule) => ReplacingExpressionVisitor.Replace(rule.Parameters[0], order, rule.Body);
        var manual = Body(isManual);
        return Expression.Lambda<Func<Order, bool>>(
            Expression.OrElse(Expression.AndAlso(manual, Body(manualRule)), Expression.AndAlso(Expression.Not(manual), marketplaceRule.Body)),
            order);
    }

    // The first "shipped" status of each order: when it was handed to the carrier.
    private IQueryable<OrderStatusHistory> ShippedAtQuery(Guid tenantId) =>
        db.OrderStatusHistory.AsNoTracking().Where(h => h.TenantId == tenantId && h.CanonicalStatus == "SHIPPED");

    private static string NormalizeOrderSort(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "DATE_ASC" => "DATE_ASC",
        "SHIP_ASC" => "SHIP_ASC",
        "SHIP_DESC" => "SHIP_DESC",
        "DUE_ASC" => "DUE_ASC",
        "DUE_DESC" => "DUE_DESC",
        _ => "DATE_DESC"
    };
}
