using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService(AppDbContext db, CursorCodec cursors, IConfiguration configuration, IProductVisualLookupPort productVisuals, IOrderPort orders, IReturnPort returns, IPrivateFileStorage files, TimeProvider timeProvider) : IMarketplaceSalesService
{
    // A real Trendyol package supersedes the zero-ID payment placeholder, and a
    // package another package names as its origin (a split UnPacked parent, a
    // resent original) is superseded by it. Retain their history without
    // displaying, counting or invoicing them as live packages.
    internal IQueryable<ShipmentPackage> CurrentShipmentPackages() => CurrentShipmentPackages(db);

    // Also used by the dashboard and invoicing, so their package sets follow the same rule as the order screens.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db) => CurrentShipmentPackages(db, db.ShipmentPackages.AsNoTracking());

    // The same rule over a caller's source, e.g. tracked packages that the invoicing precheck updates.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db, IQueryable<ShipmentPackage> packages) => packages
        .Where(package => package.RawStatus != ShipmentPackageSupersession.LocallyUnpackedRawStatus)
        .Where(package => (package.ExternalPackageId != "0"
            || !db.PlatformConnections.Any(connection => connection.TenantId == package.TenantId
                && connection.Id == package.ConnectionId && connection.PlatformCode == "TRENDYOL")
            || !db.ShipmentPackages.Any(real => real.TenantId == package.TenantId
                && real.ConnectionId == package.ConnectionId && real.OrderId == package.OrderId
                && real.ExternalPackageId != "0" && real.ExternalPackageId != ""))
            && !db.ShipmentPackages.Any(child => child.TenantId == package.TenantId
                && child.ConnectionId == package.ConnectionId && child.OrderId == package.OrderId
                && child.OriginExternalPackageId == package.ExternalPackageId
                && child.ExternalPackageId != package.ExternalPackageId));

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
        var variantIds = lines.Where(x => x.VariantId is not null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var lineSkus = lines.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var lineSkuKeys = lines.SelectMany(x => CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.Ordinal).ToArray();
        var marketplaceSkuKeys = lines.Select(x => MarketplaceVariantLinkCoverage.Normalize(x.Sku)).Where(x => x.Length > 0).Distinct().ToArray();
        var lineBarcodes = lines.Select(x => x.Barcode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var lineBarcodeKeys = lines.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lineVariantLinks = marketplaceSkuKeys.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId
                    && connectionIds.Contains(x.ConnectionId)
                    && (marketplaceSkuKeys.Contains(x.ExternalId.Trim().ToUpper()) || x.ExternalSku != null && marketplaceSkuKeys.Contains(x.ExternalSku.Trim().ToUpper())))
                .ToListAsync(cancellationToken);
        var linkedVariantIds = lineVariantLinks.Select(link => link.VariantId).Distinct().ToArray();
        var variantRows = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId &&
            (variantIds.Contains(x.Id) || linkedVariantIds.Contains(x.Id) || lineSkus.Contains(x.Sku) || lineSkuKeys.Contains(x.SkuNormalized) ||
             lineBarcodes.Contains(x.Barcode) || lineBarcodeKeys.Contains(x.BarcodeNormalized))).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variants = variantRows.ToDictionary(x => x.Id, x => x);
        var variantsBySku = variantRows.GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var variantsByBarcode = BarcodeVariantLookup(variantRows);
        var variantsByMarketplaceIdentity = lineVariantLinks
            .SelectMany(link => LinkIdentities(link).Select(identity => (Key: (link.ConnectionId, ExternalId: identity), link.VariantId)))
            .GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.First().VariantId);
        var imageUrls = await MediaUrls(tenantId, variantRows.Select(x => (Guid?)x.Id), cancellationToken);
        var now = timeProvider.GetUtcNow();
        ProductVariant? ResolveOrderVariant(Order order, OrderLine line)
        {
            var variant = ResolveVariant(line, variants, variantsBySku, variantsByBarcode);
            if (variant is not null) return variant;
            var key = (order.ConnectionId, ExternalId: MarketplaceVariantLinkCoverage.Normalize(line.Sku));
            return variantsByMarketplaceIdentity.TryGetValue(key, out var variantId) ? variants.GetValueOrDefault(variantId) : null;
        }
        var rows = orders.Select(order =>
        {
            var orderLines = (linesByOrder.GetValueOrDefault(order.Id) ?? [])
                .Where(line => OrderLinePresentationPolicy.ShouldShowInOrderList(order.DerivedStatus, line.OrderedQuantity, line.CancelledQuantity))
                .ToList();
            var connection = connections.GetValueOrDefault(order.ConnectionId);
            var orderPackages = (packagesByOrder.GetValueOrDefault(order.Id) ?? [])
                .Where(package => !string.Equals(connection?.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
                    || !(package.ExternalPackageId.StartsWith("order:", StringComparison.OrdinalIgnoreCase)
                        && package.ExternalPackageId.EndsWith(":remainder", StringComparison.OrdinalIgnoreCase)))
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
            var dueAt = order.ShipmentDueAt ?? OperationalDueAt(order.CustomerSnapshotJson);
            var unverifiedWithoutPackage = OpenOrderLifecyclePolicy.IsHepsiburadaOrderUnverifiedWithoutPackage(
                connection?.PlatformCode,
                order.DerivedStatus,
                orderPackages.Count(candidate => candidate.Status != ShipmentPackageStatus.Cancelled),
                order.OrderedAt,
                now);
            var displayStatus = unverifiedWithoutPackage ? "UNVERIFIED" : order.DerivedStatus;
            var lineViews = orderLines.Select(x =>
            {
                var variant = ResolveOrderVariant(order, x);
                var source = SourceLine(x.SourceSnapshotJson);
                var images = LineImageUrls(string.IsNullOrWhiteSpace(source.ImageUrl) ? null : source.ImageUrl, variant is null ? null : imageUrls.GetValueOrDefault(variant.Id));
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
                customer.Name, customer.OrderType, customer.IsMicroExport, dueAt,
                OpenOrderLifecyclePolicy.ShouldShowShipmentDeadlineWarning(connection?.PlatformCode, displayStatus, dueAt, now, unverifiedWithoutPackage), PackageInvoiceLabel(package, invoice, order.CustomerSnapshotJson, orderPackages.Select(x => x.RawStatus), connection?.PlatformCode, invoice is not null && confirmedDeliveryInvoiceIds.Contains(invoice.Id)),
                package?.CargoProviderExternalId ?? JsonText(order.CustomerSnapshotJson, "marketplaceCargoProviderName"), package?.CargoTrackingNumber,
                orderLines.Select(x => ResolveOrderVariant(order, x)).Where(x => x is not null).Select(x => imageUrls.GetValueOrDefault(x!.Id)).FirstOrDefault(x => x is not null),
                orderLines.Sum(x => OrderLinePresentationPolicy.ActiveQuantity(x.OrderedQuantity, x.CancelledQuantity)), MaskIdentityNumber(customer.TaxOrIdentityNumber),
                ListAddress(order.ShipmentAddressSnapshotJson), order.GrossAmount, order.DiscountAmount,
                lineViews, packageViews, invoice?.Id, invoiceDocumentUrl,
                connection?.PlatformCode == "N11" ? N11OrderInfo(orderLines.Select(x => x.SourceSnapshotJson), order.CustomerSnapshotJson) : null,
                shippedAtByOrder.TryGetValue(order.Id, out var shippedAt) ? shippedAt : null,
                order.LabelPrintedAt);
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

    // The tabs the order summary counts; "ALL" is the list without a status tab.
    internal static readonly string[] OrderSummaryTabs = ["ALL", "NEW", "PROCESSING", "SHIPPED", "DELIVERED", "RESENT", "ON_HOLD", "CANCELLED", "RETURNED", "RETURN_IN_TRANSIT", "PARTIALLY_CANCELLED", "MANUAL_REVIEW", "PENDING", "UNVERIFIED", "PAYMENT_PENDING", "UNDELIVERED"];

    // The first print is kept; printing again later does not move it. A plain column update: the
    // order's version and the worker's concurrent writes are not touched.
    public async Task<bool> MarkLabelPrintedAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var exists = await db.Orders.AnyAsync(x => x.TenantId == tenantId && x.Id == orderId, cancellationToken);
        if (!exists) return false;
        await db.Orders.Where(x => x.TenantId == tenantId && x.Id == orderId && x.LabelPrintedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LabelPrintedAt, now), cancellationToken);
        return true;
    }

    internal void ApplyOrderFilters(ref IQueryable<Order> query, OrderListQuery options, Guid tenantId)
    {
        if (options.LabelPrinted is { } printed)
            query = printed ? query.Where(x => x.LabelPrintedAt != null) : query.Where(x => x.LabelPrintedAt == null);
        // Superseded packages (split parents, resent originals, the zero-ID
        // placeholder) neither match a tab nor count as invoice targets.
        var currentPackages = CurrentShipmentPackages();
        var resendCreators = new[] { "transfer", "resend", "replacement" };
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
                "FATURA_PLATFORMA_AKTARILMADI" => query.Where(x => db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && (i.Status == InvoiceStatus.MarketplaceFailed || ((i.Status == InvoiceStatus.Accepted || i.Status == InvoiceStatus.MarketplacePending) && i.LastErrorCode != null)))),
                "FATURA_REDDEDILDI" => query.Where(x =>
                    db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                        && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                        && (i.Status == InvoiceStatus.Rejected || i.Status == InvoiceStatus.ValidationFailed || i.Status == InvoiceStatus.ManualReview))
                    || currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.ManualInvoiceStatus == null && package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Rejected)),
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
                "FATURA_BEKLIYOR" => query.Where(x =>
                    currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.ManualInvoiceStatus == "PENDING")
                    || !currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.ManualInvoiceStatus == "UPLOADED")
                        && (currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.NotInvoiced)
                            || x.CustomerSnapshotJson.Contains("NOTINVOICED") || x.CustomerSnapshotJson.Contains("NOT_INVOICED"))),
                "FATURA_ISLENIYOR" => query.Where(x => db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && (i.Status == InvoiceStatus.Draft || i.Status == InvoiceStatus.Validating || i.Status == InvoiceStatus.Ready || i.Status == InvoiceStatus.Submitting || i.Status == InvoiceStatus.Submitted
                        || ((i.Status == InvoiceStatus.Accepted || i.Status == InvoiceStatus.MarketplacePending) && i.LastErrorCode == null)))),
                _ => query.Where(_ => false)
            };
        }

        var invoiceType = options.InvoiceType?.Trim().ToUpperInvariant();
        // Trendyol always sends the company fields, often as empty strings.
        // Checking only for the property name therefore classified nearly every
        // individual order as corporate. Match a non-empty JSON value instead.
        if (invoiceType == "KURUMSAL")
            query = query.Where(x =>
                x.CustomerSnapshotJson.Contains("\"commercial\":true")
                || x.CustomerSnapshotJson.Contains("\"commercial\": true")
                || EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"company\":\"_%")
                || EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"company\": \"_%")
                || EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"companyName\":\"_%")
                || EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"companyName\": \"_%")
                || EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"taxOffice\":\"_%")
                || EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"taxOffice\": \"_%"));
        else if (invoiceType == "BIREYSEL")
            query = query.Where(x =>
                !x.CustomerSnapshotJson.Contains("\"commercial\":true")
                && !x.CustomerSnapshotJson.Contains("\"commercial\": true")
                && !EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"company\":\"_%")
                && !EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"company\": \"_%")
                && !EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"companyName\":\"_%")
                && !EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"companyName\": \"_%")
                && !EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"taxOffice\":\"_%")
                && !EF.Functions.Like(x.InvoiceAddressSnapshotJson, "%\"taxOffice\": \"_%"));

        var invoiceRegion = options.InvoiceRegion?.Trim().ToUpperInvariant();
        if (invoiceRegion == "MICRO_EXPORT")
            query = query.Where(x => x.CustomerSnapshotJson.Contains("\"micro\":true") || x.CustomerSnapshotJson.Contains("\"micro\": true") || x.CustomerSnapshotJson.Contains("\"microExport\":true") || x.CustomerSnapshotJson.Contains("\"microExport\": true") || x.CustomerSnapshotJson.Contains("\"3pByTrendyol\":true") || x.CustomerSnapshotJson.Contains("MICRO") || x.CustomerSnapshotJson.Contains("İHRAC") || x.CustomerSnapshotJson.Contains("IHRAC"));
        else if (invoiceRegion == "TR")
            query = query.Where(x => !x.CustomerSnapshotJson.Contains("\"micro\":true") && !x.CustomerSnapshotJson.Contains("\"micro\": true") && !x.CustomerSnapshotJson.Contains("\"microExport\":true") && !x.CustomerSnapshotJson.Contains("\"microExport\": true") && !x.CustomerSnapshotJson.Contains("\"3pByTrendyol\":true") && !x.CustomerSnapshotJson.Contains("MICRO") && !x.CustomerSnapshotJson.Contains("İHRAC") && !x.CustomerSnapshotJson.Contains("IHRAC"));

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
                ? order => order.DerivedStatus == "CANCELLED"
                    || (!db.PlatformConnections.Any(connection => connection.TenantId == order.TenantId && connection.Id == order.ConnectionId && connection.PlatformCode == "SHOPIFY")
                        && currentPackages.Any(package => package.TenantId == order.TenantId && package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Cancelled))
                : derivedStatuses is not null
                ? order => derivedStatuses.Contains(order.DerivedStatus)
                : packageStatuses is not null
                ? status switch
                {
                    "NEW" => order => currentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && packageStatuses.Contains(package.Status))
                        || unpackagedNew.Any(newOrder => newOrder.Id == order.Id),
                    "ON_HOLD" => order => currentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && (packageStatuses.Contains(package.Status)
                                || package.Status == ShipmentPackageStatus.Undelivered
                                    && db.PlatformConnections.Any(connection => connection.TenantId == package.TenantId
                                        && connection.Id == package.ConnectionId
                                        && connection.PlatformCode == "HEPSIBURADA")))
                        || unpackagedOnHold.Any(holdOrder => holdOrder.Id == order.Id),
                    "DELIVERED" => order => currentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && packageStatuses.Contains(package.Status))
                        || unpackagedDelivered.Any(deliveredOrder => deliveredOrder.Id == order.Id),
                    "SHIPPED" => order => currentPackages.Any(package => package.TenantId == order.TenantId
                        && package.OrderId == order.Id
                        && packageStatuses.Contains(package.Status)
                        && (package.Status != ShipmentPackageStatus.Undelivered
                            || !db.PlatformConnections.Any(connection => connection.TenantId == package.TenantId
                                && connection.Id == package.ConnectionId
                                && connection.PlatformCode == "HEPSIBURADA"))),
                    _ => order => currentPackages.Any(package => package.TenantId == order.TenantId
                        && package.OrderId == order.Id
                        && packageStatuses.Contains(package.Status))
                }
                : status switch
                {
                    // originPackageIds is also present for split/cancel packages;
                    // only Trendyol's explicit creator marker identifies a resend.
                    "RESENT" => x => currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.OriginExternalPackageId != null && package.Status != ShipmentPackageStatus.Cancelled && package.CreatedBy != null && resendCreators.Contains(package.CreatedBy)),
                    // UNVERIFIED (a Hepsiburada order known only from a claim) and any other order status.
                    _ => x => x.DerivedStatus == status
                };
            // A Shopify order with a current panel status (O13) is in the tabs of that status only,
            // never in the tabs of its packages: its badge shows the panel status.
            var manualOrders = CurrentManualShopifyOrderStatuses(tenantId);
            var manualStatuses = ManualShopifyStatusesForOrderTab(status);
            query = query.Where(WithManualShopifyStatus(order => manualOrders.Any(manual => manual.Id == order.Id), order => manualStatuses.Contains(order.DerivedStatus), marketplaceRule));
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
        if (listing == "CLOSED") query = query.Where(x => x.DerivedStatus == "DELIVERED" || x.DerivedStatus == "CANCELLED" || x.DerivedStatus == "RETURNED");

        var cargo = options.Cargo?.Trim();
        if (!string.IsNullOrWhiteSpace(cargo) && cargo != "ALL")
            query = query.Where(x => currentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.CargoProviderExternalId == cargo));

        if (options.DateFrom?.ToUniversalTime() is { } dateFrom) query = query.Where(x => x.OrderedAt >= dateFrom);
        if (options.DateTo?.ToUniversalTime() is { } dateTo) query = query.Where(x => x.OrderedAt <= dateTo);
    }

    // Status counters are package-based because a marketplace order may be
    // split. Use the same rule for each tab so a cancelled split package can
    // never increase the "İptal" counter while hiding its parent order.
    internal static ShipmentPackageStatus[]? PackageStatusesForOrderTab(string status) => status switch
    {
        "NEW" => [ShipmentPackageStatus.New],
        "PROCESSING" => [ShipmentPackageStatus.Processing, ShipmentPackageStatus.ReadyToShip],
        "SHIPPED" => [ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Undelivered],
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

    // The shared catalog key (CatalogKeyNormalizer), so order and return lines find variants stored by the panel
    // or the import under the same key (K1).
    private static string NormalizeCatalogKey(string? value, int maximum) => CatalogKeyNormalizer.Key(value, maximum);

    // The codes an order or return line may name a linked variant by: the link id and the marketplace's stock code
    // (a Hepsiburada link is keyed by its hbSku and keeps the merchant SKU beside it).
    private static IEnumerable<string> LinkIdentities(MarketplaceVariantLink link)
    {
        yield return MarketplaceVariantLinkCoverage.Normalize(link.ExternalId);
        if (!string.IsNullOrWhiteSpace(link.ExternalSku)) yield return MarketplaceVariantLinkCoverage.Normalize(link.ExternalSku);
    }

    internal static string[] CatalogLookupKeys(string? value, int maximum = 160)
    {
        var normalized = NormalizeCatalogKey(value, maximum);
        if (normalized.Length == 0) return [];
        // Stored variant keys may still be in a legacy form until the deferred recompute; look up those too.
        var keys = CatalogKeyNormalizer.LookupKeys(value, maximum).ToList();
        var withoutMarketplacePadding = normalized.TrimStart('0');
        if (withoutMarketplacePadding.Length > 0
            && withoutMarketplacePadding.Length < normalized.Length
            && withoutMarketplacePadding.Any(char.IsLetter))
            keys.Add(withoutMarketplacePadding);
        return keys.ToArray();
    }

    private static Dictionary<string, ProductVariant> BarcodeVariantLookup(IEnumerable<ProductVariant> variants) => variants
        .Where(variant => !string.IsNullOrWhiteSpace(variant.Barcode))
        .SelectMany(variant => CatalogLookupKeys(variant.Barcode).Select(key => (Key: key, Variant: variant)))
        .GroupBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Select(candidate => candidate.Variant.Id).Distinct().Count() == 1)
        .ToDictionary(group => group.Key, group => group.First().Variant, StringComparer.OrdinalIgnoreCase);

    public Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, string? platform, CancellationToken cancellationToken) =>
        OrderSummaryAsync(tenantId, new OrderListQuery(Platform: platform), cancellationToken);

    // A cached answer per tenant, filters and tab set. Every open panel page polls the menu counter; without
    // this, identical counts ran side by side and their memory took the database over its limit (live
    // 20 Oct 2026: PostgreSQL killed by the out-of-memory killer while several 15-tab counts ran).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string Stamp, OrderSummaryView View)> OrderSummaryCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> OrderSummaryGates = new();
    // An answer is reused while no order or package of the tenant changed (a cheap stamp read), at most this long.
    // The full count takes seconds on live data (8 s on 20 Oct 2026), so it runs again only after a change.
    internal static readonly TimeSpan OrderSummaryCacheLifetime = TimeSpan.FromMinutes(5);

    // Each counter is the number of distinct orders its tab lists for the same filters (date,
    // search, platform, cargo, invoice, label): the tab query itself, counted once per tab in a
    // single round trip. Only the requested tabs are counted.
    public async Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, OrderListQuery options, CancellationToken cancellationToken, IReadOnlyCollection<string>? tabs = null, bool allowCached = false)
    {
        var requested = tabs is null ? OrderSummaryTabs : OrderSummaryTabs.Where(tab => tabs.Contains(tab, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (requested.Length == 0) requested = OrderSummaryTabs;
        if (!allowCached) return await CountOrderSummaryAsync(tenantId, options, requested, cancellationToken);

        var key = $"{tenantId:N}|{string.Join(',', requested)}|{JsonSerializer.Serialize(options)}";
        var stamp = await OrderSummaryStampAsync(tenantId, cancellationToken);
        if (OrderSummaryCache.TryGetValue(key, out var cached) && cached.Stamp == stamp && DateTimeOffset.UtcNow - cached.At < OrderSummaryCacheLifetime) return cached.View;
        var gate = OrderSummaryGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // A request that waited behind an identical one takes its answer.
            if (OrderSummaryCache.TryGetValue(key, out cached) && cached.Stamp == stamp && DateTimeOffset.UtcNow - cached.At < OrderSummaryCacheLifetime) return cached.View;
            var view = await CountOrderSummaryAsync(tenantId, options, requested, cancellationToken);
            if (OrderSummaryCache.Count > 1000) OrderSummaryCache.Clear();
            OrderSummaryCache[key] = (DateTimeOffset.UtcNow, stamp, view);
            return view;
        }
        finally { gate.Release(); }
    }

    // Changes of the tenant's orders, packages, return claims or connections: any of them can move a counter.
    private async Task<string> OrderSummaryStampAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId).GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Changed = group.Max(x => (DateTimeOffset?)x.UpdatedAt) }).SingleOrDefaultAsync(cancellationToken);
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId).GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Changed = group.Max(x => (DateTimeOffset?)x.UpdatedAt) }).SingleOrDefaultAsync(cancellationToken);
        var claims = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId).MaxAsync(x => (DateTimeOffset?)x.UpdatedAt, cancellationToken);
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId).SumAsync(x => (long)x.Version, cancellationToken);
        return $"{orders?.Count}|{orders?.Changed:O}|{packages?.Count}|{packages?.Changed:O}|{claims:O}|{connections}|{timeProvider.GetUtcNow():yyyyMMddHH}";
    }

    private async Task<OrderSummaryView> CountOrderSummaryAsync(Guid tenantId, OrderListQuery options, string[] requested, CancellationToken cancellationToken)
    {
        IQueryable<string>? tagged = null;
        foreach (var tab in requested)
        {
            var tabName = tab;
            var rows = FilteredOrders(tenantId, options with { Status = tab, Sort = null }).Select(_ => tabName);
            tagged = tagged is null ? rows : tagged.Concat(rows);
        }
        var counts = await tagged!.GroupBy(tab => tab).Select(group => new { Tab = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Tab, row => row.Count, cancellationToken);
        int Count(string tab) => counts.GetValueOrDefault(tab);
        return new OrderSummaryView(Count("ALL"), Count("NEW"), Count("PROCESSING"), Count("SHIPPED"), Count("DELIVERED"), Count("RESENT"), Count("ON_HOLD"),
            Count("CANCELLED"), Count("RETURNED"), Count("RETURN_IN_TRANSIT"), Count("PARTIALLY_CANCELLED"), Count("MANUAL_REVIEW"), Count("PENDING"), Count("UNVERIFIED"), Count("PAYMENT_PENDING"), Count("UNDELIVERED"));
    }

    internal IQueryable<Order> CurrentManualShopifyOrderStatuses(Guid tenantId) => db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId
        && db.PlatformConnections.Any(connection => connection.TenantId == order.TenantId
            && connection.Id == order.ConnectionId
            && connection.PlatformCode == "SHOPIFY")
        && db.OrderStatusHistory.Any(history => history.TenantId == order.TenantId
            && history.OrderId == order.Id
            && history.RawStatus.StartsWith("MANUAL_SHOPIFY_STATUS:")
            && history.CanonicalStatus == order.DerivedStatus
            && history.OccurredAt >= order.LastRemoteModifiedAt));

    // "Unpackaged" means no live package: a package the panel unpacked (UNPACKED_BY_PANEL, history
    // only) or a cancelled one leaves the order unpackaged again, so it is back in Yeni (O14).
    private IQueryable<Order> HepsiburadaUnpackagedNewOrders(Guid tenantId)
    {
        var verificationCutoff = OpenOrderLifecyclePolicy.HepsiburadaUnpackagedOrderVerificationCutoff(timeProvider.GetUtcNow());
        return db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.DerivedStatus == "NEW"
                && order.OrderedAt >= verificationCutoff
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                    && connection.Id == order.ConnectionId
                    && connection.PlatformCode == "HEPSIBURADA"
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                && !CurrentShipmentPackages().Any(package => package.TenantId == tenantId && package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled));
    }

    internal IQueryable<Order> HepsiburadaUnpackagedOnHoldOrders(Guid tenantId)
    {
        var verificationCutoff = OpenOrderLifecyclePolicy.HepsiburadaUnpackagedOrderVerificationCutoff(timeProvider.GetUtcNow());
        return db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.DerivedStatus == "ON_HOLD"
                && order.OrderedAt >= verificationCutoff
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                    && connection.Id == order.ConnectionId
                    && connection.PlatformCode == "HEPSIBURADA"
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                && !CurrentShipmentPackages().Any(package => package.TenantId == tenantId && package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled));
    }

    internal IQueryable<Order> HepsiburadaUnpackagedDeliveredOrders(Guid tenantId)
    {
        var verificationCutoff = OpenOrderLifecyclePolicy.HepsiburadaUnpackagedOrderVerificationCutoff(timeProvider.GetUtcNow());
        return db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.DerivedStatus == "DELIVERED"
                && order.OrderedAt >= verificationCutoff
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                    && connection.Id == order.ConnectionId
                    && connection.PlatformCode == "HEPSIBURADA"
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                && !CurrentShipmentPackages().Any(package => package.TenantId == tenantId && package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled));
    }

    internal IQueryable<Order> ExcludeStaleUnpackagedHepsiburadaOrders(IQueryable<Order> query, Guid tenantId)
    {
        return query.ExcludeStaleUnpackaged(db, tenantId, timeProvider.GetUtcNow());
    }

    public async Task<ServiceResult<OrderDetailView>> OrderAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        // Detail pages also read the persisted snapshot; they never call the marketplace.
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (order is null) return NotFound<OrderDetailView>();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var activeOrderLines = orderLines.Where(line => OrderLinePresentationPolicy.HasActiveQuantity(line.OrderedQuantity, line.CancelledQuantity)).ToList();
        var activeLineIds = activeOrderLines.Select(line => line.Id).ToArray();
        var allocatedQuantities = await ActiveAllocatedQuantitiesAsync(tenantId, activeLineIds, cancellationToken);
        var variantIds = activeOrderLines.Where(x => x.VariantId is not null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var lineSkus = activeOrderLines.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var lineSkuKeys = activeOrderLines.SelectMany(x => CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.Ordinal).ToArray();
        var lineBarcodes = activeOrderLines.Select(x => x.Barcode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var lineBarcodeKeys = activeOrderLines.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var variantRows = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId &&
            (variantIds.Contains(x.Id) || lineSkus.Contains(x.Sku) || lineSkuKeys.Contains(x.SkuNormalized) ||
             lineBarcodes.Contains(x.Barcode) || lineBarcodeKeys.Contains(x.BarcodeNormalized))).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variants = variantRows.ToDictionary(x => x.Id, x => x);
        var variantsBySku = variantRows.GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var variantsByBarcode = BarcodeVariantLookup(variantRows);
        var imageUrls = await MediaUrls(tenantId, variantRows.Select(x => (Guid?)x.Id), cancellationToken);
        var lines = activeOrderLines.Select(x =>
        {
            var variant = ResolveVariant(x, variants, variantsBySku, variantsByBarcode);
            var source = SourceLine(x.SourceSnapshotJson);
            var images = LineImageUrls(source.ImageUrl, variant is null ? null : imageUrls.GetValueOrDefault(variant.Id));
            return new OrderLineView(x.Id, x.Sku, x.Barcode, x.TitleSnapshot, x.OrderedQuantity, x.CancelledQuantity, x.ShippedQuantity, x.DeliveredQuantity, x.ReturnedQuantity, x.UnitPrice, x.VatRate, x.RawStatus, x.VariantId, variant?.ModelCode ?? source.ModelCode, variant?.OptionSignature ?? source.OptionSignature, images.ImageUrl, allocatedQuantities.GetValueOrDefault(x.Id), FallbackImageUrl: images.FallbackImageUrl);
        }).ToList();
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == order.ConnectionId, cancellationToken);
        var packages = await CurrentShipmentPackages().Where(x => x.TenantId == tenantId && x.OrderId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var visiblePackages = packages
            .Where(package => !string.Equals(connection?.PlatformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
                || !(package.ExternalPackageId.StartsWith("order:", StringComparison.OrdinalIgnoreCase)
                    && package.ExternalPackageId.EndsWith(":remainder", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var invoices = await db.Invoices.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.OriginalInvoiceId == null
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var displayPackage = SelectDisplayPackage(packages, connection?.PlatformCode);
        var invoice = packages.Select(x => invoices.FirstOrDefault(invoice => invoice.PackageId == x.Id)).FirstOrDefault(x => x is not null)
            ?? invoices.FirstOrDefault(x => x.PackageId == null)
            ?? invoices.FirstOrDefault();
        var invoiceTargetPackages = invoice?.PackageId is { } invoicePackageId
            ? packages.Where(package => package.Id == invoicePackageId && package.Status != ShipmentPackageStatus.Cancelled).ToArray()
            : packages.Where(package => package.Status != ShipmentPackageStatus.Cancelled).ToArray();
        var invoiceTargetPackageIds = invoiceTargetPackages.Select(package => package.Id).ToArray();
        var currentInvoiceDeliveryStates = invoice is null || invoiceTargetPackageIds.Length == 0
            ? []
            : await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(state => state.TenantId == tenantId && state.InvoiceId == invoice.Id
                    && state.PackageId.HasValue && invoiceTargetPackageIds.Contains(state.PackageId.Value))
                .Select(state => new { state.PackageId, state.Status, state.UpdatedAt })
                .ToListAsync(cancellationToken);
        var historicalConfirmedInvoiceDeliveries = invoice is null || invoiceTargetPackageIds.Length == 0
            ? []
            : await db.MarketplaceDeliveries.AsNoTracking()
                .Where(delivery => delivery.TenantId == tenantId && delivery.InvoiceId == invoice.Id
                    && delivery.PackageId.HasValue && invoiceTargetPackageIds.Contains(delivery.PackageId.Value)
                    && delivery.Status == "CONFIRMED")
                .Select(delivery => delivery.PackageId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);
        var confirmedInvoicePackageIds = invoiceTargetPackages
            .Where(package => MarketplaceInvoiceDeliveryEvidencePolicy.HasConfirmedDelivery(
                currentInvoiceDeliveryStates.Where(state => state.PackageId == package.Id)
                    .OrderByDescending(state => state.UpdatedAt).Select(state => state.Status).FirstOrDefault(),
                historicalConfirmedInvoiceDeliveries.Contains(package.Id)))
            .Select(package => package.Id)
            .ToArray();
        var deliveryConfirmed = invoice is not null && MarketplaceInvoiceDeliveryEvidencePolicy.AllTargetsConfirmed(
            invoiceTargetPackageIds, confirmedInvoicePackageIds);
        var invoiceDocumentUrl = packages
            .Select(x => ValidInvoiceDocumentUrl(x.MarketplaceInvoiceUrl))
            .FirstOrDefault(x => x is not null)
            ?? InvoiceDocumentUrl(order.CustomerSnapshotJson);
        var customer = Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
        return ServiceResult<OrderDetailView>.Ok(new(
            order.Id, order.OrderNumber, order.DerivedStatus, order.Currency, order.GrossAmount, order.DiscountAmount, order.NetAmount, order.OrderedAt,
            lines, visiblePackages.Select(x => Map(x, order.OrderNumber)).ToList(), order.Version,
            order.ConnectionId, connection?.PlatformCode ?? "TRENDYOL", connection?.DisplayName ?? "Trendyol",
            customer.Name, customer.Email, customer.TaxOrIdentityNumber, customer.OrderType, customer.IsMicroExport,
            order.ShipmentAddressSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentDueAt ?? OperationalDueAt(order.CustomerSnapshotJson), PackageInvoiceLabel(displayPackage, invoice, order.CustomerSnapshotJson, packages.Select(x => x.RawStatus), connection?.PlatformCode, deliveryConfirmed),
            customer.Phone, customer.IsEInvoiceAvailable, invoiceDocumentUrl, ListAddress(order.ShipmentAddressSnapshotJson)));
    }

    public async Task<ServiceResult<string>> ProductImageAsync(Guid tenantId, string? barcode, string correlationId, CancellationToken cancellationToken, Guid? connectionId = null, string? productName = null)
    {
        var normalizedBarcode = barcode?.Trim() ?? "";
        var normalizedProductName = productName?.Trim();
        if ((string.IsNullOrWhiteSpace(normalizedBarcode) || normalizedBarcode.Length > 128)
            && (string.IsNullOrWhiteSpace(normalizedProductName) || normalizedProductName.Length > 320))
            return ServiceResult<string>.Fail("PRODUCT_BARCODE_INVALID", "Geçerli bir ürün barkodu gereklidir.", 400);

        var connection = connectionId is { } requestedConnectionId
            ? await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId
                && x.Id == requestedConnectionId
                && (x.Status == "ACTIVE" || x.Status == "VERIFIED")
                && (x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY"), cancellationToken)
            : null;
        if (connectionId is not null && connection is null) return NotFound<string>();

        // Questions sometimes have only a product title and no usable image,
        // SKU, or barcode. Reuse a unique local catalog title match in that case.
        if (!string.IsNullOrWhiteSpace(normalizedProductName) && normalizedProductName.Length <= 320)
        {
            var matchingProductIds = await db.Products.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Title.ToUpper() == normalizedProductName.ToUpper())
                .Select(x => x.Id).Take(2).ToListAsync(cancellationToken);
            if (matchingProductIds.Count == 1)
            {
                var titleVariantIds = await db.ProductVariants.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ProductId == matchingProductIds[0])
                    .OrderBy(x => x.SortOrder).Select(x => (Guid?)x.Id).Take(100).ToListAsync(cancellationToken);
                var titleImages = await MediaUrls(tenantId, titleVariantIds, cancellationToken);
                var titleImage = titleVariantIds.Where(id => id is not null)
                    .Select(id => titleImages.GetValueOrDefault(id!.Value))
                    .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
                if (!string.IsNullOrWhiteSpace(titleImage)) return ServiceResult<string>.Ok(titleImage);
            }
        }

        // Order rows can use a marketplace merchant SKU when the provider
        // does not return a barcode. Prefer this order's own marketplace link,
        // then use a unique local SKU/barcode match before asking that same
        // marketplace for its image.
        var catalogKey = NormalizeCatalogKey(normalizedBarcode, 160);
        if (catalogKey.Length > 0)
        {
            var lookupKeys = CatalogLookupKeys(normalizedBarcode, 160);
            var linkedVariantIds = connection is null
                ? []
                : await db.MarketplaceVariantLinks.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId == connection.Id
                        && (lookupKeys.Contains(x.ExternalId.Trim().ToUpper()) || x.ExternalSku != null && lookupKeys.Contains(x.ExternalSku.Trim().ToUpper())))
                    .Select(x => x.VariantId)
                    .Distinct()
                    .Take(2)
                    .ToListAsync(cancellationToken);
            Guid? linkedVariantId = linkedVariantIds.Count == 1 ? linkedVariantIds[0] : null;
            if (linkedVariantId is { } mappedVariantId)
            {
                var mappedImage = (await MediaUrls(tenantId, [mappedVariantId], cancellationToken)).GetValueOrDefault(mappedVariantId);
                if (!string.IsNullOrWhiteSpace(mappedImage)) return ServiceResult<string>.Ok(mappedImage);
            }

            var variantIds = await db.ProductVariants.AsNoTracking()
                .Where(x => x.TenantId == tenantId
                    && (x.Sku == normalizedBarcode || lookupKeys.Contains(x.SkuNormalized)
                        || x.Barcode == normalizedBarcode || x.BarcodeNormalized != null && lookupKeys.Contains(x.BarcodeNormalized)))
                .OrderBy(x => x.Id)
                .Select(x => (Guid?)x.Id)
                .Take(2)
                .ToListAsync(cancellationToken);
            if (variantIds.Count == 1 && variantIds[0] is { } localVariantId)
            {
                var localImage = (await MediaUrls(tenantId, [localVariantId], cancellationToken)).GetValueOrDefault(localVariantId);
                if (!string.IsNullOrWhiteSpace(localImage)) return ServiceResult<string>.Ok(localImage);
            }

            // Question feeds often provide the marketplace SKU plus the catalog
            // model code, while our local catalog stores images on the model's
            // variants. Resolve the shared product image when a SKU lookup has
            // no exact variant match.
            var modelProductIds = await db.ProductVariants.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ModelCode != null
                    && x.ModelCode.Trim().ToUpper() == catalogKey.ToUpper())
                .Select(x => x.ProductId)
                .Distinct()
                .Take(2)
                .ToListAsync(cancellationToken);
            if (modelProductIds.Count == 1)
            {
                var modelVariantIds = await db.ProductVariants.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ProductId == modelProductIds[0])
                    .OrderBy(x => x.SortOrder)
                    .Select(x => (Guid?)x.Id)
                    .Take(100)
                    .ToListAsync(cancellationToken);
                var modelImages = await MediaUrls(tenantId, modelVariantIds, cancellationToken);
                var modelImage = modelVariantIds
                    .Where(id => id is not null)
                    .Select(id => modelImages.GetValueOrDefault(id!.Value))
                    .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
                if (!string.IsNullOrWhiteSpace(modelImage)) return ServiceResult<string>.Ok(modelImage);
            }
        }

        // Older marketplace question records may contain SKUs that are no
        // longer present in the current catalog. Reuse image URLs captured on
        // this same store's historical order lines, first by exact SKU/barcode,
        // then by the exact product title supplied by the question.
        if (connection is not null)
        {
            var skuSnapshots = await (
                from line in db.OrderLines.AsNoTracking()
                join order in db.Orders.AsNoTracking()
                    on new { line.TenantId, line.OrderId } equals new { order.TenantId, OrderId = order.Id }
                where line.TenantId == tenantId && order.ConnectionId == connection.Id
                    && (line.Sku == normalizedBarcode || line.Barcode == normalizedBarcode)
                orderby order.OrderedAt descending
                select line.SourceSnapshotJson
            ).Take(10).ToListAsync(cancellationToken);
            foreach (var snapshot in skuSnapshots)
            {
                var snapshotImage = NormalizeImageUrl(SourceImageUrl(snapshot ?? "{}"));
                if (!string.IsNullOrWhiteSpace(snapshotImage)) return ServiceResult<string>.Ok(snapshotImage);
            }

            if (!string.IsNullOrWhiteSpace(normalizedProductName) && normalizedProductName.Length <= 320)
            {
                var titleSnapshots = await (
                    from line in db.OrderLines.AsNoTracking()
                    join order in db.Orders.AsNoTracking()
                        on new { line.TenantId, line.OrderId } equals new { order.TenantId, OrderId = order.Id }
                    where line.TenantId == tenantId && order.ConnectionId == connection.Id
                        && line.TitleSnapshot == normalizedProductName
                    orderby order.OrderedAt descending
                    select line.SourceSnapshotJson
                ).Take(20).ToListAsync(cancellationToken);
                foreach (var snapshot in titleSnapshots)
                {
                    var snapshotImage = NormalizeImageUrl(SourceImageUrl(snapshot ?? "{}"));
                    if (!string.IsNullOrWhiteSpace(snapshotImage)) return ServiceResult<string>.Ok(snapshotImage);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedBarcode)) return NotFound<string>();
        connection ??= await ActiveTrendyolConnection(tenantId, cancellationToken);
        if (connection is null) return NotFound<string>();

        var result = await productVisuals.FindByBarcodeAsync(
            new AdapterContext(tenantId, connection.Id, correlationId, $"order-product-image:{normalizedBarcode}", timeProvider.GetUtcNow().AddSeconds(20)),
            normalizedBarcode,
            cancellationToken);
        if (!result.IsSuccess)
            return ServiceResult<string>.Fail("LIVE_PRODUCT_IMAGE_READ_FAILED", result.Error?.SafeMessage ?? "Ürün görseli okunamadı.", result.Error?.HttpStatus is >= 400 and <= 599 ? result.Error.HttpStatus.Value : 502);

        var image = result.Value is null ? null : NormalizeImageUrl(SourceImageUrl(result.Value.RawJson));
        return image is not null ? ServiceResult<string>.Ok(image) : NotFound<string>();
    }

    // The shipments page: live packages only (no split parent, resent original or placeholder),
    // the latest status change first, paged by (StatusOccurredAt, Id) (audit 2026-10-20 S18).
    private const string ShipmentCursorSort = "SHIPMENTS";

    public async Task<PageResult<ShipmentView>> ShipmentsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken)
    {
        var query = CurrentShipmentPackages().Where(x => x.TenantId == tenantId && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")));
        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ShipmentStatusFilter(status) ?? throw new ArgumentException("Gönderi durumu filtresi tanınmıyor.", nameof(status));
            query = query.Where(x => x.Status == parsed);
        }
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!cursors.TryDecodeOrder(after, out var cursorSort, out _, out var cursorAt, out var cursorId) || cursorSort != ShipmentCursorSort)
                throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(after));
            query = query.Where(x => x.StatusOccurredAt < cursorAt || x.StatusOccurredAt == cursorAt && x.Id.CompareTo(cursorId) < 0);
        }
        var rows = await (from package in query
                          join order in db.Orders.AsNoTracking() on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                          orderby package.StatusOccurredAt descending, package.Id descending
                          select new { Package = package, order.OrderNumber }).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        var items = rows.Take(limit).ToList();
        var last = items.LastOrDefault();
        return new(items.Select(x => Map(x.Package, x.OrderNumber)).ToList(), hasMore && last is not null ? cursors.EncodeOrder(ShipmentCursorSort, null, last.Package.StatusOccurredAt, last.Package.Id) : null, hasMore);
    }

    // The page's filter values are the wire names (READY_TO_SHIP); the enum names are accepted too.
    internal static ShipmentPackageStatus? ShipmentStatusFilter(string value)
    {
        var compact = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse<ShipmentPackageStatus>(compact, true, out var parsed) && Enum.IsDefined(parsed) && !compact.All(char.IsDigit) ? parsed : null;
    }

    public async Task<ServiceResult<ShipmentDetailView>> ShipmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var row = await (from package in db.ShipmentPackages.AsNoTracking() where package.TenantId == tenantId && package.Id == id && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == package.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")) join order in db.Orders.AsNoTracking() on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id } join connection in db.PlatformConnections.AsNoTracking() on new { package.TenantId, ConnectionId = package.ConnectionId } equals new { connection.TenantId, ConnectionId = connection.Id } select new { Package = package, order.OrderNumber, connection.PlatformCode }).SingleOrDefaultAsync(cancellationToken); if (row is null) return NotFound<ShipmentDetailView>();
        var stage = await IsStageConnection(tenantId, row.Package.ConnectionId, cancellationToken);
        var actions = stage
            ? row.PlatformCode == "HEPSIBURADA" ? HepsiburadaShipmentActionsFor(row.Package) : ShipmentActions
            : await CapabilityValues(tenantId, row.Package.ConnectionId, MarketplaceCapabilities.ShipmentWrite, "allowedActions", cancellationToken);
        if (row.Package.Status == ShipmentPackageStatus.PaymentPending)
            actions = [];
        // n11's capability is proven by its first accepted action; once proven, every n11 package action is offered.
        if (!stage && row.PlatformCode == "N11" && actions.Count > 0)
            actions = MarketplaceJobProcessor.N11ShipmentActions;
        // Pazarama has no sandbox and nothing to probe without writing; the actions follow the package state and every
        // send still passes the write gate (global flag, connection switch, shipment policy).
        if (row.PlatformCode == "PAZARAMA")
            actions = row.Package.Status == ShipmentPackageStatus.PaymentPending ? [] : PazaramaShipmentActionsFor(row.Package);
        if (!stage && row.PlatformCode == "HEPSIBURADA")
        {
            var evidencedActions = actions;
            actions = HepsiburadaShipmentActionsFor(row.Package)
                .Where(action => evidencedActions.Contains(action, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
        // Only the actions the package's state allows are offered; a superseded package (a split
        // parent, a resent original) takes none.
        if (row.PlatformCode is "TRENDYOL" or "N11")
            actions = actions.Where(action => (row.PlatformCode == "N11" ? N11ShipmentActionStateError(row.Package, action) : ShipmentActionStateError(row.Package, action)) is null).ToArray();
        if (!await CurrentShipmentPackages().AnyAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken))
            actions = [];
        IReadOnlyList<string> formats;
        if (row.PlatformCode == "HEPSIBURADA")
        {
            formats = !CommonLabelCarrierPolicy.SupportsHepsiburada(row.Package.CargoProviderExternalId)
                ? []
                : stage
                    ? HepsiburadaLabelFormats
                    : (await CapabilityValues(tenantId, row.Package.ConnectionId, MarketplaceCapabilities.LabelRead, "formats", cancellationToken))
                        .Where(format => HepsiburadaLabelFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
                        .ToArray();
        }
        else
        {
            formats = stage
                ? StageLabelFormats
                : await CapabilityValues(tenantId, row.Package.ConnectionId, MarketplaceCapabilities.LabelRead, "formats", cancellationToken);
        }
        var documents = await db.ShipmentDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.PackageId == id).OrderByDescending(x => x.DocumentVersion).Select(x => new ShipmentDocumentView(x.Id, x.DocumentKind, x.Format, x.Source, x.DocumentVersion, x.CreatedAt, x.ExpiresAt)).ToListAsync(cancellationToken);
        return ServiceResult<ShipmentDetailView>.Ok(new(Map(row.Package, row.OrderNumber), actions, formats, stage, documents, row.PlatformCode));
    }

    public async Task<ServiceResult<IReadOnlyList<RemoteCargoCompany>>> ChangeableCargoCompaniesAsync(Guid tenantId, Guid packageId, string correlationId, CancellationToken cancellationToken)
    {
        var row = await (from package in db.ShipmentPackages.AsNoTracking()
                         join connection in db.PlatformConnections.AsNoTracking() on new { package.TenantId, package.ConnectionId } equals new { connection.TenantId, ConnectionId = connection.Id }
                         where package.TenantId == tenantId && package.Id == packageId && connection.PlatformCode == "HEPSIBURADA" && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                         select new { package.ConnectionId, package.ExternalPackageId }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return ServiceResult<IReadOnlyList<RemoteCargoCompany>>.Fail("HEPSIBURADA_PACKAGE_NOT_FOUND", "Etkin Hepsiburada paketi bulunamadı.", 404);
        var result = await orders.GetChangeableCargoCompaniesAsync(
            new AdapterContext(tenantId, row.ConnectionId, correlationId, $"hb-carriers:{packageId:N}", timeProvider.GetUtcNow().AddSeconds(30)),
            row.ExternalPackageId,
            cancellationToken);
        return result.IsSuccess
            ? ServiceResult<IReadOnlyList<RemoteCargoCompany>>.Ok(result.Value!)
            : ServiceResult<IReadOnlyList<RemoteCargoCompany>>.Fail(result.Error?.Code ?? "HEPSIBURADA_CARGO_COMPANIES_FAILED", result.Error?.SafeMessage ?? "Hepsiburada kargo firmaları okunamadı.", result.Error?.HttpStatus is >= 400 and <= 599 ? result.Error.HttpStatus.Value : 502);
    }

    public async Task<ServiceResult<CreateOrderPackageResult>> CreateOrderPackageInstantAsync(Guid tenantId, Guid orderId, long expectedVersion, OrderPackageCreateRequest command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 256) return Invalid<CreateOrderPackageResult>("idempotencyKey", "1 ile 256 karakter arasında Idempotency-Key başlığı gereklidir.");
        if (command is null) return Invalid<CreateOrderPackageResult>("package", "Paket alanları gereklidir.");
        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == orderId, cancellationToken);
        if (order is null) return NotFound<CreateOrderPackageResult>();
        if (order.Version != expectedVersion) return Precondition<CreateOrderPackageResult>(order.Version);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == order.ConnectionId && x.PlatformCode == "HEPSIBURADA" && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (connection is null) return ServiceResult<CreateOrderPackageResult>.Fail("HEPSIBURADA_CONNECTION_REQUIRED", "Sipariş etkin Hepsiburada bağlantısına bağlı olmalıdır.", 422);
        if (!PackageCreateFieldsValid(command)) return Invalid<CreateOrderPackageResult>("package", "Paket başlığı ve en az bir geçerli sipariş kalemi gereklidir.");

        var selectedIds = command.LineItems.Select(line => line.OrderLineId).ToArray();
        if (selectedIds.Distinct().Count() != selectedIds.Length) return Invalid<CreateOrderPackageResult>("lineItems", "Aynı sipariş kalemi pakette bir kez bulunabilir.");
        var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId && selectedIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (lines.Count != selectedIds.Length) return ServiceResult<CreateOrderPackageResult>.Fail("ORDER_PACKAGE_LINES_INVALID", "Seçili sipariş kalemlerinden biri bu siparişe ait değil.", 422);
        var allocations = await ActiveAllocatedQuantitiesAsync(tenantId, selectedIds, cancellationToken);
        var remoteLines = new List<RemotePackageCreateLineRequest>();
        foreach (var requested in command.LineItems)
        {
            var line = lines.Single(item => item.Id == requested.OrderLineId);
            var remaining = Math.Max(0m, line.OrderedQuantity - line.CancelledQuantity - (allocations.GetValueOrDefault(line.Id)));
            if (requested.Quantity <= 0 || requested.Quantity > remaining || requested.Quantity != decimal.Truncate(requested.Quantity))
                return ServiceResult<CreateOrderPackageResult>.Fail("ORDER_PACKAGE_QUANTITY_INVALID", $"{line.Sku} için kalan paketlenebilir miktar {remaining} adetten fazla olamaz.", 422);
            if (string.IsNullOrWhiteSpace(line.ExternalLineId) || line.ExternalLineId.Any(char.IsWhiteSpace))
                return ServiceResult<CreateOrderPackageResult>.Fail("ORDER_PACKAGE_LINE_ID_INVALID", $"{line.Sku} Hepsiburada kalem kimliği geçersiz.", 422);
            remoteLines.Add(new(line.ExternalLineId, requested.Quantity));
        }

        var stage = string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase);
        if (!stage && !string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase)) return ServiceResult<CreateOrderPackageResult>.Fail("ENVIRONMENT_INVALID", "Paket oluşturma yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, connection.Id, cancellationToken)) return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var policy = await ExternalWritePolicyAsync(tenantId, connection.Id, MarketplaceExternalWritePolicies.Shipment, cancellationToken);
        if (!policy.Enabled) return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);

        var normalizedKey = idempotencyKey.Trim();
        var effectPrefix = $"HEPSIBURADA_PACKAGE_CREATE:{connection.Id:N}:{order.Id:N}";
        var existing = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey && x.EffectType.StartsWith(effectPrefix), cancellationToken);
        if (existing is not null)
        {
            if (existing.CompletedAt is null) return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki paket oluşturma isteğinin sonucu kesinleşmedi; sipariş eşitlemesini bekleyin.", 409);
            var packageNumber = existing.EffectType[(effectPrefix.Length + 1)..];
            return packageNumber.Length > 0
                ? ServiceResult<CreateOrderPackageResult>.Ok(new(Uri.UnescapeDataString(packageNumber)))
                : ServiceResult<CreateOrderPackageResult>.Fail("PACKAGE_CREATE_READBACK_REQUIRED", "Paket oluşturma daha önce tamamlandı; sipariş eşitlemesinden paket numarasını kontrol edin.", 409);
        }
        var completedPackageEffects = await db.ExternalEffectRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EffectType.StartsWith(effectPrefix + ":") && x.CompletedAt != null)
            .Select(x => x.EffectType)
            .ToListAsync(cancellationToken);
        if (completedPackageEffects.Count > 0)
        {
            var knownPackageNumbers = await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connection.Id && x.OrderId == orderId)
                .Select(x => x.ExternalPackageId)
                .ToListAsync(cancellationToken);
            var readbackPending = completedPackageEffects.Any(effectType =>
            {
                var encodedPackageNumber = effectType[(effectPrefix.Length + 1)..];
                var packageNumber = Uri.UnescapeDataString(encodedPackageNumber);
                return !knownPackageNumbers.Contains(packageNumber, StringComparer.Ordinal);
            });
            if (readbackPending) return ServiceResult<CreateOrderPackageResult>.Fail("PACKAGE_CREATE_READBACK_REQUIRED", "Önceki paket Hepsiburada’da oluşturuldu; sipariş/paket eşitlemesi tamamlanmadan başka paket açılamaz.", 409);
        }
        // A reservation the effect check (or the sweep) settled keeps its row as history under its
        // own name; left under the reservation key it would make every later package request on
        // this order collide with it.
        var settledReservations = await db.ExternalEffectRecords
            .Where(x => x.TenantId == tenantId && x.EffectType == effectPrefix && x.IdempotencyKey == effectPrefix && x.CompletedAt != null)
            .ToListAsync(cancellationToken);
        if (settledReservations.Count > 0)
        {
            foreach (var settled in settledReservations) settled.IdempotencyKey = HepsiburadaPackageReservationSettledKey(effectPrefix, settled.Id);
            await db.SaveChangesAsync(cancellationToken);
        }
        var pendingReservation = await db.ExternalEffectRecords.AsNoTracking().AnyAsync(
            x => x.TenantId == tenantId && x.IdempotencyKey == effectPrefix && x.EffectType == effectPrefix && x.CompletedAt == null,
            cancellationToken);
        if (pendingReservation) return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Bu sipariş için önceki paket oluşturma isteğinin sonucu kesinleşmedi; yeni paket isteği engellendi.", 409);

        var seed = remoteLines[0];
        var eligible = await orders.GetPackageableLineItemsAsync(new AdapterContext(tenantId, connection.Id, correlationId, $"{normalizedKey}:eligible", timeProvider.GetUtcNow().AddSeconds(30)), seed.LineItemId, cancellationToken);
        if (!eligible.IsSuccess && eligible.Error?.Class != AdapterErrorClass.NotFound)
            return ServiceResult<CreateOrderPackageResult>.Fail(eligible.Error?.Code ?? "PACKAGEABLE_LINES_READ_FAILED", eligible.Error?.SafeMessage ?? "Paketlenebilir kalemler okunamadı.", eligible.Error?.HttpStatus is >= 400 and <= 599 ? eligible.Error.HttpStatus.Value : 502);
        // Hepsiburada returns 404 when no *additional* lines can join the seed.
        // The seed itself can still be packaged, so preserve it as eligible.
        var eligibleLines = eligible.IsSuccess ? eligible.Value! : Array.Empty<RemotePackageableLine>();
        var compatibleIds = eligibleLines.Select(line => line.LineItemId).Append(seed.LineItemId).ToHashSet(StringComparer.Ordinal);
        if (remoteLines.Skip(1).Any(line => !compatibleIds.Contains(line.LineItemId))) return ServiceResult<CreateOrderPackageResult>.Fail("HEPSIBURADA_PACKAGE_LINES_NOT_COMPATIBLE", "Seçili kalemlerden biri Hepsiburada yanıtına göre aynı pakete eklenemez.", 422);

        var packagesBefore = await db.ShipmentPackages.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status != ShipmentPackageStatus.Cancelled, cancellationToken);
        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectPrefix, IdempotencyKey = effectPrefix, CreatedAt = timeProvider.GetUtcNow(), ConnectionId = connection.Id, TargetId = order.Id, Action = "CREATE_PACKAGE", ExpectedValue = packagesBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        db.ExternalEffectRecords.Add(effect);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Bu paket isteği başka bir işlemde ele alınıyor; sipariş durumunu kontrol edin.", 409);
        }

        var result = await orders.CreateOrderPackageAsync(
            new AdapterContext(tenantId, connection.Id, correlationId, normalizedKey, timeProvider.GetUtcNow().AddSeconds(30)),
            new(command.Barcode?.Trim(), command.CargoCompany?.Trim(), command.Carrier?.Trim(), command.CreationReason?.Trim(), command.Deci, command.ParcelQuantity,
                command.ShippingAddressLabel?.Trim(), command.ShippingModel?.Trim(), remoteLines),
            cancellationToken);
        if (!result.IsSuccess)
        {
            var error = result.Error;
            if (error is null || IsAmbiguous(error)) return await VerifyLaterAsync<CreateOrderPackageResult>(effect, cancellationToken);
            db.ExternalEffectRecords.Remove(effect);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<CreateOrderPackageResult>.Fail(error.Code, error.SafeMessage, error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502);
        }

        // No local projection here (only the effect row is written): the new package reaches the
        // order through the queued order read, which takes the "orders" lock itself.
        effect.EffectType = $"{effectPrefix}:{Uri.EscapeDataString(result.Value!.PackageNumber)}";
        effect.IdempotencyKey = normalizedKey;
        effect.CompletedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        _ = await EnqueueOrderSyncAsync(tenantId, connection.Id, order.OrderNumber, false, correlationId, cancellationToken);
        return ServiceResult<CreateOrderPackageResult>.Ok(result.Value!);
    }

    // A panel status for a Shopify order (Shopify has no shipping workflow of its own here). It is
    // written on the order version the operator saw, under the order jobs' lock, once per request
    // key, and it names the status the marketplace data gave at that moment: it stays until that
    // marketplace status changes (ShopifyManualStatusPolicy).
    public async Task<ServiceResult<ShopifyOrderStatusResult>> SetShopifyOrderStatusAsync(Guid tenantId, Guid userId, Guid orderId, long expectedVersion, string? status, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var normalizedStatus = status?.Trim().ToUpperInvariant();
        if (normalizedStatus is not ("NEW" or "PROCESSING" or "ON_HOLD" or "READY_TO_SHIP" or "SHIPPED" or "UNDELIVERED" or "DELIVERED" or "RETURN_IN_TRANSIT" or "RETURNED" or "CANCELLED"))
            return ServiceResult<ShopifyOrderStatusResult>.Fail("SHOPIFY_ORDER_STATUS_INVALID", "Shopify sipariş durumu geçersiz.", 422);
        var connectionId = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == orderId).Select(x => (Guid?)x.ConnectionId).SingleOrDefaultAsync(cancellationToken);
        if (connectionId is null) return NotFound<ShopifyOrderStatusResult>();
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform != "SHOPIFY") return ServiceResult<ShopifyOrderStatusResult>.Fail("SHOPIFY_ORDER_ONLY", "Bu manuel durum akışı yalnız Shopify siparişleri için kullanılabilir.", 422);

        await using var ordersLock = await OrdersProjectionLockAsync(connectionId.Value, cancellationToken);
        var order = await db.Orders.SingleAsync(x => x.TenantId == tenantId && x.Id == orderId, cancellationToken);
        var sourceEventId = ShopifyManualStatusPolicy.SourceEventId(RemoteDerivedStatus(await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId).ToListAsync(cancellationToken), await RemoteOrderStatusAsync(order, cancellationToken)), idempotencyKey);
        // The same request again (same key): it was applied; answer with the order as it is.
        var replay = await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId && x.SourceEventId.EndsWith(ShopifyManualStatusPolicy.KeySuffix(idempotencyKey)))
            .Select(x => x.CanonicalStatus).FirstOrDefaultAsync(cancellationToken);
        if (replay is not null) return ServiceResult<ShopifyOrderStatusResult>.Ok(new(order.Id, order.DerivedStatus, order.Version));
        if (order.Version != expectedVersion) return Precondition<ShopifyOrderStatusResult>(order.Version);

        var now = timeProvider.GetUtcNow();
        order.DerivedStatus = normalizedStatus;
        order.UpdatedAt = now;
        order.Version++;
        db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, CanonicalStatus = normalizedStatus, RawStatus = $"{ShopifyManualStatusPolicy.RawStatusPrefix}{normalizedStatus}", SourceEventId = sourceEventId, OccurredAt = now, RecordedAt = now });
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "SHOPIFY_ORDER_STATUS_MANUAL", TargetType = "Order", TargetId = orderId.ToString("D"), Reason = normalizedStatus, CorrelationId = correlationId, CreatedAt = now });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var latest = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == orderId).Select(x => x.Version).SingleAsync(cancellationToken);
            return Precondition<ShopifyOrderStatusResult>(latest);
        }
        return ServiceResult<ShopifyOrderStatusResult>.Ok(new(order.Id, normalizedStatus, order.Version));
    }

    // The order status the marketplace packages give (what the order would show without a manual status).
    private static string RemoteDerivedStatus(IReadOnlyCollection<ShipmentPackage> packages, string fallback) =>
        ShipmentPackageSupersession.StatusSources(packages) is { } sources ? Wire(ShipmentPackageStatusPolicy.Aggregate(sources)) : fallback;

    // The order status the marketplace gave, for an order without package statuses. While a panel
    // status holds, order.DerivedStatus is that panel status: the marketplace status is the one its
    // history row recorded, else the latest marketplace history status. Recording the old panel
    // status as the marketplace's let the next marketplace read drop the new panel status although
    // the marketplace had not changed (ShopifyManualStatusPolicy.StillHolds).
    private async Task<string> RemoteOrderStatusAsync(Order order, CancellationToken cancellationToken)
    {
        var manual = await db.OrderStatusHistory.AsNoTracking()
            .Where(x => x.TenantId == order.TenantId && x.OrderId == order.Id && x.RawStatus.StartsWith(ShopifyManualStatusPolicy.RawStatusPrefix)
                && x.CanonicalStatus == order.DerivedStatus && x.OccurredAt >= order.LastRemoteModifiedAt)
            .OrderByDescending(x => x.OccurredAt).Select(x => x.SourceEventId).FirstOrDefaultAsync(cancellationToken);
        if (manual is null) return order.DerivedStatus;
        return ShopifyManualStatusPolicy.RemoteStatusAt(manual)
            ?? await db.OrderStatusHistory.AsNoTracking()
                .Where(x => x.TenantId == order.TenantId && x.OrderId == order.Id && !x.RawStatus.StartsWith(ShopifyManualStatusPolicy.RawStatusPrefix))
                .OrderByDescending(x => x.OccurredAt).Select(x => x.CanonicalStatus).FirstOrDefaultAsync(cancellationToken)
            ?? order.DerivedStatus;
    }

    internal static string HepsiburadaPackageReservationSettledKey(string effectPrefix, Guid effectId) => $"{effectPrefix}:reconciled:{effectId:N}";

    internal sealed record PackageLineAllocationRow(Guid PackageId, Guid OrderLineId, string SourceEventId, decimal AllocatedQuantity, decimal CancelledQuantity);

    // An order read stores a package line's allocation once per source event; the line's current
    // allocation in that package is the latest of them, never their sum.
    internal static IReadOnlyList<PackageLineAllocationRow> LatestPackageLineAllocations(IEnumerable<PackageLineAllocationRow> rows) => rows
        .GroupBy(row => (row.PackageId, row.OrderLineId))
        .Select(group => group.OrderByDescending(row => SourceEventInstant(row.SourceEventId)).ThenByDescending(row => row.SourceEventId, StringComparer.Ordinal).First())
        .ToList();

    // Source events are "{packageId}:{unix ms}"; anything else sorts first.
    private static long SourceEventInstant(string sourceEventId)
    {
        var separator = sourceEventId.LastIndexOf(':');
        return separator >= 0 && long.TryParse(sourceEventId.AsSpan(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds) ? milliseconds : long.MinValue;
    }

    // Units of each line already in a live package of the order: cancelled and superseded
    // packages (split parents, resent originals) hold none.
    private async Task<Dictionary<Guid, decimal>> ActiveAllocatedQuantitiesAsync(Guid tenantId, Guid[] orderLineIds, CancellationToken cancellationToken)
    {
        if (orderLineIds.Length == 0) return [];
        var livePackages = CurrentShipmentPackages().Where(package => package.TenantId == tenantId && package.Status != ShipmentPackageStatus.Cancelled);
        var rows = await db.PackageLineAllocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.OrderLineId) && livePackages.Any(package => package.Id == x.PackageId))
            .Select(x => new PackageLineAllocationRow(x.PackageId, x.OrderLineId, x.SourceEventId, x.AllocatedQuantity, x.CancelledQuantity))
            .ToListAsync(cancellationToken);
        return LatestPackageLineAllocations(rows).GroupBy(row => row.OrderLineId)
            .ToDictionary(group => group.Key, group => group.Sum(row => Math.Max(0m, row.AllocatedQuantity - row.CancelledQuantity)));
    }

    // The lines of one package with their current allocated units, as a picking request sends them.
    private async Task<List<(string ExternalLineId, decimal AllocatedQuantity)>> PackageLinesAsync(Guid tenantId, Guid packageId, CancellationToken cancellationToken)
    {
        var rows = await db.PackageLineAllocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PackageId == packageId)
            .Select(x => new PackageLineAllocationRow(x.PackageId, x.OrderLineId, x.SourceEventId, x.AllocatedQuantity, x.CancelledQuantity))
            .ToListAsync(cancellationToken);
        var latest = LatestPackageLineAllocations(rows).Where(row => row.AllocatedQuantity > 0).ToList();
        var lineIds = latest.Select(row => row.OrderLineId).ToArray();
        var externalIds = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && lineIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.ExternalLineId, cancellationToken);
        return latest.Where(row => externalIds.ContainsKey(row.OrderLineId))
            .OrderBy(row => externalIds[row.OrderLineId], StringComparer.Ordinal)
            .Select(row => (externalIds[row.OrderLineId], row.AllocatedQuantity)).ToList();
    }

    public async Task<ServiceResult<Guid>> EnqueueOrderSyncAsync(Guid tenantId, Guid connectionId, string? externalOrderId, bool full, string correlationId, CancellationToken cancellationToken, string? packageNumber = null)
    {
        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        var normalizedPackageNumber = packageNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedPackageNumber)
            && (platform is not ("TRENDYOL" or "HEPSIBURADA") || string.IsNullOrWhiteSpace(externalOrderId) || normalizedPackageNumber.Length > 100 || normalizedPackageNumber.Any(char.IsControl)))
            return ServiceResult<Guid>.Fail("ORDER_PACKAGE_LOOKUP_INVALID", "Paket numarasıyla yenileme yalnızca tek bir Trendyol veya Hepsiburada siparişi için kullanılabilir.", 422);
        var type = MarketplaceJobTypes.ForPlatform(platform, full ? MarketplaceJobTypes.OrderRecoverySync : MarketplaceJobTypes.OrderSync);
        return await EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.OrderRead, type, JsonSerializer.Serialize(new { connectionId, externalOrderId, full, packageNumber = normalizedPackageNumber }), correlationId, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueProductSyncAsync(Guid tenantId, Guid connectionId, bool full, bool newOnly, bool existingOnly, bool mappingOnly, bool optionsOnly, bool includeArchived, bool includeDrafts, bool includePendingApproval, bool updateExistingProducts, bool updateExistingPricesAndStock, string? productLookup, string correlationId, CancellationToken cancellationToken)
    {
        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (optionsOnly && !string.Equals(platform, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
            return ServiceResult<Guid>.Fail("PRODUCT_OPTIONS_REPAIR_UNSUPPORTED", "Varyant seçeneklerini tek başına yenileme şu anda yalnızca Trendyol bağlantılarında kullanılabilir.", 422);
        if (optionsOnly)
        {
            full = true;
            newOnly = false;
            existingOnly = true;
            mappingOnly = false;
            includeArchived = true;
            includeDrafts = false;
            includePendingApproval = true;
            updateExistingProducts = false;
            updateExistingPricesAndStock = false;
            productLookup = null;
        }
        includePendingApproval = includePendingApproval
            && (full || newOnly || mappingOnly || optionsOnly)
            && (!existingOnly || optionsOnly)
            && string.IsNullOrWhiteSpace(productLookup);
        // Mapping is deliberately exclusive: it may create links only, never
        // local products or imported product content, even if an older client
        // sends overlapping mode flags.
        if (mappingOnly)
        {
            full = false;
            newOnly = false;
            existingOnly = false;
            // Mapping is a repair pass over the whole existing remote catalog.
            // Do not let a presentation-only archive toggle leave old records
            // out of the matching scope.
            includeArchived = true;
            includeDrafts = true;
            updateExistingProducts = false;
            updateExistingPricesAndStock = false;
            optionsOnly = false;
        }
        updateExistingPricesAndStock = updateExistingPricesAndStock && (full || existingOnly) && !newOnly && !mappingOnly && !optionsOnly && !string.Equals(platform, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase) && !string.Equals(platform, "N11", StringComparison.OrdinalIgnoreCase) && !string.Equals(platform, "PAZARAMA", StringComparison.OrdinalIgnoreCase) && !string.Equals(platform, "PTTAVM", StringComparison.OrdinalIgnoreCase);
        var type = MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.ProductSync);
        return await EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ProductRead, type, JsonSerializer.Serialize(new { connectionId, full, newOnly, existingOnly, mappingOnly, optionsOnly, includeArchived, includeDrafts, includePendingApproval, updateExistingProducts, updateExistingPricesAndStock, productLookup }), correlationId, cancellationToken);
    }

    public async Task<ServiceResult<CatalogStructureRunView>> EnqueueCatalogStructureAsync(Guid tenantId, Guid connectionId, string mode, string correlationId, CancellationToken cancellationToken)
    {
        var normalizedMode = mode?.Trim().ToUpperInvariant();
        if (normalizedMode is not ("PREVIEW" or "APPLY"))
            return ServiceResult<CatalogStructureRunView>.Fail("CATALOG_STRUCTURE_MODE_INVALID", "mode PREVIEW veya APPLY olmalıdır.", 422);
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform is not ("TRENDYOL" or "HEPSIBURADA"))
            return ServiceResult<CatalogStructureRunView>.Fail("CATALOG_STRUCTURE_UNSUPPORTED", "Platformdan yapı çekme şu anda yalnızca Trendyol ve Hepsiburada bağlantılarında kullanılabilir.", 422);
        if (await db.CatalogStructureRuns.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.Status == "QUEUED" || x.Status == "RUNNING"), cancellationToken))
            return ServiceResult<CatalogStructureRunView>.Fail("CATALOG_STRUCTURE_ALREADY_RUNNING", "Bu bağlantıda yapı çekme işlemi zaten çalışıyor.", 409);
        if (normalizedMode == "APPLY")
        {
            // Applying is only allowed after a fresh, completed preview of the same connection.
            var threshold = timeProvider.GetUtcNow().AddHours(-24);
            var hasPreview = await db.CatalogStructureRuns.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Mode == "PREVIEW" && x.Status == "COMPLETED" && x.CompletedAt >= threshold, cancellationToken);
            if (!hasPreview)
                return ServiceResult<CatalogStructureRunView>.Fail("CATALOG_STRUCTURE_PREVIEW_REQUIRED", "Uygulamadan önce son 24 saat içinde tamamlanmış bir önizleme gerekir.", 409);
        }

        var run = new CatalogStructureRun { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, Mode = normalizedMode, Status = "QUEUED", CreatedAt = timeProvider.GetUtcNow(), Version = 1 };
        db.CatalogStructureRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        var queued = await EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ProductRead, MarketplaceJobTypes.CatalogStructureImport, JsonSerializer.Serialize(new { connectionId, runId = run.Id, mode = normalizedMode }), correlationId, cancellationToken);
        if (!queued.Succeeded)
        {
            db.CatalogStructureRuns.Remove(run);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<CatalogStructureRunView>.Fail(queued.Error!.Code, queued.Error.Message, queued.Error.Status);
        }
        run.JobId = queued.Value;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<CatalogStructureRunView>.Ok(ToView(run, includeReport: false));
    }

    public async Task<ServiceResult<CatalogStructureRunView>> CatalogStructureRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.CatalogStructureRuns.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == runId, cancellationToken);
        return run is null ? NotFound<CatalogStructureRunView>() : ServiceResult<CatalogStructureRunView>.Ok(ToView(run, includeReport: true));
    }

    public async Task<IReadOnlyList<CatalogStructureRunView>> CatalogStructureRunsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var runs = await db.CatalogStructureRuns.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        return runs.Select(run => ToView(run, includeReport: false)).ToList();
    }

    private static CatalogStructureRunView ToView(CatalogStructureRun run, bool includeReport)
    {
        JsonElement? report = null;
        if (includeReport && !string.IsNullOrWhiteSpace(run.ReportJson))
        {
            using var document = JsonDocument.Parse(run.ReportJson);
            report = document.RootElement.Clone();
        }
        return new(run.Id, run.ConnectionId, run.JobId, run.Mode, run.Status, run.CreatedAt, run.CompletedAt, run.ErrorSummary, report);
    }

    public Task<ServiceResult<Guid>> EnqueueReferenceSyncAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken)
    {
        var normalized = resourceType.Trim().ToUpperInvariant();
        var parent = string.IsNullOrWhiteSpace(parentExternalId) ? null : parentExternalId.Trim();
        var valid = normalized switch
        {
            "CATEGORIES" or "BRANDS" => parent is null,
            "CATEGORY_ATTRIBUTES" => parent is not null && !parent.Contains('/', StringComparison.Ordinal),
            "ATTRIBUTE_VALUES" => parent?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 2,
            _ => false
        };
        return valid
            ? EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ReferenceRead, MarketplaceJobTypes.ReferenceSync, JsonSerializer.Serialize(new { connectionId, resourceType = normalized, parentExternalId = parent }), correlationId, cancellationToken)
            : Task.FromResult(ServiceResult<Guid>.Fail("REFERENCE_RESOURCE_UNSUPPORTED", "CATEGORIES/BRANDS scope almaz; CATEGORY_ATTRIBUTES categoryId, ATTRIBUTE_VALUES categoryId/attributeId scope ister.", 422));
    }

    public async Task<ServiceResult<Guid>> EnqueueShipmentActionAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken); if (package is null) return NotFound<Guid>(); if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        var action = command.Action.Trim().ToUpperInvariant();
        var commandPayload = command.PayloadJson;
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform == "PAZARAMA" && action is "PICKING" or "NOT_SUPPLIED" && IsEmptyJsonObject(commandPayload))
        {
            // Pazarama order items are GUIDs; the adapter sends one status request per item.
            var pazaramaLines = await PackageLinesAsync(tenantId, package.Id, cancellationToken);
            if (pazaramaLines.Count == 0)
                return ServiceResult<Guid>.Fail("PICKING_LINES_REQUIRED", "Pazarama işlemi için pakette sipariş kalemi bulunamadı.", 422);
            commandPayload = JsonSerializer.Serialize(new { lines = pazaramaLines.Select(line => new { lineId = line.ExternalLineId, quantity = (int)line.AllocatedQuantity }), status = action == "PICKING" ? "Picking" : "NotSupplied" });
        }
        if (platform != "PAZARAMA" && action == "PICKING" && IsEmptyJsonObject(commandPayload))
        {
            var packageLines = await PackageLinesAsync(tenantId, package.Id, cancellationToken);
            if (packageLines.Count == 0 || packageLines.Any(line => !long.TryParse(line.ExternalLineId, out _) || line.AllocatedQuantity != decimal.Truncate(line.AllocatedQuantity) || line.AllocatedQuantity > int.MaxValue))
                return ServiceResult<Guid>.Fail("PICKING_LINES_REQUIRED", "Paket satırları Trendyol Picking isteği için hazırlanamadı.", 422);
            commandPayload = JsonSerializer.Serialize(new { lines = packageLines.Select(line => new { lineId = long.Parse(line.ExternalLineId), quantity = (int)line.AllocatedQuantity }), @params = new { }, status = "Picking" });
        }
        var validation = platform == "HEPSIBURADA"
            ? ValidateHepsiburadaShipmentAction(package, action, commandPayload)
            : platform == "N11"
                ? ValidateN11ShipmentAction(package, action, commandPayload)
                : platform == "PAZARAMA"
                    ? ValidatePazaramaShipmentAction(package, action, commandPayload)
                    : ValidateShipmentAction(package, action, commandPayload);
        if (validation is not null) return ServiceResult<Guid>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Shipment işlemi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var writePolicy = await ExternalWritePolicyAsync(tenantId, package.ConnectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken);
        if (!writePolicy.Enabled) return ServiceResult<Guid>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);
        var normalizedKey = idempotencyKey.Trim();
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{action}\n{commandPayload}")));
        var dedup = $"shipment-action:{package.Id}:v{package.Version}:{action}:{commandHash}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ShipmentAction && x.EffectIdempotencyKey == normalizedKey, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var jobId = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow(); var payload = JsonSerializer.Serialize(new ShipmentActionJobPayload(jobId, package.Id, action, commandPayload)); var job = NewJob(tenantId, package.ConnectionId, MarketplaceJobTypes.ShipmentAction, dedup, payload, correlationId); job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.AvailableAt = now.AddSeconds(stage ? 0 : writePolicy.IntervalSeconds); job.CreatedAt = now; db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<ShipmentView>> ProcessShipmentInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        const string action = "PICKING";
        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null) return NotFound<ShipmentView>();
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform is not ("TRENDYOL" or "N11" or "PAZARAMA"))
            return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_UNSUPPORTED", "Bu pazaryerinde siparişler panelden anlık işleme alınamıyor.", 422);
        var effectType = $"{platform}_INSTANT_PICKING";
        var normalizedKey = idempotencyKey.Trim();
        // A resend of a request that already went through answers with the package as it is now,
        // before the version and state checks the first request itself moved on.
        var existing = await db.ExternalEffectRecords.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.TargetId is { } target && target != package.Id) return ServiceResult<ShipmentView>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Bu Idempotency-Key başka bir paket için kullanılmış.", 409);
            if (existing.CompletedAt is null) return Verifying<ShipmentView>();
            var replayOrderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.OrderNumber).SingleOrDefaultAsync(cancellationToken);
            return replayOrderNumber is null ? NotFound<ShipmentView>() : ServiceResult<ShipmentView>.Ok(Map(package, replayOrderNumber));
        }
        if (package.Version != expectedVersion) return Precondition<ShipmentView>(package.Version);
        if (package.Status != ShipmentPackageStatus.New) return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_NOT_ALLOWED", "Yalnız yeni durumdaki paketler işleme alınabilir.", 409);

        var packageLines = await PackageLinesAsync(tenantId, package.Id, cancellationToken);
        string payload;
        ServiceError? validation;
        if (platform == "PAZARAMA")
        {
            // Pazarama order items are GUIDs; the adapter sends one status request per item.
            if (packageLines.Count == 0 || packageLines.Any(line => string.IsNullOrWhiteSpace(line.ExternalLineId) || line.AllocatedQuantity != decimal.Truncate(line.AllocatedQuantity) || line.AllocatedQuantity > int.MaxValue))
                return ServiceResult<ShipmentView>.Fail("PICKING_LINES_REQUIRED", "Paket satırları Pazarama işleme alma isteği için hazırlanamadı.", 422);
            payload = JsonSerializer.Serialize(new { lines = packageLines.Select(line => new { lineId = line.ExternalLineId, quantity = (int)line.AllocatedQuantity }), status = "Picking" });
            validation = ValidatePazaramaShipmentAction(package, action, payload);
        }
        else
        {
            if (packageLines.Count == 0 || packageLines.Any(line => !long.TryParse(line.ExternalLineId, out _) || line.AllocatedQuantity != decimal.Truncate(line.AllocatedQuantity) || line.AllocatedQuantity > int.MaxValue))
                return ServiceResult<ShipmentView>.Fail("PICKING_LINES_REQUIRED", "Paket satırları işleme alma isteği için hazırlanamadı.", 422);
            payload = JsonSerializer.Serialize(new { lines = packageLines.Select(line => new { lineId = long.Parse(line.ExternalLineId), quantity = (int)line.AllocatedQuantity }), @params = new { }, status = "Picking" });
            validation = platform == "N11" ? ValidateN11ShipmentAction(package, action, payload) : ValidateShipmentAction(package, action, payload);
        }
        if (validation is not null) return ServiceResult<ShipmentView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("ENVIRONMENT_INVALID", "Shipment işlemi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (!await ExternalWritePolicyEnabledAsync(tenantId, package.ConnectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);
        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.OrderId, cancellationToken);
        if (order is null) return NotFound<ShipmentView>();
        var orderNumber = order.OrderNumber;

        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = normalizedKey, CreatedAt = timeProvider.GetUtcNow(), ConnectionId = package.ConnectionId, TargetId = package.Id, Action = "PICKING" };
        db.ExternalEffectRecords.Add(effect);
        await db.SaveChangesAsync(cancellationToken);
        AdapterResult<PackageActionResult> result;
        using (TrendyolQuotaLane.Instant()) result = await orders.ExecutePackageActionAsync(new AdapterContext(tenantId, package.ConnectionId, correlationId, normalizedKey, timeProvider.GetUtcNow().AddSeconds(30)), new(package.ExternalPackageId, action, payload, order.StoreFrontCode), cancellationToken);
        if (!result.IsSuccess)
        {
            var error = result.Error;
            if (error is null || IsAmbiguous(error)) return await VerifyLaterAsync<ShipmentView>(effect, cancellationToken);
            db.ExternalEffectRecords.Remove(effect);
            await db.SaveChangesAsync(cancellationToken);
            // The marketplace quota was full and nothing was sent: the action waits in the user lane instead.
            if (error.Class == AdapterErrorClass.RateLimit)
                return await QueueInsteadAsync<ShipmentView>(EnqueueShipmentActionAsync(tenantId, package.Id, package.Version, new ShipmentActionCommand(action, "{}"), normalizedKey, correlationId, cancellationToken));
            return ServiceResult<ShipmentView>.Fail("PICKING_FAILED", error.SafeMessage, error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502);
        }

        // The marketplace accepted the write: record that first and on its own,
        // so a conflict while projecting it locally can never leave the effect
        // open (and the same key answering "verifying" forever).
        var appliedAt = timeProvider.GetUtcNow();
        effect.CompletedAt = appliedAt;
        effect.Outcome = "APPLIED";
        await db.SaveChangesAsync(cancellationToken);

        // The local projection runs under the connection's "orders" lock, as the order jobs'
        // writes do (see OrdersProjectionLockAsync); the remote call above did not hold it.
        await using var projectionLock = await OrdersProjectionLockAsync(package.ConnectionId, cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            if (package.Status == ShipmentPackageStatus.New)
            {
                package.Status = ShipmentPackageStatus.Processing;
                package.RawStatus = "Processing";
                // StatusOccurredAt stays the marketplace's own event time: a
                // local clock here would make the marketplace's Picking event
                // (with its cargo and invoice fields) look older and be dropped.
                package.UpdatedAt = appliedAt;
                package.Version++;
                db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = "PROCESSING", RawStatus = PackageIngestionSafety.InstantPickingRawStatus, SourceEventId = $"instant-picking:{effect.Id:N}", OccurredAt = appliedAt, RecordedAt = appliedAt });
            }
            // Orders are filtered by DerivedStatus while the counters are calculated
            // from shipment packages. Keep both read models in sync before returning
            // so the order moves between tabs in the same response.
            var orderPackages = await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.OrderId == order.Id)
                .ToListAsync(cancellationToken);
            var current = orderPackages.Select(x => x.Id == package.Id ? package : x).ToList();
            if (ShipmentPackageSupersession.StatusSources(current) is { } statusSources)
            {
                var derivedStatus = Wire(ShipmentPackageStatusPolicy.Aggregate(statusSources));
                if (!string.Equals(order.DerivedStatus, derivedStatus, StringComparison.Ordinal))
                {
                    order.DerivedStatus = derivedStatus;
                    order.UpdatedAt = appliedAt;
                    order.Version++;
                }
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                // A sync read updated the package or order meanwhile; project
                // the accepted action once more on the fresh rows.
                db.ChangeTracker.Clear();
                var freshPackage = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
                var freshOrder = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.OrderId, cancellationToken);
                if (freshPackage is null || freshOrder is null) return NotFound<ShipmentView>();
                package = freshPackage;
                order = freshOrder;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The action is applied on the marketplace; a targeted order read
                // brings the package into the same state.
                db.ChangeTracker.Clear();
                await QueueProjectionReadAsync(tenantId, package.ConnectionId, orderNumber, correlationId, cancellationToken);
                var latest = await db.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
                return ServiceResult<ShipmentView>.Ok(Map(latest, orderNumber));
            }
        }
    }

    // How long an instant action waits for the order jobs' lock before projecting without it.
    // The remote write is already applied and recorded; holding the request longer only delays
    // the answer, and an optimistic projection (retried once, then left to a targeted order read)
    // is still safe.
    internal static readonly TimeSpan InstantProjectionLockWait = TimeSpan.FromSeconds(3);

    // The lock is taken around the local projection only, not around the marketplace call: an
    // order job may hold it for a whole page read, and the remote call itself may take up to
    // 30 seconds; waiting for both would hold either the user or the order lane far too long.
    private Task<MarketplaceSyncExecutionLock?> OrdersProjectionLockAsync(Guid connectionId, CancellationToken cancellationToken) =>
        MarketplaceSyncExecutionLock.AcquireGroupAsync(db, connectionId, MarketplaceSyncExecutionLock.OrdersGroup, InstantProjectionLockWait, timeProvider, cancellationToken);

    private async Task QueueProjectionReadAsync(Guid tenantId, Guid connectionId, string orderNumber, string correlationId, CancellationToken cancellationToken)
    {
        // Best effort: the scheduled order reads reach the same state later anyway.
        var queued = await EnqueueOrderSyncAsync(tenantId, connectionId, orderNumber, false, correlationId, cancellationToken);
        if (!queued.Succeeded) db.ChangeTracker.Clear();
    }

    public async Task<ServiceResult<ShipmentView>> ChangeCargoProviderInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        const string effectType = "TRENDYOL_INSTANT_CARGO_PROVIDER_CHANGE";
        var action = command.Action.Trim().ToUpperInvariant();
        if (action != "CHANGE_CARGO_PROVIDER") return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_UNSUPPORTED", "Bu anlık endpoint yalnız kargo firması değişikliği için kullanılabilir.", 422);

        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null) return NotFound<ShipmentView>();
        // Only Trendyol changes a package's carrier through this request; the other marketplaces
        // have their own flow (Hepsiburada) or none (audit 2026-10-20 S17).
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform != "TRENDYOL") return ServiceResult<ShipmentView>.Fail("CARGO_PROVIDER_CHANGE_UNSUPPORTED", "Kargo firması değişikliği panelden yalnız Trendyol paketlerinde yapılabilir.", 422);

        var normalizedKey = idempotencyKey.Trim();
        // A resend of a change that went through answers with the carrier that change asked for
        // (its recorded expected value), before the version check the change itself moved on.
        var existing = await db.ExternalEffectRecords.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.TargetId is { } target && target != package.Id) return ServiceResult<ShipmentView>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Bu Idempotency-Key başka bir paket için kullanılmış.", 409);
            if (existing.CompletedAt is null) return Verifying<ShipmentView>();
            var replayOrderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.OrderNumber).SingleOrDefaultAsync(cancellationToken);
            if (replayOrderNumber is null) return NotFound<ShipmentView>();
            // A change closed as unresolved was never confirmed (Q-2): the package is shown as it is, not with that carrier.
            return string.IsNullOrWhiteSpace(existing.ExpectedValue) || existing.Outcome == ExternalEffectReconcilePolicy.UnresolvedOutcome
                ? ServiceResult<ShipmentView>.Ok(Map(package, replayOrderNumber))
                : await ProjectCargoProviderAsync(tenantId, package, existing.ExpectedValue, replayOrderNumber, correlationId, cancellationToken);
        }
        if (package.Version != expectedVersion) return Precondition<ShipmentView>(package.Version);

        var validation = ValidateShipmentAction(package, action, command.PayloadJson);
        if (validation is not null) return ServiceResult<ShipmentView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);

        string cargoProvider;
        try
        {
            using var document = JsonDocument.Parse(command.PayloadJson);
            cargoProvider = document.RootElement.GetProperty("cargoProvider").GetString()?.Trim() ?? string.Empty;
        }
        catch (JsonException)
        {
            return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_PAYLOAD_INVALID", "Kargo firması bilgisi geçerli JSON olmalıdır.", 422);
        }

        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("ENVIRONMENT_INVALID", "Shipment işlemi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (!await ExternalWritePolicyEnabledAsync(tenantId, package.ConnectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);

        var orderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.OrderNumber).SingleOrDefaultAsync(cancellationToken);
        if (orderNumber is null) return NotFound<ShipmentView>();

        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = normalizedKey, CreatedAt = timeProvider.GetUtcNow(), ConnectionId = package.ConnectionId, TargetId = package.Id, Action = "CHANGE_CARGO_PROVIDER", ExpectedValue = cargoProvider };
        db.ExternalEffectRecords.Add(effect);
        await db.SaveChangesAsync(cancellationToken);

        AdapterResult<PackageActionResult> result;
        using (TrendyolQuotaLane.Instant())
            result = await orders.ExecutePackageActionAsync(
                new AdapterContext(tenantId, package.ConnectionId, correlationId, normalizedKey, timeProvider.GetUtcNow().AddSeconds(30)),
                new(package.ExternalPackageId, action, command.PayloadJson, await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.StoreFrontCode).SingleOrDefaultAsync(cancellationToken)),
                cancellationToken);
        if (!result.IsSuccess)
        {
            var error = result.Error;
            if (error is null || IsAmbiguous(error)) return await VerifyLaterAsync<ShipmentView>(effect, cancellationToken);

            db.ExternalEffectRecords.Remove(effect);
            await db.SaveChangesAsync(cancellationToken);
            if (error.Class == AdapterErrorClass.RateLimit)
                return await QueueInsteadAsync<ShipmentView>(EnqueueShipmentActionAsync(tenantId, package.Id, package.Version, command, normalizedKey, correlationId, cancellationToken));
            var status = error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502;
            return ServiceResult<ShipmentView>.Fail("CARGO_PROVIDER_CHANGE_FAILED", error.SafeMessage, status);
        }

        // Record the accepted write on its own first (see ProcessShipmentInstantAsync).
        effect.CompletedAt = timeProvider.GetUtcNow();
        effect.Outcome = "APPLIED";
        await db.SaveChangesAsync(cancellationToken);
        return await ProjectCargoProviderAsync(tenantId, package, cargoProvider, orderNumber, correlationId, cancellationToken);
    }

    // Writes an accepted cargo provider change onto the package under the order jobs' lock
    // (see OrdersProjectionLockAsync): retried once on fresh rows, then left to a targeted order read.
    private async Task<ServiceResult<ShipmentView>> ProjectCargoProviderAsync(Guid tenantId, ShipmentPackage package, string cargoProvider, string orderNumber, string correlationId, CancellationToken cancellationToken)
    {
        if (string.Equals(package.CargoProviderExternalId, cargoProvider, StringComparison.OrdinalIgnoreCase))
            return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
        var packageId = package.Id;
        var connectionId = package.ConnectionId;
        await using var projectionLock = await OrdersProjectionLockAsync(connectionId, cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            if (!string.Equals(package.CargoProviderExternalId, cargoProvider, StringComparison.OrdinalIgnoreCase))
            {
                package.CargoProviderExternalId = cargoProvider;
                package.UpdatedAt = timeProvider.GetUtcNow();
                package.Version++;
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
                var fresh = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
                if (fresh is null) return NotFound<ShipmentView>();
                package = fresh;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                await QueueProjectionReadAsync(tenantId, connectionId, orderNumber, correlationId, cancellationToken);
                var latest = await db.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
                return ServiceResult<ShipmentView>.Ok(Map(latest, orderNumber));
            }
        }
    }

    public async Task<ServiceResult<Guid>> EnqueueCommonLabelAsync(Guid tenantId, Guid packageId, long expectedVersion, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken, string format = "ZPL")
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken); if (package is null) return NotFound<Guid>(); if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var hepsiburada = platform == "HEPSIBURADA";
        var requestedFormat = hepsiburada ? (format ?? string.Empty).Trim().ToUpperInvariant() : "ZPL";
        if (hepsiburada && !HepsiburadaLabelFormats.Contains(requestedFormat, StringComparer.OrdinalIgnoreCase))
            return Invalid<Guid>("format", "Hepsiburada etiket biçimi ZPL, BASE64ZPL, PDF, PNG veya JPG olmalıdır.");
        if (string.IsNullOrWhiteSpace(package.ExternalPackageId) || !hepsiburada && string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return ServiceResult<Guid>.Fail("CARGO_TRACKING_REQUIRED", "Etiket için paket veya kargo takip numarası gerekir.", 422);
        if (hepsiburada ? !CommonLabelCarrierPolicy.SupportsHepsiburada(package.CargoProviderExternalId) : !CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return ServiceResult<Guid>.Fail("COMMON_LABEL_CARRIER_UNSUPPORTED", hepsiburada ? "Hepsiburada ortak etiket yalnız HepsiJet ve Aras paketlerinde desteklenir." : "Ortak etiket yalnız Trendyol öder Aras Kargo veya TEX gönderilerinde kullanılabilir.", 422);
        if (boxQuantity is < 1 or > 50) return Invalid<Guid>("boxQuantity", "boxQuantity 1-50 arasında olmalıdır.");
        if (volumetricHeight < 0 || volumetricHeight > 10000) return Invalid<Guid>("volumetricHeight", "volumetricHeight 0-10000 arasında olmalıdır.");
        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Ortak etiket yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (hepsiburada)
        {
            var supportedFormats = stage
                ? HepsiburadaLabelFormats
                : await CapabilityValues(tenantId, package.ConnectionId, MarketplaceCapabilities.LabelRead, "formats", cancellationToken);
            if (!supportedFormats.Contains(requestedFormat, StringComparer.OrdinalIgnoreCase))
                return ServiceResult<Guid>.Fail("LABEL_READ_CAPABILITY_REQUIRED", $"Production Hepsiburada etiket okuması için doğrulanmış LABEL_READ/{requestedFormat} kanıtı gerekir.", 422);
        }
        if (!hepsiburada)
        {
            if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
            if (!await ExternalWritePolicyEnabledAsync(tenantId, package.ConnectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);
        }
        var normalizedKey = idempotencyKey.Trim(); var existingAttempt = await db.ShipmentDocumentAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (existingAttempt is not null)
        {
            var existingJob = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.CommonLabel && x.EffectIdempotencyKey == normalizedKey, cancellationToken);
            return existingJob is null ? ServiceResult<Guid>.Fail("LABEL_ATTEMPT_STATE_CONFLICT", "Etiket denemesi var ancak job kaydı bulunamadı.", 409) : ServiceResult<Guid>.Ok(existingJob.Id);
        }
        var now = timeProvider.GetUtcNow(); var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new CommonLabelJobPayload(jobId, package.Id, "SUBMIT", boxQuantity, decimal.Round(volumetricHeight, 2, MidpointRounding.ToEven), now, now.AddMinutes(15), requestedFormat));
        var job = NewJob(tenantId, package.ConnectionId, MarketplaceJobTypes.CommonLabel, $"common-label:{package.Id}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}", payload, correlationId); job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.MaxAttempts = 30;
        db.ShipmentDocumentAttempts.Add(new ShipmentDocumentAttempt { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = package.Id, IdempotencyKey = normalizedKey, Status = "PENDING", CreatedAt = now }); db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<Guid>> EnqueueLabelCapabilityProbeAsync(Guid tenantId, Guid actorUserId, Guid packageId, long expectedVersion, string capabilityCode, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null) return NotFound<Guid>();
        if (package.Version != expectedVersion) return Precondition<Guid>(package.Version);
        if (string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return ServiceResult<Guid>.Fail("CARGO_TRACKING_REQUIRED", "Stage etiket capability testi için takip numarası gerekir.", 422);
        var capability = capabilityCode.Trim().ToUpperInvariant();
        if (capability is not (MarketplaceCapabilities.LabelRead or MarketplaceCapabilities.LabelWrite)) return Invalid<Guid>("capabilityCode", "Bu Stage canary yalnız LABEL_READ veya LABEL_WRITE için kullanılabilir.");
        if (boxQuantity is < 1 or > 50 || volumetricHeight <= 0 || volumetricHeight > 10000) return Invalid<Guid>("probe", "Koli adedi 1-50, desi/hacim 0-10000 arasında olmalıdır.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.ConnectionId && x.PlatformCode == "TRENDYOL", cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)) return ServiceResult<Guid>.Fail("STAGE_CONNECTION_REQUIRED", "Capability canary yalnız Trendyol STAGE bağlantısında çalışır.", 422);
        if (capability == MarketplaceCapabilities.LabelWrite && package.Status.ToString() is not ("ReadyToShip" or "Processing")) return ServiceResult<Guid>.Fail("STAGE_LABEL_PACKAGE_NOT_READY", "LABEL_WRITE canary yalnız Picking/Processing veya ReadyToShip Stage paketi üzerinde çalışır.", 422);
        if (capability == MarketplaceCapabilities.LabelWrite && !CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return ServiceResult<Guid>.Fail("COMMON_LABEL_CARRIER_UNSUPPORTED", "LABEL_WRITE canary yalnız Trendyol öder Aras Kargo veya TEX Stage paketi üzerinde çalışır.", 422);
        var normalizedKey = idempotencyKey.Trim();
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.CapabilityProbe && x.EffectIdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var now = timeProvider.GetUtcNow(); var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new CapabilityProbeJobPayload(jobId, package.Id, actorUserId, capability, boxQuantity, decimal.Round(volumetricHeight, 2, MidpointRounding.ToEven), now, now.AddMinutes(15)));
        var job = NewJob(tenantId, package.ConnectionId, MarketplaceJobTypes.CapabilityProbe, $"stage-capability-probe:{package.Id}:{capability}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}", payload, correlationId);
        job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.MaxAttempts = 1; db.IntegrationJobs.Add(job);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = actorUserId, Action = "CAPABILITY_STAGE_PROBE_ENQUEUED", TargetType = "PlatformCapability", TargetId = package.ConnectionId.ToString("D"), Reason = $"{capability}:package:{package.Id:D}", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<ServiceResult<Guid>> EnqueueStageTestOrderAsync(Guid tenantId, Guid actorUserId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA"), cancellationToken);
        if (connection is null) return NotFound<Guid>();
        if (!string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)
            || connection.PlatformCode == "TRENDYOL" && !string.Equals(connection.ExternalStoreId, "2738", StringComparison.Ordinal))
            return ServiceResult<Guid>.Fail("STAGE_TEST_ORDER_SCOPE_REQUIRED", "Taze test siparişi yalnız desteklenen Stage bağlantılarında oluşturulabilir.", 422);
        var barcode = "9900000000486";
        string? hepsiburadaSku = null;
        if (connection.PlatformCode == "HEPSIBURADA")
        {
            var liveBarcode = await (from listing in db.ChannelListingVariants.AsNoTracking()
                                     join profile in db.ChannelListingProfiles.AsNoTracking() on new { listing.TenantId, ProfileId = listing.ProfileId } equals new { profile.TenantId, ProfileId = profile.Id }
                                     join state in db.MarketplaceListingStates.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { state.TenantId, state.ConnectionId, state.VariantId }
                                     join link in db.MarketplaceVariantLinks.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { link.TenantId, link.ConnectionId, link.VariantId }
                                     where listing.TenantId == tenantId && profile.ConnectionId == connectionId && profile.Enabled && state.ActualStatus == "LIVE"
                                           && listing.ExternalBarcode != null && !string.IsNullOrWhiteSpace(link.ExternalId)
                                     orderby listing.VariantId
                                     select listing.ExternalBarcode!).FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(liveBarcode)) barcode = liveBarcode;
            else
            {
                // SIT fallback (this method only reaches here for a STAGE connection): the products Hepsiburada
                // loads into the SIT account are imported, not published from the panel, so they have no LIVE
                // listing. Use an imported link whose external id is a real hbSku and whose offer is ACTIVE.
                var importedExternalIds = await (from link in db.MarketplaceVariantLinks.AsNoTracking()
                                                 join offer in db.ChannelOffers.AsNoTracking() on new { link.TenantId, link.ConnectionId, link.VariantId } equals new { offer.TenantId, offer.ConnectionId, offer.VariantId }
                                                 where link.TenantId == tenantId && link.ConnectionId == connectionId && offer.Status == "ACTIVE"
                                                 orderby link.VariantId
                                                 select link.ExternalId).ToListAsync(cancellationToken);
                hepsiburadaSku = HepsiburadaStageCertificationPolicy.FirstHepsiburadaSku(importedExternalIds);
                if (hepsiburadaSku is null) return ServiceResult<Guid>.Fail("HEPSIBURADA_STAGE_LISTING_REQUIRED", "Test siparişi için Stage ortamında bağlı Hepsiburada SKU’su bulunan LIVE bir ürün veya hbSku ile içe aktarılmış ACTIVE bir teklif gerekir.", 409);
                barcode = hepsiburadaSku;
            }
        }
        var normalizedKey = idempotencyKey.Trim();
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.StageTestOrder && x.EffectIdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var now = timeProvider.GetUtcNow(); var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new StageTestOrderJobPayload(jobId, actorUserId, barcode, now, hepsiburadaSku));
        var job = NewJob(tenantId, connectionId, MarketplaceJobTypes.StageTestOrder, $"stage-test-order:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}", payload, correlationId);
        job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.MaxAttempts = 1; db.IntegrationJobs.Add(job);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = actorUserId, Action = "STAGE_TEST_ORDER_ENQUEUED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = $"official-stage-test-order-fixture:{connection.PlatformCode}", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(jobId);
    }

    // The Returns page reads cards (a claim with accepted and waiting lines is two cards), filtered,
    // counted and paged by the server (MarketplaceSalesService.ReturnList.cs). "latest" keeps the
    // plain claim list by last change, unsplit and unpaged, for tools and tests.
    public async Task<PageResult<ReturnListView>> ReturnsAsync(Guid tenantId, int limit, string? after, ReturnListQuery options, bool latest, CancellationToken cancellationToken, int page = 1)
    {
        if (!await HasOperationalReturnConnectionAsync(tenantId, cancellationToken)) return new([], null, false);
        if (!latest) return await ReturnCardPageAsync(tenantId, limit, page, after, options, cancellationToken);

        var query = ReturnBaseQuery(tenantId);
        ApplyReturnFilters(ref query, options);
        var totalCount = await query.CountAsync(cancellationToken);
        var claims = await query.OrderByDescending(x => x.LastRemoteModifiedAt).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var rows = await ReturnViewsAsync(tenantId, claims, cancellationToken);
        return new(rows.Take(limit).ToList(), null, rows.Count > limit, totalCount);
    }

    private Task<bool> HasOperationalReturnConnectionAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId
                && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM")
                && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);

    private IQueryable<ReturnClaim> ReturnBaseQuery(Guid tenantId) =>
        OrderQueueQueries.ListedReturns(db, tenantId, timeProvider.GetUtcNow());

    // One list view per claim, in the claims' order.
    private async Task<List<ReturnListView>> ReturnViewsAsync(Guid tenantId, List<ReturnClaim> claims, CancellationToken cancellationToken)
    {
        var orderIds = claims.Select(x => x.OrderId).Distinct().ToArray();
        var claimIds = claims.Select(x => x.Id).ToArray();
        var approvedAtByClaim = claimIds.Length == 0
            ? new Dictionary<Guid, DateTimeOffset?>()
            : await db.ReturnDecisions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && claimIds.Contains(x.ClaimId) && x.Action == "APPROVE" && x.Status == "SUCCEEDED")
                .GroupBy(x => x.ClaimId)
                .Select(group => new { ClaimId = group.Key, ApprovedAt = group.Max(decision => decision.CompletedAt) })
                .ToDictionaryAsync(x => x.ClaimId, x => x.ApprovedAt, cancellationToken);
        var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var lineGrossByOrder = await ActiveLineGrossAsync(tenantId, orders.Values.Where(x => x.GrossAmount <= 0).Select(x => x.Id).ToArray(), cancellationToken);
        decimal RefundRatio(Order order) => ReturnRefundAmounts.DiscountRatio(order.GrossAmount, order.DiscountAmount, order.NetAmount, lineGrossByOrder.GetValueOrDefault(order.Id));
        var connectionIds = claims.Select(x => x.ConnectionId).Concat(orders.Values.Select(x => x.ConnectionId)).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var returnLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && claimIds.Contains(x.ClaimId)).ToListAsync(cancellationToken);
        var orderLineIds = returnLines.Select(x => x.OrderLineId).Distinct().ToArray();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var returnLineSkus = orderLines.Values.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLineSkuKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLineBarcodes = orderLines.Values.Select(x => x.Barcode).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLineBarcodeKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnVariantLinks = returnLineSkuKeys.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId
                    && connectionIds.Contains(x.ConnectionId)
                    && (returnLineSkuKeys.Contains(x.ExternalId.Trim().ToUpper()) || x.ExternalSku != null && returnLineSkuKeys.Contains(x.ExternalSku.Trim().ToUpper())))
                .ToListAsync(cancellationToken);
        var linkedReturnVariantIds = returnVariantLinks.Select(x => x.VariantId).Distinct().ToArray();
        var returnVariantIds = orderLines.Values.Where(x => x.VariantId.HasValue).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var returnVariants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId
                && (returnVariantIds.Contains(x.Id)
                    || linkedReturnVariantIds.Contains(x.Id)
                    || returnLineSkus.Contains(x.Sku)
                    || returnLineSkuKeys.Contains(x.SkuNormalized)
                    || returnLineBarcodes.Contains(x.Barcode)
                    || returnLineBarcodeKeys.Contains(x.BarcodeNormalized)))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var returnVariantsById = returnVariants.ToDictionary(x => x.Id);
        var returnVariantsBySku = returnVariants.GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var returnVariantsByNormalizedSku = returnVariants.GroupBy(x => x.SkuNormalized, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var returnVariantsByBarcode = BarcodeVariantLookup(returnVariants);
        var returnVariantsByMarketplaceIdentity = returnVariantLinks
            .SelectMany(link => LinkIdentities(link).Select(identity => (Key: (link.ConnectionId, ExternalId: identity), link.VariantId)))
            .GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.First().VariantId);
        var returnImageUrls = await MediaUrls(tenantId, returnVariants.Select(x => (Guid?)x.Id), cancellationToken);
        ProductVariant? ResolveReturnVariant(OrderLine line)
        {
            if (line.VariantId is { } variantId && returnVariantsById.TryGetValue(variantId, out var direct)) return direct;
            if (returnVariantsBySku.TryGetValue(line.Sku, out var bySku)) return bySku;
            foreach (var key in CatalogLookupKeys(line.Sku, 160))
                if (returnVariantsByNormalizedSku.TryGetValue(key, out var byNormalizedSku)) return byNormalizedSku;
            foreach (var key in CatalogLookupKeys(line.Barcode, 160))
                if (returnVariantsByBarcode.TryGetValue(key, out var byBarcode)) return byBarcode;
            var connectionId = orders.GetValueOrDefault(line.OrderId)?.ConnectionId;
            var identity = (connectionId ?? Guid.Empty, ExternalId: MarketplaceVariantLinkCoverage.Normalize(line.Sku));
            return returnVariantsByMarketplaceIdentity.TryGetValue(identity, out var linkedId)
                ? returnVariantsById.GetValueOrDefault(linkedId)
                : null;
        }
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).OrderByDescending(x => x.StatusOccurredAt).ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId) && x.OriginalInvoiceId == null
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var invoiceIds = invoices.Select(invoice => invoice.Id).ToArray();
        var returnDeliveryStates = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(delivery => delivery.TenantId == tenantId && invoiceIds.Contains(delivery.InvoiceId))
                .Select(delivery => new { delivery.InvoiceId, delivery.PackageId, delivery.Status, delivery.UpdatedAt })
                .ToListAsync(cancellationToken);
        var returnDeliveryAttempts = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveries.AsNoTracking()
                .Where(delivery => delivery.TenantId == tenantId && invoiceIds.Contains(delivery.InvoiceId))
                .Select(delivery => new { delivery.InvoiceId, delivery.PackageId, delivery.Status })
                .ToListAsync(cancellationToken);
        var rows = claims.Select(claim =>
        {
            var order = orders.GetValueOrDefault(claim.OrderId);
            var connection = order is null ? connections.GetValueOrDefault(claim.ConnectionId) : connections.GetValueOrDefault(order.ConnectionId);
            var claimConnection = connections.GetValueOrDefault(claim.ConnectionId);
            var platformCode = claimConnection?.PlatformCode ?? connection?.PlatformCode ?? "TRENDYOL";
            var claimLines = returnLines.Where(x => x.ClaimId == claim.Id).ToList();
            var package = packages.FirstOrDefault(x => x.OrderId == claim.OrderId);
            var outboundPackage = packages.FirstOrDefault(x => x.OrderId == claim.OrderId && !string.IsNullOrWhiteSpace(x.CargoTrackingNumber)) ?? package;
            var invoice = invoices.FirstOrDefault(x => x.OrderId == claim.OrderId);
            var packageDeliveryConfirmed = invoice is not null && package is not null
                && MarketplaceInvoiceDeliveryEvidencePolicy.HasConfirmedDelivery(
                    returnDeliveryStates.Where(state => state.InvoiceId == invoice.Id && state.PackageId == package.Id)
                        .OrderByDescending(state => state.UpdatedAt).Select(state => state.Status).FirstOrDefault(),
                    returnDeliveryAttempts.Any(attempt => attempt.InvoiceId == invoice.Id && attempt.PackageId == package.Id && attempt.Status == "CONFIRMED"));
            DateTimeOffset? approvedAt = claim.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                ? approvedAtByClaim.GetValueOrDefault(claim.Id) ?? claim.LastRemoteModifiedAt
                : null;
            var firstLine = claimLines.Select(x => orderLines.GetValueOrDefault(x.OrderLineId)).FirstOrDefault(x => x is not null);
            var firstVariant = firstLine is null ? null : ResolveReturnVariant(firstLine);
            var image = firstVariant is null ? null : returnImageUrls.GetValueOrDefault(firstVariant.Id);
            var lineViews = claimLines.Select(returnLine =>
            {
                var line = orderLines.GetValueOrDefault(returnLine.OrderLineId);
                if (line is null) return null;
                var source = SourceLine(line.SourceSnapshotJson);
                var variant = ResolveReturnVariant(line);
                return new OrderLineView(line.Id, line.Sku, line.Barcode ?? variant?.Barcode, line.TitleSnapshot, returnLine.Quantity, line.CancelledQuantity, line.ShippedQuantity, line.DeliveredQuantity, line.ReturnedQuantity, line.UnitPrice, line.VatRate, line.RawStatus, line.VariantId ?? variant?.Id, ReturnLineModelCode(variant, source.ModelCode), variant?.OptionSignature ?? source.OptionSignature, source.ImageUrl ?? (variant is null ? null : returnImageUrls.GetValueOrDefault(variant.Id)),
                    RefundAmount: order is null ? null : ReturnRefundAmounts.LineRefund(returnLine.Quantity, line.UnitPrice, RefundRatio(order)));
            }).Where(line => line is not null).Select(line => line!).ToList();
            return new ReturnListView(claim.Id, claim.ExternalClaimId, order?.OrderNumber ?? "—", Wire(claim.Status), claim.RawStatus, claim.ReasonText, ReturnActionDueAt(platformCode, claim.RawStatus, claim.ActionDueAt, claim.LastRemoteModifiedAt), claim.Version,
                order is null ? "—" : Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).Name,
                order?.OrderedAt, order?.NetAmount ?? 0, order?.Currency ?? "TRY", claim.CargoProviderName, claim.CargoTrackingNumber, image, claimLines.Count, firstLine?.Barcode ?? firstVariant?.Barcode,
                lineViews, package?.ExternalPackageId, order is null ? "FATURA_BEKLIYOR" : ReturnInvoiceLabel(invoice, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, order.CustomerSnapshotJson, package is null ? [] : [package.RawStatus], packageDeliveryConfirmed), order?.GrossAmount ?? 0, order?.DiscountAmount ?? 0,
                order is not null && Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).IsMicroExport,
                claimConnection?.Id ?? connection?.Id, platformCode, claimConnection?.DisplayName ?? connection?.DisplayName ?? "Trendyol", outboundPackage?.CargoProviderExternalId, outboundPackage?.CargoTrackingNumber, claim.ReasonCode, approvedAt, claim.CargoTrackingLink,
                claimLines.Where(x => !string.IsNullOrWhiteSpace(x.RawStatus)).GroupBy(x => x.OrderLineId).ToDictionary(x => x.Key, x => Wire(MarketplaceReturnStatus.Canonicalize(x.First().RawStatus))),
                IsEstimatedReturnDueAt(platformCode, claim),
                ReturnRefundAmounts.ClaimTotal(lineViews.Select(line => line.RefundAmount)));
        }).ToList();
        return rows;
    }

    internal static string? ReturnLineModelCode(ProductVariant? variant, string? sourceModelCode) =>
        string.IsNullOrWhiteSpace(variant?.ModelCode) ? sourceModelCode : variant.ModelCode;

    // The deadline is an estimate when the marketplace sent none: stored as
    // derived, or derived here from the status.
    internal static bool IsEstimatedReturnDueAt(string? platformCode, ReturnClaim claim) =>
        claim.ActionDueAt is null
            ? ReturnActionDueAt(platformCode, claim.RawStatus, null, claim.LastRemoteModifiedAt) is not null
            : claim.ActionDueAtEstimated;

    internal static DateTimeOffset? ReturnActionDueAt(string? platformCode, string? rawStatus, DateTimeOffset? actionDueAt, DateTimeOffset lastRemoteModifiedAt)
    {
        if (actionDueAt is not null) return actionDueAt;
        var isTrendyolWaitingInAction = string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(rawStatus?.Trim(), "WaitingInAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawStatus?.Trim(), "WAITING_IN_ACTION", StringComparison.OrdinalIgnoreCase));
        if (isTrendyolWaitingInAction) return lastRemoteModifiedAt.AddHours(48);
        var isTrendyolRejected = string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(rawStatus?.Trim(), "Rejected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawStatus?.Trim(), "REJECTED", StringComparison.OrdinalIgnoreCase));
        return isTrendyolRejected ? lastRemoteModifiedAt.AddDays(7) : null;
    }

    private void ApplyReturnFilters(ref IQueryable<ReturnClaim> query, ReturnListQuery options)
    {
        var status = options.Status?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(status) && status != "ALL")
        {
            query = status switch
            {
                "REQUESTED" => query.Where(x => x.Status == ReturnClaimStatus.Requested),
                "SHIPPING" => query.Where(x => x.Status == ReturnClaimStatus.AwaitingShipment || x.Status == ReturnClaimStatus.InTransit),
                "ACTION_REQUIRED" => query.Where(x => x.Status == ReturnClaimStatus.ActionRequired),
                "APPROVED" => query.Where(x => x.Status == ReturnClaimStatus.Approved || x.Status == ReturnClaimStatus.Completed
                    || (x.Status == ReturnClaimStatus.ActionRequired && db.ReturnLines.Any(line => line.TenantId == x.TenantId && line.ClaimId == x.Id && line.RawStatus != null && line.RawStatus.ToUpper() == "ACCEPTED"))),
                "REJECTED" => query.Where(x => x.Status == ReturnClaimStatus.Rejected || x.Status == ReturnClaimStatus.Cancelled),
                "DISPUTED" => query.Where(x => x.Status == ReturnClaimStatus.Disputed),
                // The UI's Analysis tab is reserved for explicitly submitted
                // analysis cases. ActionRequired claims belong only to their
                // own operational tab and must not leak into Analysis.
                "REVIEW" => query.Where(_ => false),
                "SUSPENDED" => query.Where(_ => false),
                _ => query.Where(_ => false)
            };
        }

        var search = options.Search?.Trim();
        var searchCustomer = search is not null && SearchesCustomerSnapshot(search);
        var customerPattern = CustomerSnapshotSearchPattern(search ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // The page's own search ignores case the Turkish way; this database pre-filter must
            // never be narrower than it.
            // The reason and the product title are folded the Turkish way on both sides (TurkishSearchText).
            var (asTyped, folded) = SearchPatterns(search);
            var reasonCodeMatches = db.ReturnClaims.Where(TurkishSearchText.FoldedILike<ReturnClaim>(claim => claim.ReasonCode, folded, LikeEscape));
            var reasonTextMatches = db.ReturnClaims.Where(TurkishSearchText.FoldedILike<ReturnClaim>(claim => claim.ReasonText, folded, LikeEscape));
            var titleMatches = db.OrderLines.Where(TurkishSearchText.FoldedILike<OrderLine>(line => line.TitleSnapshot, folded, LikeEscape));
            query = query.Where(claim => EF.Functions.ILike(claim.ExternalClaimId, asTyped, LikeEscape)
                || reasonCodeMatches.Any(match => match.TenantId == claim.TenantId && match.Id == claim.Id)
                || reasonTextMatches.Any(match => match.TenantId == claim.TenantId && match.Id == claim.Id)
                || db.Orders.Any(order => order.TenantId == claim.TenantId && order.Id == claim.OrderId
                    && (EF.Functions.ILike(order.OrderNumber, asTyped, LikeEscape) || (searchCustomer && Regex.IsMatch(order.CustomerSnapshotJson, customerPattern, RegexOptions.IgnoreCase))))
                || db.ReturnLines.Any(returnLine => returnLine.TenantId == claim.TenantId && returnLine.ClaimId == claim.Id
                    && (titleMatches.Any(line => line.TenantId == returnLine.TenantId && line.Id == returnLine.OrderLineId)
                        || db.OrderLines.Any(line => line.TenantId == returnLine.TenantId && line.Id == returnLine.OrderLineId
                            && ((line.Barcode != null && EF.Functions.ILike(line.Barcode, asTyped, LikeEscape)) || EF.Functions.ILike(line.Sku, asTyped, LikeEscape))))));
        }

        var customer = options.Customer?.Trim();
        if (!string.IsNullOrWhiteSpace(customer))
        {
            var customerFilterPattern = CustomerSnapshotSearchPattern(customer);
            var customerNumber = SearchPatterns(customer).AsTyped;
            query = query.Where(x => db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && (Regex.IsMatch(order.CustomerSnapshotJson, customerFilterPattern, RegexOptions.IgnoreCase) || EF.Functions.ILike(order.OrderNumber, customerNumber, LikeEscape))));
        }
        var orderNumber = options.OrderNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(orderNumber))
            query = query.Where(x => db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && order.OrderNumber.Contains(orderNumber)));
        var claimCode = options.ClaimCode?.Trim();
        if (!string.IsNullOrWhiteSpace(claimCode)) query = query.Where(x => x.ExternalClaimId.Contains(claimCode));
        var barcode = options.Barcode?.Trim();
        if (!string.IsNullOrWhiteSpace(barcode))
            query = query.Where(x => db.ReturnLines.Any(returnLine => returnLine.TenantId == x.TenantId && returnLine.ClaimId == x.Id && db.OrderLines.Any(line => line.TenantId == x.TenantId && line.Id == returnLine.OrderLineId && ((line.Barcode != null && line.Barcode.Contains(barcode)) || line.Sku.Contains(barcode)))));
        var reason = options.Reason?.Trim();
        if (!string.IsNullOrWhiteSpace(reason)) query = query.Where(x => x.ReasonCode == reason || (x.ReasonText != null && x.ReasonText.Contains(reason)));
        if (options.DateFrom?.ToUniversalTime() is { } dateFrom) query = query.Where(x => db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && order.OrderedAt >= dateFrom));
        if (options.DateTo?.ToUniversalTime() is { } dateTo) query = query.Where(x => db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && order.OrderedAt <= dateTo));
    }

    public async Task<ServiceResult<ReturnDetailView>> ReturnAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claim.OrderId, cancellationToken);
        if (order is null) return NotFound<ReturnDetailView>();
        var stageConnection = await IsStageConnection(tenantId, claim.ConnectionId, cancellationToken);
        var externalWritesEnabled = stageConnection || await WritesEnabled(tenantId, claim.ConnectionId, cancellationToken);
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var awaitingPreApproval = isHepsiburada && HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(claim.RawStatus);
        // ActionRequired is the provider's decision point. Supported return
        // adapters expose explicit APPROVE/REJECT implementations, so the
        // UI must not hide those controls just because a production capability
        // evidence row was not recorded. The write policy is still enforced by
        // ProcessReturnActionInstantAsync enforces the policy before any external
        // request is made. Return decisions are intentionally not queued.
        var openDecisionLines = await OpenReturnDecisions(tenantId, claim.Id, isHepsiburada).Select(x => x.ReturnLineIds).ToListAsync(cancellationToken);
        var sourceLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        bool HeldByDecision(Guid lineId) => openDecisionLines.Any(lineIds => ReturnDecisionLinePolicy.Overlaps(lineIds, [lineId]));
        // The claim waits on a sent decision only while every line still open for a decision is covered by one:
        // a decision on some lines leaves the others free for their own.
        var decisionPending = openDecisionLines.Count > 0
            && sourceLines.Where(line => isHepsiburada || ReturnLineStatusPolicy.IsActionable(line.RawStatus)).All(line => HeldByDecision(line.Id));
        var actions = claim.Status switch
        {
            ReturnClaimStatus.Requested when awaitingPreApproval => HepsiburadaReturnActionPolicy.AllowedActions(claim.RawStatus, decisionPending),
            ReturnClaimStatus.Requested or ReturnClaimStatus.InTransit => ["RECEIVE"],
            ReturnClaimStatus.AwaitingShipment when isHepsiburada => Array.Empty<string>(),
            ReturnClaimStatus.ActionRequired when isHepsiburada => HepsiburadaReturnActionPolicy.AllowedActions(claim.RawStatus, decisionPending),
            // n11 approvals go through the API; denials need n11's own panel.
            ReturnClaimStatus.ActionRequired when !decisionPending && string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase) => ["APPROVE"],
            ReturnClaimStatus.ActionRequired when !decisionPending => ReturnActions,
            ReturnClaimStatus.ActionRequired => Array.Empty<string>(),
            _ => await CapabilityValues(tenantId, claim.ConnectionId, MarketplaceCapabilities.ReturnWrite, "allowedActions", cancellationToken)
        };
        var approvedAt = claim.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
            ? await db.ReturnDecisions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id && x.Action == "APPROVE" && x.Status == "SUCCEEDED")
                .OrderByDescending(x => x.CompletedAt)
                .Select(x => x.CompletedAt)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        approvedAt ??= claim.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed ? claim.LastRemoteModifiedAt : null;
        var orderLineIds = sourceLines.Select(x => x.OrderLineId).ToArray();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var dispositions = await db.ReturnStockDispositions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == id).GroupBy(x => x.ReturnLineId).Select(x => new { ReturnLineId = x.Key, Quantity = x.Sum(y => y.Quantity) }).ToDictionaryAsync(x => x.ReturnLineId, x => x.Quantity, cancellationToken);
        var lineSkuKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lineBarcodeKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var variantLinks = lineSkuKeys.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == claim.ConnectionId
                    && (lineSkuKeys.Contains(x.ExternalId.Trim().ToUpper()) || x.ExternalSku != null && lineSkuKeys.Contains(x.ExternalSku.Trim().ToUpper())))
                .ToListAsync(cancellationToken);
        var candidateVariantIds = orderLines.Values.Where(x => x.VariantId.HasValue).Select(x => x.VariantId!.Value)
            .Concat(variantLinks.Select(x => x.VariantId)).Distinct().ToArray();
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && (candidateVariantIds.Contains(x.Id)
                    || lineSkuKeys.Contains(x.SkuNormalized)
                    || x.BarcodeNormalized != null && lineBarcodeKeys.Contains(x.BarcodeNormalized)))
            .ToListAsync(cancellationToken);
        var variantsById = variants.ToDictionary(x => x.Id);
        var variantsBySku = variants.GroupBy(x => x.SkuNormalized, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var variantsByBarcode = BarcodeVariantLookup(variants);
        var variantsByExternalSku = variantLinks
            .SelectMany(link => LinkIdentities(link).Select(identity => (Identity: identity, link.VariantId)))
            .GroupBy(x => x.Identity, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().VariantId, StringComparer.Ordinal);
        ProductVariant? ResolveReturnDetailVariant(OrderLine line)
        {
            if (line.VariantId is { } directId && variantsById.TryGetValue(directId, out var direct)) return direct;
            foreach (var key in CatalogLookupKeys(line.Sku, 160))
                if (variantsBySku.TryGetValue(key, out var bySku)) return bySku;
            foreach (var key in CatalogLookupKeys(line.Barcode, 160))
                if (variantsByBarcode.TryGetValue(key, out var byBarcode)) return byBarcode;
            var normalizedSku = MarketplaceVariantLinkCoverage.Normalize(line.Sku);
            return variantsByExternalSku.TryGetValue(normalizedSku, out var linkedId) ? variantsById.GetValueOrDefault(linkedId) : null;
        }
        var resolvedVariants = orderLines.Values.Select(ResolveReturnDetailVariant).Where(x => x is not null).Select(x => x!).ToArray();
        var variantIds = resolvedVariants.Select(x => x.Id).Distinct().ToArray();
        var inventoryVariants = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId) && x.LocationCode == "MAIN").Select(x => x.VariantId).ToListAsync(cancellationToken);
        var imageUrls = await MediaUrls(tenantId, variantIds.Select(x => (Guid?)x), cancellationToken);
        var refundRatio = ReturnRefundAmounts.DiscountRatio(order.GrossAmount, order.DiscountAmount, order.NetAmount,
            order.GrossAmount > 0 ? 0 : (await ActiveLineGrossAsync(tenantId, [order.Id], cancellationToken)).GetValueOrDefault(order.Id));
        var lines = sourceLines.Select(line =>
        {
            var source = orderLines.GetValueOrDefault(line.OrderLineId);
            var sourceSnapshot = source is null ? null : SourceLine(source.SourceSnapshotJson);
            var variant = source is null ? null : ResolveReturnDetailVariant(source);
            var disposed = dispositions.GetValueOrDefault(line.Id);
            var imageUrl = sourceSnapshot?.ImageUrl ?? (variant is null ? null : imageUrls.GetValueOrDefault(variant.Id));
            if (imageUrl is null && !string.IsNullOrWhiteSpace(source?.Barcode))
                imageUrl = $"/api/v1/orders/product-image?barcode={Uri.EscapeDataString(source.Barcode)}";
            return new ReturnLineView(line.Id, line.ExternalLineId, line.OrderLineId, source?.Sku ?? "—", source?.Barcode, source?.TitleSnapshot ?? "—", line.Quantity, disposed, Math.Max(0, line.Quantity - disposed), source?.UnitPrice ?? 0,
                imageUrl,
                variant is not null && inventoryVariants.Contains(variant.Id),
                variant?.OptionSignature ?? sourceSnapshot?.OptionSignature,
                variant?.ModelCode ?? sourceSnapshot?.ModelCode,
                string.IsNullOrWhiteSpace(line.RawStatus) ? null : Wire(MarketplaceReturnStatus.Canonicalize(line.RawStatus)),
                source is null ? null : ReturnRefundAmounts.LineRefund(line.Quantity, source.UnitPrice, refundRatio),
                line.AcceptDetail, line.MarketplaceNote, line.Resolved, line.AcceptedBySeller,
                HeldByDecision(line.Id), ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, line.RawStatus));
        }).ToList();
        var stockDispositionAvailable = sourceLines.Count == 0
            ? ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, null)
            : lines.Any(line => line.StockDispositionAvailable);
        var package = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderByDescending(x => x.StatusOccurredAt).FirstOrDefaultAsync(cancellationToken);
        var customer = Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
        return ServiceResult<ReturnDetailView>.Ok(new(claim.Id, claim.ExternalClaimId, order.OrderNumber, Wire(claim.Status), claim.RawStatus, claim.ReasonCode, claim.ReasonText, ReturnActionDueAt(platformCode, claim.RawStatus, claim.ActionDueAt, claim.LastRemoteModifiedAt), actions, claim.Version,
            customer.Name, order.OrderedAt, order.NetAmount, order.Currency, claim.CargoProviderName, claim.CargoTrackingNumber, lines, stockDispositionAvailable, approvedAt, externalWritesEnabled, decisionPending, platformCode, claim.CargoTrackingLink, IsEstimatedReturnDueAt(platformCode, claim),
            ReturnRefundAmounts.ClaimTotal(lines.Select(line => line.RefundAmount))));
    }

    // The gross of each order's active lines (quantity × unit price): the refund's discount share
    // falls back on it when an order carries no gross total.
    private async Task<Dictionary<Guid, decimal>> ActiveLineGrossAsync(Guid tenantId, Guid[] orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Length == 0) return [];
        var rows = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId))
            .Select(x => new { x.OrderId, x.OrderedQuantity, x.CancelledQuantity, x.UnitPrice }).ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.OrderId).ToDictionary(group => group.Key, group => group.Sum(x => Math.Max(0, x.OrderedQuantity - x.CancelledQuantity) * x.UnitPrice));
    }

    // Each marketplace says per rejection reason whether evidence is needed;
    // the list changes rarely, so it is read at most once an hour per store.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset ReadAt, IReadOnlyList<ReturnIssueReason> Reasons)> IssueReasonCache = new();
    private static readonly TimeSpan IssueReasonCacheLifetime = TimeSpan.FromHours(1);

    private async Task<IReadOnlyList<ReturnIssueReason>?> CachedIssueReasonsAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (IssueReasonCache.TryGetValue(connectionId, out var cached) && now - cached.ReadAt < IssueReasonCacheLifetime) return cached.Reasons;
        var result = await returns.IssueReasonsAsync(new AdapterContext(tenantId, connectionId, correlationId, $"return-issue-reasons:{connectionId:N}", now.AddSeconds(30)), cancellationToken);
        if (!result.IsSuccess || result.Value is null) return null;
        IssueReasonCache[connectionId] = (now, result.Value);
        return result.Value;
    }

    internal static bool RejectEvidenceRequired(string? reasonCode, IReadOnlyList<ReturnIssueReason>? reasons, string? platformCode)
    {
        var code = reasonCode?.Trim();
        if (reasons?.FirstOrDefault(reason => string.Equals(reason.Id, code, StringComparison.Ordinal)) is { } reason) return reason.EvidenceRequired;
        // The list could not be read (or does not name the reason): Trendyol's
        // documented no-evidence reasons stay optional, everything else needs evidence.
        return !(string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase) && code is "1651" or "451" or "2101");
    }

    public async Task<ServiceResult<IReadOnlyList<ReturnIssueReason>>> ReturnIssueReasonsAsync(Guid tenantId, Guid id, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<IReadOnlyList<ReturnIssueReason>>();
        var context = new AdapterContext(tenantId, claim.ConnectionId, correlationId, $"return-issue-reasons:{claim.ConnectionId:N}", timeProvider.GetUtcNow().AddSeconds(30));
        var result = await returns.IssueReasonsAsync(context, cancellationToken);
        return result.IsSuccess
            ? ServiceResult<IReadOnlyList<ReturnIssueReason>>.Ok(result.Value!)
            : ServiceResult<IReadOnlyList<ReturnIssueReason>>.Fail(result.Error!.Code, result.Error.SafeMessage, result.Error.HttpStatus ?? 502);
    }

    public Task<ServiceResult<Guid>> EnqueueReturnSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ReturnRead, MarketplaceJobTypes.ReturnSync, JsonSerializer.Serialize(new { connectionId, forceFull = true }), correlationId, cancellationToken);

    public Task<ServiceResult<Guid>> EnqueueQuestionSyncAsync(Guid tenantId, Guid connectionId, string? kind, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.QuestionRead, MarketplaceJobTypes.QuestionSync, JsonSerializer.Serialize(new { connectionId, kind }), correlationId, cancellationToken);

    public async Task<ServiceResult<ReturnDetailView>> MarkReturnReceivedAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        if (claim.Version != expectedVersion) return Precondition<ReturnDetailView>(claim.Version);
        if (claim.Status is not (ReturnClaimStatus.Requested or ReturnClaimStatus.InTransit)) return ServiceResult<ReturnDetailView>.Fail("RETURN_RECEIPT_NOT_ALLOWED", "Teslim alındı işlemi yalnız talep oluşturulan veya kargodaki iadeler için kullanılabilir.", 409);
        if (!ReturnClaimStateMachine.CanTransition(claim.Status, ReturnClaimStatus.ActionRequired)) return ServiceResult<ReturnDetailView>.Fail("RETURN_STATE_CONFLICT", "İade durumu aksiyon bekliyor durumuna geçirilemedi.", 409);

        var now = timeProvider.GetUtcNow();
        claim.Status = ReturnClaimStatus.ActionRequired;
        claim.RawStatus = "RECEIVED_BY_SELLER";
        claim.UpdatedAt = now;
        claim.Version++;
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "RETURN_RECEIVED_BY_SELLER",
            TargetType = "ReturnClaim",
            TargetId = claimId.ToString("D"),
            Reason = "Kargo fiziksel olarak teslim alındı; iade inceleme aksiyonları açıldı.",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return await ReturnAsync(tenantId, claimId, cancellationToken);
    }

    public async Task<ServiceResult<ReturnDetailView>> ProcessReturnActionInstantAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, ReturnDecisionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var normalizedKey = idempotencyKey.Trim();
        var prior = await db.ReturnDecisions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (prior is not null)
        {
            if (prior.ClaimId != claimId) return ServiceResult<ReturnDetailView>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Bu Idempotency-Key başka bir iade için kullanılmış.", 409);
            if (prior.Status is "SUCCEEDED" or "SUBMITTED") return await ReturnAsync(tenantId, claimId, cancellationToken);
            if (prior.Status is "PENDING" or "MANUAL_REVIEW") return ServiceResult<ReturnDetailView>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade kararının sonucu henüz kesinleşmedi.", 409);
            return ServiceResult<ReturnDetailView>.Fail(prior.ErrorCode ?? "RETURN_ACTION_FAILED", "Bu Idempotency-Key ile başlatılan iade kararı başarısız oldu; yeni bir anahtar ile tekrar deneyin.", 409);
        }

        var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleAsync(cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var effectType = isHepsiburada ? "HEPSIBURADA_INSTANT_RETURN_ACTION" : "TRENDYOL_INSTANT_RETURN_ACTION";
        if (claim.Version != expectedVersion) return Precondition<ReturnDetailView>(claim.Version);
        var action = command.Action.Trim().ToUpperInvariant();
        var preApprovalConfirm = action == "PREAPPROVAL_CONFIRM";
        var awaitingPreApproval = isHepsiburada && HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(claim.RawStatus);
        if (action is not ("APPROVE" or "REJECT") && !preApprovalConfirm) return Invalid<ReturnDetailView>("action", "İade aksiyonu APPROVE, PREAPPROVAL_CONFIRM veya REJECT olmalıdır.");
        if (action == "REJECT" && string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase)) return ServiceResult<ReturnDetailView>.Fail("N11_RETURN_REJECT_MANUAL", "N11 iadeleri panelden yalnız onaylanabilir; reddetmek için N11 satıcı panelini kullanın.", 422);
        if (preApprovalConfirm && !awaitingPreApproval) return ServiceResult<ReturnDetailView>.Fail("HEPSIBURADA_PREAPPROVAL_ACTION_NOT_ALLOWED", "İnceleme için geri gönderim yalnız Hepsiburada AwaitingPreApproval durumunda kullanılabilir.", 409);
        if (claim.Status != ReturnClaimStatus.ActionRequired && !(awaitingPreApproval && claim.Status == ReturnClaimStatus.Requested)) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "İade aksiyonu yalnız sağlayıcının aksiyon beklediği durumda kullanılabilir.", 409);

        var claimLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claimId).Select(x => new { x.Id, x.RawStatus }).ToListAsync(cancellationToken);
        var claimLineIds = claimLines.Select(x => x.Id).ToList();
        if (claimLineIds.Count == 0) return Invalid<ReturnDetailView>("returnLineIds", "İade işleminde en az bir ürün satırı bulunmalıdır.");
        // Lines the marketplace already settled (for example one accepted on Trendyol) are not sent again.
        var actionableLineIds = isHepsiburada ? claimLineIds : claimLines.Where(x => ReturnLineStatusPolicy.IsActionable(x.RawStatus)).Select(x => x.Id).ToList();
        if (actionableLineIds.Count == 0) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "Bu iadenin işlem bekleyen ürün satırı kalmadı.", 409);
        // Only an open decision on the same lines holds this one back; the claim's other lines take their own decision.
        var openDecisionLines = await OpenReturnDecisions(tenantId, claimId, isHepsiburada).Select(x => x.ReturnLineIds).ToListAsync(cancellationToken);
        // Without a selection the decision takes the waiting lines that no open decision covers yet.
        var freeLineIds = isHepsiburada ? actionableLineIds : actionableLineIds.Where(lineId => !openDecisionLines.Any(lineIds => ReturnDecisionLinePolicy.Overlaps(lineIds, [lineId]))).ToList();
        var returnLineIds = command.ReturnLineIds?.Distinct().ToArray() ?? (freeLineIds.Count > 0 ? freeLineIds : actionableLineIds).ToArray();
        if (returnLineIds.Length == 0 || returnLineIds.Except(claimLineIds).Any()) return Invalid<ReturnDetailView>("returnLineIds", "İşlem yapılacak ürün satırları bu iadeye ait olmalıdır.");
        returnLineIds = returnLineIds.Intersect(actionableLineIds).ToArray();
        if (returnLineIds.Length == 0) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "Seçilen ürün satırları pazaryerinde zaten sonuçlanmış.", 409);
        if (isHepsiburada && returnLineIds.Length != claimLineIds.Count) return Invalid<ReturnDetailView>("returnLineIds", "Hepsiburada talep kabul/red işlemi talebin tüm ürün satırlarına uygulanır; kısmi satır seçimi desteklenmez.");
        var partialLineDecision = returnLineIds.Length < actionableLineIds.Count;
        var finalizedWith = command.FinalizedWith?.Trim();
        if (isHepsiburada && action == "APPROVE" && finalizedWith is not ("Refund" or "Change")) return Invalid<ReturnDetailView>("finalizedWith", "Hepsiburada talep kabulünde iade veya ürün değişimi seçilmelidir.");
        if ((!isHepsiburada || action != "APPROVE") && !string.IsNullOrWhiteSpace(finalizedWith)) return Invalid<ReturnDetailView>("finalizedWith", "Kabul türü yalnız Hepsiburada talebini kabul ederken gönderilebilir.");
        if (isHepsiburada && action == "APPROVE" && string.Equals(claim.ReasonCode, "MissingInvoice", StringComparison.OrdinalIgnoreCase)) return ServiceResult<ReturnDetailView>.Fail("HEPSIBURADA_MISSING_INVOICE_MANUAL", "Eksik fatura taleplerinin kabul işlemi Hepsiburada panelinden yapılmalıdır.", 409);
        if (openDecisionLines.Any(lineIds => ReturnDecisionLinePolicy.Overlaps(lineIds, returnLineIds))) return ServiceResult<ReturnDetailView>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade kararı pazaryerine gönderildi; sonuç kesinleşene kadar yeni bir karar gönderilemez.", 409);
        if (action == "REJECT" && (string.IsNullOrWhiteSpace(command.ReasonCode) || string.IsNullOrWhiteSpace(command.Explanation) || command.Explanation.Trim().Length > 500)) return Invalid<ReturnDetailView>("explanation", "REJECT için reasonCode ve en fazla 500 karakter açıklama gerekir.");
        if (action == "REJECT" && !isHepsiburada && (command.EvidenceAssetIds is null || command.EvidenceAssetIds.Count == 0)
            && RejectEvidenceRequired(command.ReasonCode, await CachedIssueReasonsAsync(tenantId, claim.ConnectionId, correlationId, cancellationToken), platformCode))
            return Invalid<ReturnDetailView>("evidenceAssetIds", "Seçilen ret nedeni için en az bir kanıt dosyası gerekir.");
        if (preApprovalConfirm && (!string.IsNullOrWhiteSpace(command.FinalizedWith) || !string.IsNullOrWhiteSpace(command.ReasonCode) || !string.IsNullOrWhiteSpace(command.Explanation) || command.EvidenceAssetIds is { Count: > 0 })) return Invalid<ReturnDetailView>("action", "Hepsiburada ön onay incelemesi ek kabul, ret veya kanıt alanı almaz.");

        var stage = await IsStageConnection(tenantId, claim.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("ENVIRONMENT_INVALID", "İade aksiyonu yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (isHepsiburada && !stage && !await Supported(tenantId, claim.ConnectionId, MarketplaceCapabilities.ReturnWrite, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("CAPABILITY_EVIDENCE_REQUIRED", "Hepsiburada talep yazması için aynı mağaza ve ortamda doğrulanmış SIT yetenek kanıtı gerekir.", 422);
        if (!await ExternalWritePolicyEnabledAsync(tenantId, claim.ConnectionId, MarketplaceExternalWritePolicies.Return, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "İade dış yazma akışı kapalı.", 422);

        // "Stoğa ekle": every approved line must already map to a local variant with a MAIN inventory item,
        // otherwise nothing is sent to the marketplace and nothing is half-done.
        var stockTargets = new List<(ReturnLine Line, InventoryItem Item)>();
        // Stock is chosen per product; the older all-or-nothing flag still means every approved line.
        var stockPlan = new Dictionary<Guid, decimal?>();
        foreach (var requested in command.StockLines ?? [])
        {
            if (requested.Quantity <= 0) return Invalid<ReturnDetailView>("stockLines", "Stoğa eklenecek adet sıfırdan büyük olmalıdır.");
            stockPlan[requested.ReturnLineId] = requested.Quantity;
        }
        foreach (var wholeLineId in command.StockLineIds ?? (command.AddToStock ? returnLineIds : []))
            stockPlan.TryAdd(wholeLineId, null);
        var stockLineIds = stockPlan.Keys.ToArray();
        if (stockLineIds.Length > 0)
        {
            if (action != "APPROVE") return Invalid<ReturnDetailView>("addToStock", "Stoğa ekleme yalnız iade kabulünde seçilebilir.");
            if (stockLineIds.Except(returnLineIds).Any()) return Invalid<ReturnDetailView>("stockLineIds", "Stoğa eklenecek ürünler onaylanan ürünler arasında olmalıdır.");
            var approvedLines = await db.ReturnLines.Where(x => x.TenantId == tenantId && x.ClaimId == claimId && stockLineIds.Contains(x.Id)).ToListAsync(cancellationToken);
            foreach (var approvedLine in approvedLines)
            {
                var variantId = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == approvedLine.OrderLineId).Select(x => x.VariantId).SingleOrDefaultAsync(cancellationToken);
                var inventoryItem = variantId is null ? null : await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", cancellationToken);
                if (inventoryItem is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "Seçilen ürünlerden biri yerel varyant ve stok kaydıyla eşleşmiyor; stoğa eklenemez.", 422);
                if (stockPlan[approvedLine.Id] is { } wantedUnits && wantedUnits > approvedLine.Quantity) return Invalid<ReturnDetailView>("stockLines", "Stoğa eklenecek adet iade satırı adedini aşamaz.");
                stockTargets.Add((approvedLine, inventoryItem));
            }
        }

        var now = timeProvider.GetUtcNow();
        var decision = new ReturnDecision
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ClaimId = claimId,
            Action = action,
            ReasonCode = isHepsiburada && action == "APPROVE" ? finalizedWith : string.IsNullOrWhiteSpace(command.ReasonCode) ? null : command.ReasonCode.Trim(),
            Explanation = string.IsNullOrWhiteSpace(command.Explanation) ? null : command.Explanation.Trim(),
            IdempotencyKey = normalizedKey,
            Status = "PENDING",
            ActorUserId = userId,
            CreatedAt = now,
            ReturnLineIds = returnLineIds
        };
        db.ReturnDecisions.Add(decision);

        var evidenceFiles = new List<ReturnEvidenceFile>(); long totalBytes = 0;
        if (command.EvidenceAssetIds is not null) foreach (var assetId in command.EvidenceAssetIds.Distinct())
        {
            var asset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == assetId && x.ArchivedAt == null && x.Status == "ACTIVE", cancellationToken);
            if (asset is null) return ServiceResult<ReturnDetailView>.Fail("EVIDENCE_NOT_FOUND", "İade kanıt dosyası tenant private storage içinde bulunamadı.", 422);
            if (asset.Classification != "RETURN_EVIDENCE" || asset.SizeBytes is <= 0 or > 10 * 1024 * 1024 || totalBytes + asset.SizeBytes > 25 * 1024 * 1024 || asset.MimeType is not ("application/pdf" or "image/jpeg" or "image/png")) return ServiceResult<ReturnDetailView>.Fail("EVIDENCE_INVALID", "İade kanıtları PDF/JPEG/PNG olmalı, dosya başına 10 MiB ve toplamda 25 MiB sınırını aşmamalıdır.", 422);
            await using var source = await files.OpenReadAsync(tenantId, asset.RelativePath, cancellationToken);
            await using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length != asset.SizeBytes) return ServiceResult<ReturnDetailView>.Fail("RETURN_EVIDENCE_SIZE_MISMATCH", "İade kanıt dosyasının kayıtlı boyutu ile okunan içerik eşleşmedi.", 422);
            var bytes = buffer.ToArray();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase)) return ServiceResult<ReturnDetailView>.Fail("RETURN_EVIDENCE_CHECKSUM_MISMATCH", "İade kanıt dosyasının checksum doğrulaması başarısız oldu.", 422);
            db.ReturnEvidence.Add(new ReturnEvidence { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, DecisionId = decision.Id, FileAssetId = asset.Id, EvidenceKind = asset.Classification, Checksum = asset.Sha256, CreatedAt = now });
            evidenceFiles.Add(new(asset.OriginalNameSafe ?? $"evidence-{asset.Id:N}", asset.MimeType, bytes));
            totalBytes += bytes.LongLength;
        }

        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = normalizedKey, CreatedAt = now, ConnectionId = claim.ConnectionId, TargetId = claim.Id, Action = $"RETURN_{action}" };
        db.ExternalEffectRecords.Add(effect);
        await db.SaveChangesAsync(cancellationToken);

        var lineIds = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claimId && returnLineIds.Contains(x.Id)).OrderBy(x => x.Id).Select(x => x.ExternalLineId).ToListAsync(cancellationToken);
        var context = new AdapterContext(tenantId, claim.ConnectionId, correlationId, normalizedKey, now.AddSeconds(30));
        var result = await returns.ExecuteAsync(context, new(claim.ExternalClaimId, lineIds, action, action == "APPROVE" && isHepsiburada ? null : decision.ReasonCode, decision.Explanation, evidenceFiles, isHepsiburada ? finalizedWith : null), cancellationToken);
        if (!result.IsSuccess)
        {
            var error = result.Error;
            decision.ErrorCode = error?.Code ?? "RETURN_ACTION_FAILED";
            decision.ExternalOperationId ??= error?.RemoteRequestId;
            decision.CompletedAt = timeProvider.GetUtcNow();
            if (error is null || IsAmbiguous(error))
            {
                // The outcome is settled by the effect check or the next claim
                // read; the code marks it as waiting for that, not as a conflict.
                decision.Status = "MANUAL_REVIEW";
                decision.ErrorCode = "EXTERNAL_EFFECT_VERIFYING";
                await db.SaveChangesAsync(cancellationToken);
                return await VerifyLaterAsync<ReturnDetailView>(effect, cancellationToken);
            }
            db.ExternalEffectRecords.Remove(effect);
            decision.Status = "FAILED";
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<ReturnDetailView>.Fail(error.Code, error.SafeMessage, error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502);
        }

        decision.Status = "SUBMITTED";
        decision.ExternalOperationId = result.Value?.ExternalOperationId;
        decision.ErrorCode = null;
        effect.CompletedAt = timeProvider.GetUtcNow();
        claim.Status = ReturnClaimDecisionPolicy.StatusAfterLineDecision(claim.Status, action, returnLineIds.Length, actionableLineIds.Count);
        if (!isHepsiburada && action is "APPROVE" or "REJECT")
        {
            var settledStatus = action == "APPROVE" ? "Accepted" : "Rejected";
            foreach (var settled in await db.ReturnLines.Where(x => x.TenantId == tenantId && x.ClaimId == claimId && returnLineIds.Contains(x.Id)).ToListAsync(cancellationToken)) settled.RawStatus = settledStatus;
        }
        claim.RawStatus = partialLineDecision ? "PARTIAL_LINE_DECISION_PENDING" : result.Value?.Status ?? (action switch
        {
            "APPROVE" => "ACCEPTED",
            "REJECT" => "REJECTED",
            _ => "PREAPPROVAL_CONFIRM_SUBMITTED"
        });
        var restocked = new List<StockProjectionChange>();
        foreach (var (stockLine, stockItem) in stockTargets)
        {
            var alreadyReceived = await db.ReturnStockDispositions.Where(x => x.TenantId == tenantId && x.ReturnLineId == stockLine.Id).SumAsync(x => x.Quantity, cancellationToken);
            var receivable = stockLine.Quantity - alreadyReceived;
            if (stockPlan[stockLine.Id] is { } plannedUnits) receivable = Math.Min(receivable, plannedUnits);
            if (receivable <= 0) continue;
            var stockKey = $"{normalizedKey}:stock:{stockLine.Id:N}";
            var stockAt = effect.CompletedAt.Value;
            db.ReturnStockDispositions.Add(new ReturnStockDisposition { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, ReturnLineId = stockLine.Id, InventoryItemId = stockItem.Id, Disposition = ReturnStockDispositionKind.Pass, Quantity = receivable, IdempotencyKey = stockKey, Reason = "İade kabulünde stoğa eklendi", ActorUserId = userId, CreatedAt = stockAt });
            stockItem.OnHand += receivable; stockItem.Available = Math.Max(0, stockItem.OnHand - stockItem.Reserved); stockItem.ProjectionVersion++; stockItem.Version++;
            db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = stockItem.Id, MovementType = "RETURN_PASS", QuantityDelta = receivable, SourceType = "RETURN_CLAIM", SourceId = claim.ExternalClaimId, SourceEventId = stockKey, IdempotencyKey = stockKey, OccurredAt = stockAt, RecordedAt = stockAt, ActorUserId = userId, CorrelationId = correlationId });
            restocked.Add(new StockProjectionChange(stockItem.VariantId, stockItem.ProjectionVersion, stockKey));
        }
        // Restocked units go to every connection selling the variant, committed with the restock.
        await StockProjectionOutbox.EnqueueAsync(db, tenantId, restocked, effect.CompletedAt.Value, cancellationToken);
        if (stockTargets.Count > 0) db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "RETURN_STOCK_DISPOSITION", TargetType = "ReturnClaim", TargetId = claimId.ToString("D"), Reason = "İade kabulünde stoğa eklendi", CorrelationId = correlationId, CreatedAt = effect.CompletedAt.Value });
        claim.UpdatedAt = effect.CompletedAt.Value;
        claim.Version++;
        await db.SaveChangesAsync(cancellationToken);

        // The action itself is already complete. Read-back is best-effort and
        // only refines the local status; it must not put the user action in a
        // background queue or hide the provider response from the panel.
        var readback = await returns.GetAsync(new AdapterContext(tenantId, claim.ConnectionId, correlationId, $"{normalizedKey}:readback", timeProvider.GetUtcNow().AddSeconds(30)), claim.ExternalClaimId, cancellationToken);
        if (readback.IsSuccess && readback.Value is not null)
        {
            var remoteStatus = MarketplaceReturnStatus.Canonicalize(readback.Value.RawStatus, readback.Value.CargoTrackingLink);
            if (!ReturnClaimStoragePolicy.ShouldPersist(remoteStatus))
            {
                // The decision just made is history: the claim is kept as
                // cancelled with it instead of being deleted.
                if (await CancelledReturnClaimCleanup.RemoveOrRetainAsync(db, tenantId, claimId, readback.Value.RawStatus, readback.Value.LastModifiedAt, timeProvider.GetUtcNow(), cancellationToken))
                {
                    decision.Status = "MANUAL_REVIEW";
                    decision.ErrorCode = "RETURN_ACTION_READBACK_CONFLICT";
                    decision.CompletedAt = timeProvider.GetUtcNow();
                    await db.SaveChangesAsync(cancellationToken);
                    return ServiceResult<ReturnDetailView>.Fail("RETURN_CLAIM_CANCELLED", "Pazaryerindeki iade talebi iptal edilmiş; iade, verilen kararla birlikte iptal olarak saklandı.", 409);
                }
                db.ChangeTracker.Clear();
                return ServiceResult<ReturnDetailView>.Fail("RETURN_CLAIM_CANCELLED", "Pazaryerindeki iade talebi iptal edilmiş; iptal kaydı saklanmadı.", 409);
            }
            if (!partialLineDecision && ReturnClaimStateMachine.CanTransition(claim.Status, remoteStatus)) claim.Status = remoteStatus;
            claim.RawStatus = partialLineDecision ? "PARTIAL_LINE_DECISION_PENDING" : readback.Value.RawStatus;
            claim.CargoProviderName = readback.Value.CargoProviderName ?? claim.CargoProviderName;
            claim.CargoTrackingNumber = readback.Value.CargoTrackingNumber ?? claim.CargoTrackingNumber;
            claim.ReasonCode = readback.Value.ReasonCode;
            claim.ReasonText = readback.Value.ReasonText;
            claim.ActionDueAt = readback.Value.ActionDueAt;
            claim.LastRemoteModifiedAt = readback.Value.LastModifiedAt;
            claim.UpdatedAt = timeProvider.GetUtcNow();
            claim.Version++;
            // The decision is judged by its own lines: Trendyol's claim status is the first item's, which may be a line
            // this decision did not cover and that still waits for its own decision.
            var decisionStatus = ReturnDecisionLinePolicy.Outcome(remoteStatus,
                lineIds.Select(lineId => readback.Value.Lines.FirstOrDefault(remoteLine => remoteLine.ExternalLineId == lineId)?.RawStatus).ToList());
            var confirmed = action == "APPROVE" && decisionStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || action == "REJECT" && decisionStatus is ReturnClaimStatus.Rejected or ReturnClaimStatus.Disputed
                || preApprovalConfirm && (decisionStatus is ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit
                    || decisionStatus == ReturnClaimStatus.ActionRequired && !HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(readback.Value.RawStatus));
            var conflicting = action == "APPROVE" && decisionStatus is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled
                || action == "REJECT" && decisionStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || preApprovalConfirm && decisionStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled;
            if (confirmed) { decision.Status = "SUCCEEDED"; decision.CompletedAt = claim.UpdatedAt; }
            else if (conflicting) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_ACTION_READBACK_CONFLICT"; decision.CompletedAt = claim.UpdatedAt; }
        }
        await db.SaveChangesAsync(cancellationToken);
        return await ReturnAsync(tenantId, claimId, cancellationToken);
    }

    public async Task<ServiceResult<ReturnDetailView>> ApplyDispositionAsync(Guid tenantId, Guid userId, Guid claimId, ReturnDispositionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ReturnStockDispositionKind>(command.Disposition, true, out var disposition)) return Invalid<ReturnDetailView>("disposition", "Disposition PASS, QUARANTINE, DAMAGED veya NOT_RECEIVED olmalıdır.");
        if (command.Quantity <= 0 || string.IsNullOrWhiteSpace(command.Reason)) return Invalid<ReturnDetailView>("quantity", "Pozitif quantity ve açıklama zorunludur.");

        // Idempotency and quantity reservation must share one serializable
        // transaction. Otherwise two concurrent operators can both observe
        // the same remaining quantity and increase inventory twice.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existing = await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing)
            {
                await transaction.CommitAsync(cancellationToken);
                return await ReturnAsync(tenantId, claimId, cancellationToken);
            }

            var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId, cancellationToken);
            if (claim is null) return NotFound<ReturnDetailView>();
            var line = await db.ReturnLines.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claimId && x.Id == command.ReturnLineId, cancellationToken);
            if (line is null) return NotFound<ReturnDetailView>();
            // Decided per line: a line approved on its own takes stock while the claim's other lines still wait.
            if (!ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, line.RawStatus)) return ServiceResult<ReturnDetailView>.Fail("RETURN_DISPOSITION_NOT_ALLOWED", "Stok kararı yalnız onaylanan iade ürünlerine uygulanabilir.", 409);
            var already = await db.ReturnStockDispositions.Where(x => x.TenantId == tenantId && x.ReturnLineId == line.Id).SumAsync(x => x.Quantity, cancellationToken);
            if (already + command.Quantity > line.Quantity) return ServiceResult<ReturnDetailView>.Fail("RETURN_QUANTITY_EXCEEDED", "Disposition toplamı iade satırı miktarını aşamaz.", 409);
            var variantId = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == line.OrderLineId).Select(x => x.VariantId).SingleAsync(cancellationToken);
            if (variantId is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "İade satırı yerel varyantla eşleşmiyor.", 422);
            var item = await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", cancellationToken);
            if (item is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "MAIN inventory item bulunamadı.", 422);
            var now = timeProvider.GetUtcNow();
            db.ReturnStockDispositions.Add(new ReturnStockDisposition { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, ReturnLineId = line.Id, InventoryItemId = item.Id, Disposition = disposition, Quantity = command.Quantity, IdempotencyKey = idempotencyKey, Reason = command.Reason.Trim(), ActorUserId = userId, CreatedAt = now });
            if (disposition == ReturnStockDispositionKind.Pass)
            {
                item.OnHand += command.Quantity; item.Available = Math.Max(0, item.OnHand - item.Reserved); item.ProjectionVersion++; item.Version++;
                db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = item.Id, MovementType = "RETURN_PASS", QuantityDelta = command.Quantity, SourceType = "RETURN_CLAIM", SourceId = claim.ExternalClaimId, SourceEventId = idempotencyKey, IdempotencyKey = idempotencyKey, OccurredAt = now, RecordedAt = now, ActorUserId = userId, CorrelationId = correlationId });
                await StockProjectionOutbox.EnqueueAsync(db, tenantId, [new StockProjectionChange(item.VariantId, item.ProjectionVersion, idempotencyKey)], now, cancellationToken);
            }
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "RETURN_STOCK_DISPOSITION", TargetType = "ReturnClaim", TargetId = claimId.ToString("D"), Reason = command.Reason.Trim(), CorrelationId = correlationId, CreatedAt = now });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken)) return await ReturnAsync(tenantId, claimId, cancellationToken);
            throw;
        }
        return await ReturnAsync(tenantId, claimId, cancellationToken);
    }

    private static readonly IReadOnlyList<string> ShipmentActions = ["PICKING", "INVOICED", "TRACKING_NUMBER", "CANCEL_ITEMS", "SPLIT", "MULTI_SPLIT", "CHANGE_CARGO_PROVIDER", "ALTERNATIVE_DELIVERY", "MANUAL_DELIVER", "MANUAL_RETURN"];
    private static readonly IReadOnlyList<string> HepsiburadaShipmentActions = ["UNPACK", "CHANGE_CARGO_PROVIDER"];
    private static readonly IReadOnlyList<string> HepsiburadaLabelFormats = ["ZPL", "BASE64ZPL", "PDF", "PNG", "JPG"];
    private static IReadOnlyList<string> HepsiburadaShipmentActionsFor(ShipmentPackage package) =>
        package.Status is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Returned or ShipmentPackageStatus.Cancelled
            ? []
            : HepsiburadaShipmentActions;
    private static bool IsEmptyJsonObject(string value) { try { using var document = JsonDocument.Parse(value); return document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.EnumerateObject().Any(); } catch (JsonException) { return false; } }
    private static readonly IReadOnlyList<string> StageLabelFormats = ["PDF"];
    private static readonly IReadOnlyList<string> ReturnActions = ["APPROVE", "REJECT"];

    private static bool PackageCreateFieldsValid(OrderPackageCreateRequest command)
    {
        // Only the lines, desi and parcel count are required; the other fields are checked when given.
        static bool Text(string? value, int maxLength) => string.IsNullOrEmpty(value) || value == value.Trim() && value.Length <= maxLength;
        return Text(command.Barcode, 128) && command.Barcode?.Any(char.IsWhiteSpace) != true
            && Text(command.CargoCompany, 100) && Text(command.Carrier, 100) && Text(command.CreationReason, 100)
            && Text(command.ShippingAddressLabel, 100) && Text(command.ShippingModel, 100)
            && command.Deci is >= 0 and <= 10000 && command.ParcelQuantity is >= 1 and <= 100
            && command.LineItems is { Count: > 0 and <= 100 }
            && command.LineItems.All(line => line.OrderLineId != Guid.Empty && line.Quantity is > 0 and <= 10000);
    }

    // Whether a Trendyol package action fits the package's state; the shipment detail offers only
    // the actions that pass (audit 2026-10-20 S7), and a request is refused with the same reason.
    internal static ServiceError? ShipmentActionStateError(ShipmentPackage package, string action)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal) { "PICKING", "INVOICED", "TRACKING_NUMBER", "CANCEL_ITEMS", "SPLIT", "MULTI_SPLIT", "CHANGE_CARGO_PROVIDER", "ALTERNATIVE_DELIVERY", "MANUAL_DELIVER", "MANUAL_RETURN" };
        if (!supported.Contains(action)) return new("SHIPMENT_ACTION_UNSUPPORTED", "Paket aksiyonu tanınmıyor.", 422);
        if (package.Status == ShipmentPackageStatus.PaymentPending) return new("SHIPMENT_PAYMENT_PENDING", "Trendyol ödeme onayı vermeden paket işlemi yapılamaz.", 409);
        if (action == "PICKING" && package.Status is not (ShipmentPackageStatus.New or ShipmentPackageStatus.OnHold)) return new("SHIPMENT_STATE_CONFLICT", "PICKING yalnız NEW/ON_HOLD paket için gönderilebilir.", 409);
        if (action == "INVOICED" && package.Status != ShipmentPackageStatus.Processing) return new("SHIPMENT_STATE_CONFLICT", "INVOICED yalnız PROCESSING paket için gönderilebilir.", 409);
        if (action == "TRACKING_NUMBER" && package.Status is not (ShipmentPackageStatus.Processing or ShipmentPackageStatus.ReadyToShip)) return new("SHIPMENT_STATE_CONFLICT", "TRACKING_NUMBER yalnız PICKING/INVOICED paket için gönderilebilir.", 409);
        if ((action is "CANCEL_ITEMS" or "SPLIT" or "MULTI_SPLIT" or "CHANGE_CARGO_PROVIDER" or "ALTERNATIVE_DELIVERY") && package.Status is (ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered or ShipmentPackageStatus.ReturnInTransit or ShipmentPackageStatus.Returned or ShipmentPackageStatus.Cancelled or ShipmentPackageStatus.ManualReview)) return new("SHIPMENT_STATE_CONFLICT", "Bu aksiyon sevk edilmiş veya terminal paket için kullanılamaz.", 409);
        // A package is marked delivered or returned by hand only after it reached the carrier.
        if (action == "MANUAL_DELIVER" && package.Status is not (ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Undelivered)) return new("SHIPMENT_STATE_CONFLICT", "Elle teslim yalnız kargodaki veya teslim edilemeyen paket için yapılabilir.", 409);
        if (action == "MANUAL_RETURN" && package.Status is not (ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Undelivered or ShipmentPackageStatus.ReturnInTransit)) return new("SHIPMENT_STATE_CONFLICT", "Elle iade yalnız kargodaki, teslim edilemeyen veya iade yolundaki paket için yapılabilir.", 409);
        return null;
    }

    private static ServiceError? ValidateShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        if (ShipmentActionStateError(package, action) is { } stateError) return stateError;
        if (action is "MANUAL_DELIVER" or "MANUAL_RETURN") return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson); var root = document.RootElement; if (root.ValueKind != JsonValueKind.Object) return new("SHIPMENT_ACTION_PAYLOAD_INVALID", "Paket aksiyonu body nesne olmalıdır.", 422);
            static bool Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
            static bool NonEmptyArray(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;
            static bool Object(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object;
            static bool Boolean(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            static bool OptionalPositiveNumber(JsonElement root, string name) => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number > 0;
            static bool StatusWithLinesAndParams(JsonElement root, string expected) => Text(root, "status") && string.Equals(root.GetProperty("status").GetString(), expected, StringComparison.OrdinalIgnoreCase) && NonEmptyArray(root, "lines") && Object(root, "params");
            var valid = action switch
            {
                "PICKING" => StatusWithLinesAndParams(root, "Picking"),
                "INVOICED" => StatusWithLinesAndParams(root, "Invoiced"),
                "TRACKING_NUMBER" => Text(root, "cargoSenderNumber") && Text(root, "providerCode") && (!root.TryGetProperty("returnTrackingNumber", out var returnTracking) || returnTracking.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(returnTracking.GetString())),
                "CANCEL_ITEMS" => TrendyolPackageActionBodies.TryBuildUnsupplied(payloadJson, out _),
                "SPLIT" => NonEmptyArray(root, "orderLineIds") && (!root.TryGetProperty("shouldKeepPreviousStatus", out var keep) || keep.ValueKind is JsonValueKind.True or JsonValueKind.False),
                "MULTI_SPLIT" => NonEmptyArray(root, "splitGroups"),
                "CHANGE_CARGO_PROVIDER" => Text(root, "cargoProvider"),
                "ALTERNATIVE_DELIVERY" => Boolean(root, "isPhoneNumber") && Text(root, "trackingInfo") && Object(root, "params") && OptionalPositiveNumber(root, "boxQuantity") && OptionalPositiveNumber(root, "deci"),
                _ => true
            };
            return valid ? null : new("SHIPMENT_ACTION_PAYLOAD_INVALID", $"{action} için zorunlu body alanları eksik.", 422);
        }
        catch (JsonException) { return new("SHIPMENT_ACTION_PAYLOAD_INVALID", "Paket aksiyonu body geçerli JSON olmalıdır.", 422); }
    }

    // n11 offers approving (Picking) and splitting through its API; a package
    // can only be split after it was approved.
    internal static ServiceError? N11ShipmentActionStateError(ShipmentPackage package, string action)
    {
        if (!MarketplaceJobProcessor.N11ShipmentActions.Contains(action, StringComparer.Ordinal))
            return new("SHIPMENT_ACTION_UNSUPPORTED", "N11 için yalnız siparişi işleme alma, paket bölme, işçilik bedeli ve lojistik toplama talebi desteklenir; diğer işlemleri N11 satıcı panelinden yapın.", 422);
        if (package.Status == ShipmentPackageStatus.PaymentPending) return new("SHIPMENT_PAYMENT_PENDING", "Ödeme onayı gelmeden paket işlemi yapılamaz.", 409);
        if (package.Status is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Returned or ShipmentPackageStatus.Cancelled)
            return new("SHIPMENT_STATE_CONFLICT", "Sevk edilmiş veya terminal pakette N11 işlemi yapılamaz.", 409);
        if (action is "SPLIT" or "MULTI_SPLIT" or "SPLIT_BY_QUANTITY" or "COLLECTION_REQUEST" && package.Status != ShipmentPackageStatus.Processing)
            return new("SHIPMENT_STATE_CONFLICT", "Bu N11 işlemi yalnız işleme alınmış (Picking) paketlerde yapılabilir.", 409);
        if (action == "PICKING" && package.Status is not (ShipmentPackageStatus.New or ShipmentPackageStatus.OnHold))
            return new("SHIPMENT_STATE_CONFLICT", "PICKING yalnız NEW/ON_HOLD paket için gönderilebilir.", 409);
        return null;
    }

    private static ServiceError? ValidateN11ShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        if (N11ShipmentActionStateError(package, action) is { } stateError) return stateError;
        if (action is "SPLIT_BY_QUANTITY" or "LABOR_COST" or "COLLECTION_REQUEST")
        {
            try { return MarketplaceHub.Infrastructure.Adapters.N11.N11PackageActionMapper.Body(action, payloadJson) is null ? new("SHIPMENT_ACTION_PAYLOAD_INVALID", "N11 işlem içeriği okunamadı.", 422) : null; }
            catch (JsonException) { return new("SHIPMENT_ACTION_PAYLOAD_INVALID", $"{action} için zorunlu body alanları eksik veya geçersiz.", 422); }
        }
        return ValidateShipmentAction(package, action, payloadJson);
    }

    // Pazarama: an order in "Created" can be moved to "Preparing" (picking) or marked not supplied; once preparing, the
    // package can still be split, re-addressed, get a labor cost or have a cancel request decided. Shipped and later
    // states are Pazarama's own cargo process and take no action from here.
    internal static IReadOnlyList<string> PazaramaShipmentActionsFor(ShipmentPackage package) => package.Status switch
    {
        ShipmentPackageStatus.New or ShipmentPackageStatus.OnHold => ["PICKING", "NOT_SUPPLIED", "SPLIT", "UPDATE_PACKAGE", "LABOR_COST", "CANCEL_APPROVE", "CANCEL_REJECT"],
        ShipmentPackageStatus.Processing => ["NOT_SUPPLIED", "SPLIT", "UPDATE_PACKAGE", "LABOR_COST", "CANCEL_APPROVE", "CANCEL_REJECT"],
        _ => []
    };

    private static ServiceError? ValidatePazaramaShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        if (!MarketplaceHub.Infrastructure.Adapters.Pazarama.PazaramaPackageActionMapper.Actions.Contains(action, StringComparer.Ordinal))
            return new("SHIPMENT_ACTION_UNSUPPORTED", "Pazarama için yalnız siparişi işleme alma, tedarik edilemedi, paket bölme/güncelleme, işçilik bedeli ve iptal talebi kararı desteklenir; diğer işlemleri Pazarama satıcı panelinden yapın.", 422);
        if (package.Status == ShipmentPackageStatus.PaymentPending) return new("SHIPMENT_PAYMENT_PENDING", "Ödeme onayı gelmeden paket işlemi yapılamaz.", 409);
        if (!PazaramaShipmentActionsFor(package).Contains(action, StringComparer.Ordinal))
            return new("SHIPMENT_STATE_CONFLICT", action == "PICKING" ? "Siparişi işleme alma yalnız yeni (Sipariş Alındı) paketlerde yapılabilir." : "Bu paketin durumunda Pazarama işlemi yapılamaz; kargo süreci Pazarama anlaşmalı kargosunda kendiliğinden ilerler.", 409);
        return MarketplaceHub.Infrastructure.Adapters.Pazarama.PazaramaPackageActionMapper.Validate(action, payloadJson) is { } invalid
            ? new("SHIPMENT_ACTION_PAYLOAD_INVALID", invalid, 422)
            : null;
    }

    private static ServiceError? ValidateHepsiburadaShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        if (action is not ("UNPACK" or "CHANGE_CARGO_PROVIDER")) return new("SHIPMENT_ACTION_UNSUPPORTED", "Hepsiburada için yalnız paketi açma ve izin verilen kargo firmasını değiştirme desteklenir.", 422);
        if (HepsiburadaShipmentActionsFor(package).Count == 0)
            return new("SHIPMENT_STATE_CONFLICT", "Sevk edilmiş veya terminal pakette Hepsiburada kargo işlemi yapılamaz.", 409);
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new("SHIPMENT_ACTION_PAYLOAD_INVALID", "Paket aksiyonu body nesne olmalıdır.", 422);
            if (action == "UNPACK")
                return document.RootElement.EnumerateObject().Any() ? new("SHIPMENT_ACTION_PAYLOAD_INVALID", "UNPACK payloadı boş JSON nesnesi olmalıdır.", 422) : null;
            var shortName = document.RootElement.TryGetProperty("CargoCompanyShortName", out var shortNameValue) && shortNameValue.ValueKind == JsonValueKind.String
                ? shortNameValue.GetString()
                : document.RootElement.TryGetProperty("cargoCompanyShortName", out shortNameValue) && shortNameValue.ValueKind == JsonValueKind.String
                    ? shortNameValue.GetString()
                    : null;
            return !string.IsNullOrWhiteSpace(shortName) && shortName == shortName.Trim() && shortName.Length <= 80 && !shortName.Any(char.IsWhiteSpace)
                ? null
                : new("SHIPMENT_ACTION_PAYLOAD_INVALID", "CargoCompanyShortName zorunludur.", 422);
        }
        catch (JsonException) { return new("SHIPMENT_ACTION_PAYLOAD_INVALID", "Paket aksiyonu geçerli JSON olmalıdır.", 422); }
    }

    private async Task<ServiceResult<Guid>> EnqueueRead(Guid tenantId, Guid connectionId, string capability, string type, string payload, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM") && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Aktif veya doğrulanmış marketplace bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.AllowsManualRead(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Read işlemi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        type = MarketplaceJobTypes.ForPlatform(connection.PlatformCode, type);
        return await Enqueue(tenantId, connectionId, type, $"{type.ToLowerInvariant()}:{connectionId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}", payload, correlationId, cancellationToken);
    }
    private async Task<ServiceResult<Guid>> Enqueue(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId, CancellationToken cancellationToken)
    {
        var recurringRead = type is MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.HepsiburadaReferenceSync or MarketplaceJobTypes.N11ReferenceSync or MarketplaceJobTypes.PazaramaReferenceSync or MarketplaceJobTypes.PttAvmReferenceSync or MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync or MarketplaceJobTypes.PttAvmOrderStatusSync or MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation or MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.QuestionSync;
        var activeJobs = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
            .Select(x => new { x.Id, x.JobType, x.JobDedupKey, x.Status, x.PayloadJson, x.AttemptCount, x.StartedAt })
            .ToListAsync(cancellationToken);
        var active = activeJobs.FirstOrDefault(x => x.JobType == type && (recurringRead ? x.JobDedupKey.StartsWith(dedup, StringComparison.Ordinal) : x.JobDedupKey == dedup));
        if (active is not null) return ServiceResult<Guid>.Ok(active.Id);

        // Manual sync buttons and scheduled policies use different dedup keys.
        // Collapse them onto the same provider lane before a second job reaches
        // the worker; otherwise the advisory lock turns a normal overlap into a
        // visible SYNC_LOCK_BUSY retry storm.
        var executionGroup = MarketplaceSyncExecutionLock.GroupFor(type);
        var fullScanRequest = IsFullOrderScanRequest(type, payload);
        var requestedExternalOrderId = TargetedExternalOrderId(payload);
        var conflictingJobs = activeJobs.Where(x => MarketplaceSyncExecutionLock.GroupFor(x.JobType) == executionGroup).ToList();
        var conflicting = fullScanRequest
            ? conflictingJobs.FirstOrDefault(x => FullOrderSyncConflictPolicy.Resolve(
                type,
                x.JobType,
                x.Status,
                requestedFullScan: true,
                conflictingJobTargetsSingleOrder: HasTargetedExternalOrderId(x.PayloadJson),
                conflictingJobHasStarted: x.AttemptCount > 0 || x.StartedAt is not null) == FullOrderSyncConflictResolution.PromotePending)
                ?? conflictingJobs.FirstOrDefault()
            : requestedExternalOrderId is not null
                ? TargetedOrderSyncConflictPolicy.PreferredConflictIndex(
                    conflictingJobs.Select(x => (
                        x.JobType,
                        TargetedExternalOrderId(x.PayloadJson),
                        x.Status,
                        x.AttemptCount > 0 || x.StartedAt is not null,
                        PayloadString(x.PayloadJson, "packageNumber"))).ToList(),
                    type,
                    requestedExternalOrderId,
                    PayloadString(payload, "packageNumber")) is { } preferredIndex
                    ? conflictingJobs[preferredIndex]
                    : null
                : conflictingJobs.FirstOrDefault();
        if (conflicting is not null)
        {
            var conflictResolution = FullOrderSyncConflictPolicy.Resolve(
                type,
                conflicting.JobType,
                conflicting.Status,
                fullScanRequest,
                HasTargetedExternalOrderId(conflicting.PayloadJson),
                conflicting.AttemptCount > 0 || conflicting.StartedAt is not null);
            if (conflictResolution == FullOrderSyncConflictResolution.PromotePending)
            {
                var now = timeProvider.GetUtcNow();
                var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                var updated = await db.IntegrationJobs
                    .Where(x => x.TenantId == tenantId && x.Id == conflicting.Id
                        && x.Status == JobStatus.Pending && x.AttemptCount == 0 && x.StartedAt == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.JobType, type)
                        .SetProperty(x => x.PayloadJson, payload)
                        .SetProperty(x => x.PayloadVersion, 1)
                        .SetProperty(x => x.PayloadHash, payloadHash)
                        .SetProperty(x => x.Priority, x => Math.Min(x.Priority, Priority(type)))
                        .SetProperty(x => x.AvailableAt, now)
                        .SetProperty(x => x.CreatedAt, now)
                        .SetProperty(x => x.CorrelationId, correlationId)
                        .SetProperty(x => x.ProgressCurrent, 0)
                        .SetProperty(x => x.ProgressTotal, (int?)null)
                        .SetProperty(x => x.ProgressPercent, (int?)null)
                        .SetProperty(x => x.ProgressLabel, (string?)null)
                        .SetProperty(x => x.ProgressReceived, 0)
                        .SetProperty(x => x.ProgressProcessed, 0)
                        .SetProperty(x => x.ProgressSkipped, 0)
                        .SetProperty(x => x.ProgressFailed, 0)
                        .SetProperty(x => x.Version, x => x.Version + 1),
                        cancellationToken);
                if (updated == 0)
                    return ServiceResult<Guid>.Fail("ORDER_SYNC_ALREADY_RUNNING", "Tam sipariş taraması, bağlantıdaki sipariş işlemi başlarken kuyruğa alınamadı. İşlem tamamlanınca yeniden deneyin.", 409);
                return ServiceResult<Guid>.Ok(conflicting.Id);
            }
            if (requestedExternalOrderId is not null)
            {
                var targetedResolution = TargetedOrderSyncConflictPolicy.Resolve(
                    type,
                    requestedExternalOrderId,
                    conflicting.JobType,
                    TargetedExternalOrderId(conflicting.PayloadJson),
                    conflicting.Status,
                    conflicting.AttemptCount > 0 || conflicting.StartedAt is not null,
                    PayloadString(payload, "packageNumber"),
                    PayloadString(conflicting.PayloadJson, "packageNumber"));
                if (targetedResolution == TargetedOrderSyncConflictResolution.PromotePending)
                {
                    var now = timeProvider.GetUtcNow();
                    var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                    var updated = await db.IntegrationJobs
                        .Where(x => x.TenantId == tenantId && x.Id == conflicting.Id
                            && x.Status == JobStatus.Pending && x.AttemptCount == 0 && x.StartedAt == null)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.JobType, type)
                            .SetProperty(x => x.PayloadJson, payload)
                            .SetProperty(x => x.PayloadVersion, 1)
                            .SetProperty(x => x.PayloadHash, payloadHash)
                            .SetProperty(x => x.Priority, x => Math.Min(x.Priority, Priority(type)))
                            .SetProperty(x => x.AvailableAt, now)
                            .SetProperty(x => x.CreatedAt, now)
                            .SetProperty(x => x.CorrelationId, correlationId)
                            .SetProperty(x => x.ProgressCurrent, 0)
                            .SetProperty(x => x.ProgressTotal, (int?)null)
                            .SetProperty(x => x.ProgressPercent, (int?)null)
                            .SetProperty(x => x.ProgressLabel, (string?)null)
                            .SetProperty(x => x.ProgressReceived, 0)
                            .SetProperty(x => x.ProgressProcessed, 0)
                            .SetProperty(x => x.ProgressSkipped, 0)
                            .SetProperty(x => x.ProgressFailed, 0)
                            .SetProperty(x => x.MaxAttempts, x => Math.Max(x.MaxAttempts, 30))
                            .SetProperty(x => x.Version, x => x.Version + 1),
                            cancellationToken);
                    if (updated == 0)
                        return ServiceResult<Guid>.Fail("ORDER_SYNC_ALREADY_RUNNING", "Tekil sipariş okuması mevcut işlem başlarken kuyruğa alınamadı. İşlem tamamlanınca yeniden deneyin.", 409);
                    return ServiceResult<Guid>.Ok(conflicting.Id);
                }
                if (targetedResolution == TargetedOrderSyncConflictResolution.QueueBehindActiveWork)
                {
                    var queuedJob = NewJob(
                        tenantId,
                        connectionId,
                        type,
                        recurringRead ? $"{dedup}:{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}" : dedup,
                        payload,
                        correlationId);
                    EnsureTargetedOrderReadRetryBudget(queuedJob, type, payload);
                    db.IntegrationJobs.Add(queuedJob);
                    await db.SaveChangesAsync(cancellationToken);
                    return ServiceResult<Guid>.Ok(queuedJob.Id);
                }
                if (targetedResolution == TargetedOrderSyncConflictResolution.Reject)
                    return ServiceResult<Guid>.Fail("ORDER_SYNC_ALREADY_RUNNING", "Tekil sipariş okuması bağlantıda başka bir sipariş işlemi bulunduğu için kuyruğa alınamadı. İşlem tamamlanınca yeniden deneyin.", 409);
                return ServiceResult<Guid>.Ok(conflicting.Id);
            }
            if (conflictResolution == FullOrderSyncConflictResolution.Reject)
                return ServiceResult<Guid>.Fail("ORDER_SYNC_ALREADY_RUNNING", "Tam sipariş taraması, bağlantıda başka bir sipariş işlemi çalıştığı için kuyruğa alınamadı. İşlem tamamlanınca yeniden deneyin.", 409);
            if (ProductImportConcurrencyPolicy.RejectsModeCollision(type, conflicting.JobType))
                return ServiceResult<Guid>.Fail("PRODUCT_SYNC_ALREADY_RUNNING", "Bu bağlantıda başka bir ürün aktarımı çalışıyor. Önce mevcut işlemi durdurup eşlemeyi yeniden başlatın.", 409);
            return ServiceResult<Guid>.Ok(conflicting.Id);
        }

        var job = NewJob(tenantId, connectionId, type, recurringRead ? $"{dedup}:{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}" : dedup, payload, correlationId);
        EnsureTargetedOrderReadRetryBudget(job, type, payload);
        db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    private static void EnsureTargetedOrderReadRetryBudget(IntegrationJob job, string jobType, string payload)
    {
        if ((jobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync)
            && TargetedExternalOrderId(payload) is not null)
            job.MaxAttempts = Math.Max(job.MaxAttempts, 30);
    }
    private static bool IsFullOrderScanRequest(string jobType, string payload)
    {
        if (jobType is not (MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync)) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static bool HasTargetedExternalOrderId(string payload)
        => TargetedExternalOrderId(payload) is not null;

    private static string? TargetedExternalOrderId(string payload)
        => PayloadString(payload, "externalOrderId");

    private static string? PayloadString(string payload, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty(propertyName, out var id) || id.ValueKind != JsonValueKind.String)
                return null;
            var value = id.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException) { return null; }
    }
    private IntegrationJob NewJob(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = type, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup, Priority = Priority(type), TriggerType = "user", AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
    internal static int Priority(string type) => JobPriorityPolicy.ForUser(type);
    private Task<bool> Supported(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken) => db.PlatformCapabilities.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken);
    // Return decisions whose outcome is still open: waiting to be sent or verified, or sent while the request's effect
    // is not settled. A sent decision on line-level marketplaces is closed once the marketplace accepted it (the
    // decided lines are no longer actionable); Hepsiburada decides the whole claim and its sent decision holds the
    // claim until the claim read confirms it, as before.
    private IQueryable<ReturnDecision> OpenReturnDecisions(Guid tenantId, Guid claimId, bool sentDecisionHoldsClaim) => db.ReturnDecisions.AsNoTracking()
        .Where(x => x.TenantId == tenantId && x.ClaimId == claimId
            && (x.Status == "PENDING" || x.Status == "RETRY_SCHEDULED" || x.Status == "MANUAL_REVIEW"
                || x.Status == "SUBMITTED" && db.ExternalEffectRecords.Any(effect => effect.TenantId == x.TenantId && effect.IdempotencyKey == x.IdempotencyKey && (sentDecisionHoldsClaim || effect.CompletedAt == null))));

    private Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "STAGE", cancellationToken);
    private Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "PRODUCTION", cancellationToken);
    private async Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) { if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false; var settings = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.SettingsJson).SingleOrDefaultAsync(cancellationToken); if (settings is null) return false; try { using var document = JsonDocument.Parse(settings); return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var value) && value.ValueKind == JsonValueKind.True; } catch (JsonException) { return false; } }
    private async Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => (await ExternalWritePolicyAsync(tenantId, connectionId, resourceType, cancellationToken)).Enabled;
    private async Task<(bool Enabled, int IntervalSeconds)> ExternalWritePolicyAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null ? (false, 0) : (policy.Enabled, Math.Clamp(policy.IntervalSeconds, 0, 86_400));
    }
    private async Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) { var capability = await db.PlatformCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken); if (capability?.ConstraintsJson is null) return []; try { using var doc = JsonDocument.Parse(capability.ConstraintsJson); return doc.RootElement.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : []; } catch (JsonException) { return []; } }
    private static ProductVariant? ResolveVariant(OrderLine line, IReadOnlyDictionary<Guid, ProductVariant> variants, IReadOnlyDictionary<string, ProductVariant> variantsBySku, IReadOnlyDictionary<string, ProductVariant> variantsByBarcode) =>
        line.VariantId is { } variantId ? variants.GetValueOrDefault(variantId) :
        variantsBySku.GetValueOrDefault(line.Sku) ?? CatalogLookupKeys(line.Barcode).Select(key => variantsByBarcode.GetValueOrDefault(key)).FirstOrDefault(variant => variant is not null);

    private async Task<Dictionary<Guid, string>> MediaUrls(Guid tenantId, IEnumerable<Guid?> variantIds, CancellationToken cancellationToken)
    {
        var ids = variantIds.Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await (from variant in db.ProductVariants.AsNoTracking()
                          join media in db.ProductMedia.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { media.TenantId, media.ProductId }
                          join asset in db.FileAssets.AsNoTracking() on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                          where variant.TenantId == tenantId && ids.Contains(variant.Id) && (media.VariantId == null || media.VariantId == variant.Id) && media.Status == "ACTIVE" && asset.Status == "ACTIVE" && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                          orderby media.VariantId == variant.Id ? 0 : 1, media.SortOrder
                          select new { VariantId = variant.Id, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First().Classification == "PRODUCT_MEDIA_URL" ? x.First().Url : $"/api/v1/files/product-media/{x.First().Id:D}/content");
    }

    private static (string Name, string? Email, string? Phone, string? TaxOrIdentityNumber, string OrderType, bool IsMicroExport, bool? IsEInvoiceAvailable) Customer(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var name = ResolveCustomerName(customerJson, invoiceAddressJson, shipmentAddressJson);
        var email = JsonText(customerJson, "customerEmail", "email") ?? JsonText(invoiceAddressJson, "email") ?? JsonText(shipmentAddressJson, "email");
        var phone = JsonText(customerJson, "customerPhone", "customerPhoneNumber", "phone", "phoneNumber") ?? JsonText(invoiceAddressJson, "phone", "phoneNumber", "mobilePhone") ?? JsonText(shipmentAddressJson, "phone", "phoneNumber", "mobilePhone");
        var tax = ResolveCustomerTaxOrIdentityNumber(customerJson, invoiceAddressJson, shipmentAddressJson);
        var microText = JsonText(customerJson, "shipmentPackageType", "orderType");
        // Trendyol exports through its 3P partner model explicitly return micro=false. The documented
        // 3pByTrendyol=true signal is still an export order and must be presented as such to operators.
        // Historical Stage snapshots may predate the export flags while retaining Trendyol's
        // documented PM3/Arvato export-partner identity. Keep that narrow legacy signal so
        // existing export orders are not presented as domestic orders.
        var legacyExportPartner = IsLegacyTrendyolExportPartner(name);
        var micro = JsonBool(customerJson, "micro", "microExport", "3pByTrendyol") || microText?.Contains("MICRO", StringComparison.OrdinalIgnoreCase) == true || microText?.Contains("İHRAC", StringComparison.OrdinalIgnoreCase) == true || legacyExportPartner;
        var commercial = JsonBool(customerJson, "commercial") || !string.IsNullOrWhiteSpace(JsonText(invoiceAddressJson, "company", "companyName", "taxOffice"));
        var eInvoice = JsonNullableBool(customerJson, "eInvoiceAvailable", "isEInvoice") ?? JsonNullableBool(invoiceAddressJson, "eInvoiceAvailable", "isEInvoice");
        return (name, email, phone, tax, micro ? "MIKRO_IHRACAT" : commercial ? "KURUMSAL" : "BIREYSEL", micro, eInvoice);
    }

    internal static string ResolveCustomerName(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var customerName = NameFrom(customerJson, "customerFirstName", "customerLastName", "customerName", "customerFullName", "buyerName", "fullName", "name");
        var invoiceName = NameFrom(invoiceAddressJson, "firstName", "lastName", "invoiceFirstName", "invoiceLastName", "fullName", "name", "company", "companyName");
        var shipmentName = NameFrom(shipmentAddressJson, "firstName", "lastName", "shippingFirstName", "shippingLastName", "fullName", "name", "company", "companyName");
        return FirstMeaningful(customerName, invoiceName, shipmentName) ?? "—";
    }

    internal static string? ResolveCustomerTaxOrIdentityNumber(string customerJson, string invoiceAddressJson, string shipmentAddressJson) =>
        JsonText(customerJson, "customerTaxNumber", "taxNumber", "identityNumber", "customerIdentityNumber", "tcIdentityNumber")
        ?? JsonText(invoiceAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber")
        ?? JsonText(shipmentAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber");

    private static string? NameFrom(string json, string firstName, string lastName, params string[] fullNameFields)
    {
        var first = JsonText(json, firstName);
        var last = JsonText(json, lastName);
        var combined = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return !string.IsNullOrWhiteSpace(combined) ? combined : JsonText(json, fullNameFields);
    }

    private static string? FirstMeaningful(params string?[] names) => names.FirstOrDefault(name => IsMeaningfulText(name) && !IsPlaceholderCustomerName(name!));

    private static bool IsPlaceholderCustomerName(string name) => name.Trim() is "Adı Soyadı" or "Ad Soyad" or "İsim Soyisim";

    internal static bool IsLegacyTrendyolExportPartner(string name) =>
        name.Contains("PM3", StringComparison.OrdinalIgnoreCase) && name.Contains("ARVATO", StringComparison.OrdinalIgnoreCase);

    // A shipping deadline only: Trendyol's (extended) agreed hand-over date or a marketplace's due date;
    // delivery estimates and modification times are not deadlines.
    private static DateTimeOffset? OperationalDueAt(string json) =>
        JsonInstant(json, "extendedAgreedDeliveryDate", "agreedDeliveryDate", "dueDate", "shipmentDueDate", "deliveryDueAt");

    internal static string InvoiceLabel(Invoice? invoice, string customerJson, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice, customerJson, [], marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(Invoice? invoice, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        // The local fiscal invoice and the marketplace delivery are separate
        // facts. A fiscal invoice number does not prove that Trendyol accepted
        // the document, so Submitted/Accepted/MarketplacePending stay visible
        // as an in-progress marketplace delivery.
        if (invoiceStatus is { } status)
        {
            // A lost provider answer is not a rejection: the invoice may exist at the provider.
            if (status == InvoiceStatus.UnknownResult) return "FATURA_BELIRSIZ";
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview) return "FATURA_REDDEDILDI";
            if (status == InvoiceStatus.MarketplaceFailed) return "FATURA_PLATFORMA_AKTARILMADI";
            if (status is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Submitted) return "FATURA_ISLENIYOR";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal) return "FATURA_IPTAL";
            if (status == InvoiceStatus.Completed)
                return "FATURA_KONTROLDE";
            if (status is InvoiceStatus.Submitting or InvoiceStatus.Submitted)
                return "FATURA_ISLENIYOR";
            if (!string.IsNullOrWhiteSpace(invoiceNumber) && status is not (InvoiceStatus.Draft or InvoiceStatus.Validating or InvoiceStatus.Ready))
                return "FATURA_KONTROLDE";
            return "FATURA_ISLENIYOR";
        }
        // Trendyol's order payloads expose the same business fact in two
        // different places depending on the endpoint/version: invoiceStatus
        // in the package snapshot, or the package's raw status as INVOICED.
        // RawStatus must be considered before the snapshot fallback because a
        // later shipment update can replace the snapshot's invoice fields.
        if (packageRawStatuses.Any(IsInvoicedRemoteStatus))
            return "FATURA_KONTROLDE";

        var remote = JsonText(customerJson, "marketplaceInvoiceStatus", "invoiceStatus")?.Trim().ToUpperInvariant();
        if (remote is "INVOICED") return "FATURA_KONTROLDE";
        if (remote is "RECEIVED") return "FATURA_KONTROLDE";
        if (remote is "REJECTED") return "FATURA_REDDEDILDI";
        if (remote is "NOTINVOICED" or "NOT_INVOICED") return "FATURA_BEKLIYOR";
        // Missing marketplace evidence is not the same as an explicit
        // NotInvoiced response. Historical Trendyol payloads often omit the
        // invoice field entirely; presenting those packages as actionable
        // invoice work creates false Dashboard counts.
        return "FATURA_BILINMIYOR";
    }

    internal static string InvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        if (invoiceStatus is { } status)
        {
            if (status == InvoiceStatus.UnknownResult) return "FATURA_BELIRSIZ";
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview or InvoiceStatus.MarketplaceFailed)
                return status == InvoiceStatus.MarketplaceFailed ? "FATURA_PLATFORMA_AKTARILMADI" : "FATURA_REDDEDILDI";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal)
                return "FATURA_IPTAL";
            if (status is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Submitted or InvoiceStatus.Submitting)
                return "FATURA_ISLENIYOR";
            // The marketplace observation is authoritative for the delivery
            // leg. A local invoice number only proves that our fiscal provider
            // created a document; it does not prove Trendyol accepted it.
            if (status == InvoiceStatus.Completed) return "FATURA_KONTROLDE";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Rejected) return "FATURA_REDDEDILDI";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Received) return "FATURA_KONTROLDE";
            return InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
        }
        return marketplaceStatus switch
        {
            MarketplaceInvoiceStatus.Invoiced => "FATURA_KONTROLDE",
            MarketplaceInvoiceStatus.Received => "FATURA_KONTROLDE",
            MarketplaceInvoiceStatus.Rejected => "FATURA_REDDEDILDI",
            MarketplaceInvoiceStatus.NotInvoiced => "FATURA_BEKLIYOR",
            _ => InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed)
        };
    }

    internal static string ReturnInvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        // An absent marketplace observation does not prove that the package is
        // waiting for an invoice. Keep unknown separate so the return list does
        // not report a false pending state when Trendyol has not exposed it.
        return InvoiceLabel(invoice, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
    }

    // A status the seller set by hand on the invoice page ("Faturası yüklendi" / "Fatura bekliyor", for marketplaces
    // whose API cannot show an invoice uploaded in their own seller panel) decides before any other evidence.
    internal static string? ManualInvoiceLabel(string? manualInvoiceStatus) =>
        manualInvoiceStatus?.Trim().ToUpperInvariant() switch
        {
            "UPLOADED" => "FATURA_KONTROLDE",
            "PENDING" => "FATURA_BEKLIYOR",
            _ => null
        };

    // The one invoice label of a package: the orders list, the order detail and the invoice page read it the same way.
    internal static string PackageInvoiceLabel(ShipmentPackage? package, Invoice? invoice, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false) =>
        ManualInvoiceLabel(package?.ManualInvoiceStatus)
            ?? InvoiceLabelForPlatform(invoice, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, customerJson, packageRawStatuses, platformCode, marketplaceDeliveryConfirmed);

    // The fiscal states the invoice workspace labels (pre-provider refusal, lost answer, adjustment) read the same here.
    internal static string InvoiceLabelForPlatform(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false)
        => invoice is not null && InvoicingBillingService.SharedFiscalLabel(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference) is { } shared
            ? shared
            : InvoiceLabelForPlatform(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, platformCode, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabelForPlatform(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false)
    {
        var label = InvoiceLabel(invoiceStatus, invoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
        {
            if (invoiceStatus == InvoiceStatus.Draft) return "FATURA_BEKLIYOR";
            if (invoiceStatus == InvoiceStatus.Completed) return "FATURA_KONTROLDE";
            if (label == "FATURA_BILINMIYOR") return "FATURA_BEKLIYOR";
        }
        // n11 gives no invoice state, only the link of an invoice that was attached to the order (empty until
        // one is); Pazarama gives nothing at all. A link means an invoice exists at the marketplace but no local
        // record matches it, so it is shown for review; without one the state stays unknown, never "waiting".
        else if (label == "FATURA_BILINMIYOR" && string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(JsonText(customerJson, "invoiceLink")))
            return "FATURA_KONTROLDE";
        // n11 and PTT AVM never show an invoice uploaded in their seller panel, so an unknown state there cannot be
        // checked from the marketplace: it is shown as waiting for an invoice (owner, 24 Oct 2026: no separate
        // "Fatura Doğrulanamadı"), with the not-invoiced ones, until the seller marks it uploaded.
        else if (label == "FATURA_BILINMIYOR" && IsUnverifiableInvoicePlatform(platformCode))
            return UnverifiedInvoiceLabel;
        return label;
    }

    internal const string UnverifiedInvoiceLabel = "FATURA_BEKLIYOR";

    internal static bool IsUnverifiableInvoicePlatform(string? platformCode) =>
        platformCode?.Trim().ToUpperInvariant() is "N11" or "PTTAVM";

    internal static ShipmentPackage? SelectDisplayPackage(IEnumerable<ShipmentPackage> packages, string? platformCode)
    {
        var ordered = packages.OrderByDescending(x => x.StatusOccurredAt).ThenByDescending(x => x.UpdatedAt).ToList();
        // The package an order card shows is a live one; a cancelled split package only when
        // nothing else is left.
        if (!string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            return ordered.FirstOrDefault(x => x.Status != ShipmentPackageStatus.Cancelled) ?? ordered.FirstOrDefault();

        var visible = ordered.Where(x => !(x.ExternalPackageId.StartsWith("order:", StringComparison.OrdinalIgnoreCase)
            && x.ExternalPackageId.EndsWith(":remainder", StringComparison.OrdinalIgnoreCase))).ToList();
        return visible.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CargoTrackingNumber))
            ?? visible.FirstOrDefault(x => x.Status != ShipmentPackageStatus.Cancelled)
            ?? visible.FirstOrDefault();
    }

    private static bool IsInvoicedRemoteStatus(string? status) =>
        string.Equals(status?.Trim(), "INVOICED", StringComparison.OrdinalIgnoreCase);

    private static string? InvoiceDocumentUrl(string customerJson)
    {
        return ValidInvoiceDocumentUrl(JsonText(customerJson, "invoiceLink"));
    }

    private static string? ValidInvoiceDocumentUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    // n11 facts the order card shows besides the shared fields: cargo tracking (number and carrier
    // link), delivery type, commission rate and the discounts split by who paid them.
    internal static IReadOnlyList<OrderInfoItem> N11OrderInfo(IEnumerable<string?> lineSnapshots, string customerSnapshotJson)
    {
        var info = new List<OrderInfoItem>();
        var trackingNumber = JsonText(customerSnapshotJson, "cargoSenderNumber");
        var trackingLink = JsonText(customerSnapshotJson, "cargoTrackingLink");
        var safeLink = Uri.TryCreate(trackingLink, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
        if (trackingNumber is not null) info.Add(new("Kargo takip no", trackingNumber, safeLink));
        if (JsonText(customerSnapshotJson, "deliveryAddressType") is { } deliveryType) info.Add(new("Teslimat tipi", deliveryType));

        decimal mallDiscount = 0, sellerDiscount = 0;
        var rates = new SortedSet<decimal>();
        foreach (var snapshot in lineSnapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot)) continue;
            try
            {
                using var document = JsonDocument.Parse(snapshot);
                var line = document.RootElement;
                if (line.ValueKind != JsonValueKind.Object) continue;
                mallDiscount += JsonAmount(line, "totalMallDiscountPrice");
                sellerDiscount += JsonAmount(line, "totalSellerDiscountPrice");
                if (line.TryGetProperty("commissionRate", out var rate) && rate.ValueKind == JsonValueKind.Number && rate.TryGetDecimal(out var commission))
                    rates.Add(commission - JsonAmount(line, "sellerCampaignCommissionRate"));
            }
            catch (JsonException) { }
        }
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        if (rates.Count > 0) info.Add(new("Komisyon oranı", string.Join(" / ", rates.Select(rate => "%" + rate.ToString("0.##", tr)))));
        if (mallDiscount > 0) info.Add(new("N11 indirimi", mallDiscount.ToString("N2", tr) + " ₺"));
        if (sellerDiscount > 0) info.Add(new("Satıcı indirimi", sellerDiscount.ToString("N2", tr) + " ₺"));
        return info;
    }

    private static decimal JsonAmount(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) ? amount : 0;

    private static SourceLineView SourceLine(string? json)
    {
        var snapshot = json ?? "{}";
        var image = NormalizeImageUrl(SourceImageUrl(snapshot));
        var color = JsonText(snapshot, "productColor", "color", "colorName");
        var size = JsonText(snapshot, "productSize", "size", "sizeName");
        var options = new List<string>();
        if (!string.IsNullOrWhiteSpace(color)) options.Add($"Renk: {color}");
        if (!string.IsNullOrWhiteSpace(size)) options.Add($"Beden: {size}");
        return new(image, JsonText(snapshot, "modelCode"), options.Count == 0 ? null : string.Join(" | ", options));
    }

    // The product's own image fields, the most specific first. The first field that holds a usable address wins,
    // wherever it sits in the snapshot: a match in document order picked a claim photo listed before the product's.
    private static readonly string[] ProductImageFields = ["productImageUrl", "productImageUrlFormat", "emaproductImageUrlFormat", "imageUrl", "productImage", "image"];

    // Parts of a line snapshot whose images are not the product: a Hepsiburada claim merged into the line keeps the
    // customer's evidence photos under Reports[].ImageUrl (an images.hepsiburada.net/oms/claim address the panel cannot load).
    private static readonly HashSet<string> NonProductImageContainers = new(StringComparer.OrdinalIgnoreCase) { "Reports" };

    internal static string? SourceImageUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindImageUrl(document.RootElement)
                ?? ProductImageFields.Select(field => FindImageField(document.RootElement, field)).FirstOrDefault(value => value is not null);
        }
        catch (JsonException) { return null; }
    }

    // The line's image and, when it is the marketplace's, the catalog's photo to show if that address no longer loads.
    internal static (string? ImageUrl, string? FallbackImageUrl) LineImageUrls(string? sourceImageUrl, string? catalogImageUrl) =>
        sourceImageUrl is null
            ? (catalogImageUrl, null)
            : (sourceImageUrl, catalogImageUrl is null || string.Equals(catalogImageUrl, sourceImageUrl, StringComparison.Ordinal) ? null : catalogImageUrl);

    private static string? FindImageField(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (NonProductImageContainers.Contains(property.Name)) continue;
                if (property.Name.Equals(field, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (NormalizeImageUrl(value) is not null) return value;
                }
                var nested = FindImageField(property.Value, field); if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageField(item, field); if (nested is not null) return nested; }
        return null;
    }

    internal static string? NormalizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        // Hepsiburada returns productImageUrlFormat with a {size} token.
        // Keep the stored marketplace snapshot untouched and resolve the token
        // for the order UI when projecting the image URL.
        candidate = candidate.Replace("{size}", "500", StringComparison.OrdinalIgnoreCase);
        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        else if (candidate.StartsWith("/", StringComparison.Ordinal)) candidate = "https://cdn.dsmcdn.com" + candidate;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    private static string? FindImageUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (NonProductImageContainers.Contains(property.Name)) continue;
                if (property.Name.Equals("images", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var image in property.Value.EnumerateArray())
                    {
                        var url = image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out var value) ? value.ToString() : image.ToString();
                        if (NormalizeImageUrl(url) is not null) return url;
                    }
                var nested = FindImageUrl(property.Value); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageUrl(item); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private sealed record SourceLineView(string? ImageUrl, string? ModelCode, string? OptionSignature);

    internal static bool SearchesCustomerSnapshot(string term) => term.Any(char.IsLetter);

    private const string LikeEscape = "\\";

    // "%term%" for ILIKE as typed, and Turkish-folded (TurkishSearchText.Fold) for a column folded the
    // same way (TurkishSearchText.FoldedILike), with the LIKE wildcards of the term escaped (a "%" in
    // the term is a percent sign).
    internal static (string AsTyped, string Folded) SearchPatterns(string term)
    {
        static string Pattern(string value) => "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return (Pattern(term), Pattern(TurkishSearchText.Fold(term)));
    }

    // A PostgreSQL (and .NET) regular expression that finds the term in a customer snapshot value
    // whose key is not an e-mail field ("email", "customerEmail", ...): a list search matches names
    // and places, never e-mail addresses. It is matched case-insensitively (~*), and the term's
    // Turkish lower and upper forms are alternatives, so "ışık" finds "IŞIK" and "istanbul" "İSTANBUL".
    internal static string CustomerSnapshotSearchPattern(string term)
    {
        static string Escape(string value)
        {
            var escaped = new StringBuilder(value.Length * 2);
            foreach (var ch in value)
            {
                if (char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch > 127) escaped.Append(ch);
                else escaped.Append('\\').Append(ch);
            }
            return escaped.ToString();
        }
        var variants = new[] { term, term.ToLower(TurkishCulture), term.ToUpper(TurkishCulture) }.Distinct(StringComparer.Ordinal).Select(Escape);
        return "\"(?![A-Za-z0-9_]*[Mm][Aa][Ii][Ll]\")[A-Za-z0-9_]*\"\\s*:\\s*\"[^\"]*(?:" + string.Join('|', variants) + ")";
    }

    internal static string? MaskIdentityNumber(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > 4 ? $"***{trimmed[^4..]}" : "***";
    }

    // Only the delivery fields a label prints: no phone, e-mail, identity or tax number.
    internal static OrderAddressView? ListAddress(string shipmentAddressJson)
    {
        var name = NameFrom(shipmentAddressJson, "firstName", "lastName", "shippingFirstName", "shippingLastName", "fullName", "name", "company", "companyName");
        var address = new OrderAddressView(
            IsMeaningfulText(name) ? name : null,
            JsonText(shipmentAddressJson, "fullAddress"),
            JsonText(shipmentAddressJson, "address1"),
            JsonText(shipmentAddressJson, "address2"),
            JsonText(shipmentAddressJson, "neighborhood"),
            JsonText(shipmentAddressJson, "district", "countyName"),
            JsonText(shipmentAddressJson, "city", "province"),
            JsonText(shipmentAddressJson, "postalCode", "zip"),
            JsonText(shipmentAddressJson, "countryCode", "country"),
            JsonBool(shipmentAddressJson, "isDeliveryPoint"),
            JsonText(shipmentAddressJson, "deliveryPointType"));
        return address == new OrderAddressView(null, null, null, null, null, null, null, null, null) ? null : address;
    }

    private static string? JsonText(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindText(document.RootElement, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException) { return null; }
    }

    private static string? FindText(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name) && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array))
                {
                    var value = property.Value.ToString();
                    if (IsMeaningfulText(value)) return value;
                }
                var nested = FindText(property.Value, names); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindText(item, names); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private static bool IsMeaningfulText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Any(char.IsLetterOrDigit);

    private static bool JsonBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        return bool.TryParse(value, out var parsed) && parsed || value is "1" or "YES" or "EVET";
    }

    private static bool? JsonNullableBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value is "1" or "YES" or "EVET" ? true : value is "0" or "NO" or "HAYIR" ? false : null;
    }

    private static DateTimeOffset? JsonInstant(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var namesSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return FindInstant(document.RootElement, namesSet);
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? FindInstant(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var num) && num > 0)
                    {
                        try { return DateTimeOffset.FromUnixTimeMilliseconds(num); } catch (ArgumentOutOfRangeException) { }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var str = property.Value.GetString();
                        if (long.TryParse(str, out var numStr) && numStr > 0)
                        {
                            try { return DateTimeOffset.FromUnixTimeMilliseconds(numStr); } catch (ArgumentOutOfRangeException) { }
                        }
                        if (DateTimeOffset.TryParse(str, out var parsed)) return parsed;
                    }
                }
                var nested = FindInstant(property.Value, names);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindInstant(item, names);
                if (nested is not null) return nested;
            }
        }
        return null;
    }
    private async Task<PlatformConnection?> ActiveTrendyolConnection(Guid tenantId, CancellationToken cancellationToken) =>
        await (from connection in db.PlatformConnections.AsNoTracking()
               where connection.TenantId == tenantId && connection.PlatformCode == "TRENDYOL" && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                   && db.PlatformCredentials.Any(credential => credential.TenantId == tenantId && credential.ConnectionId == connection.Id && credential.RevokedAt == null)
               orderby connection.Id
               select connection).FirstOrDefaultAsync(cancellationToken);

    private static ShipmentView Map(ShipmentPackage x, string orderNumber) => new(x.Id, x.OrderId, orderNumber, x.ExternalPackageId, Wire(x.Status), x.RawStatus, x.CargoTrackingNumber, x.StatusOccurredAt, x.Version, x.CargoProviderExternalId, ShipmentPackageClassification.IsResend(x.CreatedBy, x.OriginExternalPackageId));
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
    private const string VerifyingCode = "EXTERNAL_EFFECT_VERIFYING";
    private const string VerifyingMessage = "Pazaryerinin yanıtı kesinleşmedi; sonuç birkaç saniye içinde pazaryerinden okunup panel güncellenecek. İşlemi yeniden göndermeyin.";

    private static ServiceResult<T> Verifying<T>() => ServiceResult<T>.Fail(VerifyingCode, VerifyingMessage, 409);

    // The marketplace's answer was unclear: the action is not sent again; a verification job
    // reads the marketplace and settles the effect (see MarketplaceJobProcessor.ReconcileExternalEffect).
    private async Task<ServiceResult<T>> VerifyLaterAsync<T>(ExternalEffectRecord effect, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dedup = ExternalEffectReconcilePolicy.JobDedupKey(effect.Id);
        if (!await db.IntegrationJobs.AnyAsync(x => x.TenantId == effect.TenantId && x.JobDedupKey == dedup, cancellationToken))
            db.IntegrationJobs.Add(ExternalEffectReconcilePolicy.NewJob(effect, now, now + ExternalEffectReconcilePolicy.FirstCheckDelay));
        await db.SaveChangesAsync(cancellationToken);
        return Verifying<T>();
    }

    private static async Task<ServiceResult<T>> QueueInsteadAsync<T>(Task<ServiceResult<Guid>> enqueue)
    {
        var queued = await enqueue;
        return queued.Error is { } error
            ? ServiceResult<T>.Fail(error.Code, error.Message, error.Status, error.FieldErrors)
            : ServiceResult<T>.Fail("ACTION_QUEUED", "Pazaryeri istek kotası şu an dolu; işlem sıraya alındı ve birkaç saniye içinde gönderilecek.", 409);
    }

    private static bool IsAmbiguous(AdapterError error) => error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug;
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
}
