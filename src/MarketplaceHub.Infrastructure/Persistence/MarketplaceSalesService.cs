using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class MarketplaceSalesService(AppDbContext db, CursorCodec cursors, IConfiguration configuration, IProductVisualLookupPort productVisuals, IOrderPort orders, IReturnPort returns, IPrivateFileStorage files, TimeProvider timeProvider) : IMarketplaceSalesService
{
    public async Task<PageResult<OrderListView>> OrdersAsync(Guid tenantId, int limit, int page, string? after, OrderListQuery queryOptions, CancellationToken cancellationToken)
    {
        // The panel is a local read model. Remote reads belong to the scheduled worker.
        var sort = NormalizeOrderSort(queryOptions.Sort);
        var query = db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")));
        query = ExcludeStaleUnpackagedHepsiburadaOrders(query, tenantId);
        ApplyOrderFilters(ref query, queryOptions, tenantId);
        // Count the filtered result set before applying the page cursor. Counting
        // after the cursor made the total shrink on every subsequent page and
        // caused the frontend to hide valid later pages.
        var totalCount = await query.CountAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!cursors.TryDecodeOrder(after, out var cursorSort, out var cursorDueAt, out var cursorOrderedAt, out var cursorId) || cursorSort != sort)
                throw new ArgumentException("Cursor geçersiz veya seçili sıralamayla uyuşmuyor.", nameof(after));
            if (sort is "DUE_ASC" or "DUE_DESC")
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
        IQueryable<Order> orderedQuery = sort switch
        {
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
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).OrderByDescending(x => x.StatusOccurredAt).ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var linesByOrder = lines.GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.ToList());
        var packagesByOrder = packages.GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.ToList());
        var invoicesByOrder = invoices.Where(x => x.OriginalInvoiceId == null).GroupBy(x => x.OrderId).ToDictionary(x => x.Key, x => x.First());
        var invoicesByPackage = invoices.Where(x => x.OriginalInvoiceId == null && x.PackageId != null).GroupBy(x => x.PackageId!.Value).ToDictionary(x => x.Key, x => x.First());
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var variantIds = lines.Where(x => x.VariantId is not null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var lineSkus = lines.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var lineSkuKeys = lines.Select(x => NormalizeCatalogKey(x.Sku, 160)).Where(x => x.Length > 0).Distinct().ToArray();
        var marketplaceSkuKeys = lines.Select(x => MarketplaceVariantLinkCoverage.Normalize(x.Sku)).Where(x => x.Length > 0).Distinct().ToArray();
        var lineBarcodes = lines.Select(x => x.Barcode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var lineBarcodeKeys = lines.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lineVariantLinks = marketplaceSkuKeys.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId
                    && connectionIds.Contains(x.ConnectionId)
                    && marketplaceSkuKeys.Contains(x.ExternalId.Trim().ToUpper()))
                .ToListAsync(cancellationToken);
        var linkedVariantIds = lineVariantLinks.Select(link => link.VariantId).Distinct().ToArray();
        var variantRows = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId &&
            (variantIds.Contains(x.Id) || linkedVariantIds.Contains(x.Id) || lineSkus.Contains(x.Sku) || lineSkuKeys.Contains(x.SkuNormalized) ||
             lineBarcodes.Contains(x.Barcode) || lineBarcodeKeys.Contains(x.BarcodeNormalized))).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variants = variantRows.ToDictionary(x => x.Id, x => x);
        var variantsBySku = variantRows.GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var variantsByBarcode = BarcodeVariantLookup(variantRows);
        var variantsByMarketplaceIdentity = lineVariantLinks
            .GroupBy(link => (link.ConnectionId, ExternalId: MarketplaceVariantLinkCoverage.Normalize(link.ExternalId)))
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
                orderPackages.Count,
                order.OrderedAt,
                now);
            var displayStatus = unverifiedWithoutPackage ? "UNVERIFIED" : order.DerivedStatus;
            var lineViews = orderLines.Select(x =>
            {
                var variant = ResolveOrderVariant(order, x);
                var source = SourceLine(x.SourceSnapshotJson);
                var imageUrl = string.IsNullOrWhiteSpace(source.ImageUrl) ? null : source.ImageUrl;
                var barcode = string.IsNullOrWhiteSpace(x.Barcode) ? variant?.Barcode : x.Barcode;
                return new OrderLineView(x.Id, x.Sku, barcode, x.TitleSnapshot, x.OrderedQuantity, x.CancelledQuantity, x.ShippedQuantity, x.DeliveredQuantity, x.ReturnedQuantity, x.UnitPrice, x.VatRate, x.RawStatus, x.VariantId ?? variant?.Id, variant?.ModelCode ?? source.ModelCode, variant?.OptionSignature ?? source.OptionSignature, imageUrl ?? (variant is null ? null : imageUrls.GetValueOrDefault(variant.Id)));
            }).ToList();
            var packageViews = orderPackages.Select(x => Map(x, order.OrderNumber)).ToList();
            return new OrderListView(
                order.Id, order.OrderNumber, displayStatus, order.Currency, order.NetAmount, order.OrderedAt,
                orderLines.Count, orderPackages.Count, order.Version,
                order.ConnectionId, connection?.PlatformCode ?? "TRENDYOL", connection?.DisplayName ?? "Trendyol",
                customer.Name, customer.OrderType, customer.IsMicroExport, dueAt,
                OpenOrderLifecyclePolicy.ShouldShowShipmentDeadlineWarning(connection?.PlatformCode, displayStatus, dueAt, now, unverifiedWithoutPackage), InvoiceLabelForPlatform(invoice, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, order.CustomerSnapshotJson, orderPackages.Select(x => x.RawStatus), connection?.PlatformCode),
                package?.CargoProviderExternalId ?? JsonText(order.CustomerSnapshotJson, "marketplaceCargoProviderName"), package?.CargoTrackingNumber,
                orderLines.Select(x => ResolveOrderVariant(order, x)).Where(x => x is not null).Select(x => imageUrls.GetValueOrDefault(x!.Id)).FirstOrDefault(x => x is not null),
                orderLines.Sum(x => OrderLinePresentationPolicy.ActiveQuantity(x.OrderedQuantity, x.CancelledQuantity)), customer.Email, customer.TaxOrIdentityNumber,
                order.ShipmentAddressSnapshotJson, order.InvoiceAddressSnapshotJson, order.GrossAmount, order.DiscountAmount,
                lineViews, packageViews, invoice?.Id, invoiceDocumentUrl);
        }).ToList();
        var hasMore = orders.Count > limit;
        var pageRows = rows.Take(limit).ToList();
        var last = orders.Take(limit).LastOrDefault();
        var next = hasMore && last is not null ? cursors.EncodeOrder(sort, last.ShipmentDueAt, last.OrderedAt, last.Id) : null;
        return new(pageRows, next, hasMore, totalCount);
    }

    internal void ApplyOrderFilters(ref IQueryable<Order> query, OrderListQuery options, Guid tenantId)
    {
        var resendCreators = new[] { "transfer", "resend", "replacement" };
        var invoice = options.Invoice?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(invoice) && invoice != "ALL")
        {
            query = invoice switch
            {
                "FATURA_KESILDI" => query.Where(x =>
                    db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                        && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                        && i.Status == InvoiceStatus.Completed)
                    || db.ShipmentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id
                        && package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Invoiced
                        // A stale marketplace "invoiced" snapshot must not mask a failed local invoice attempt.
                        && !db.Invoices.Any(i => i.TenantId == package.TenantId && i.OrderId == package.OrderId
                            && i.OriginalInvoiceId == null && (i.PackageId == package.Id || i.PackageId == null)
                            && (i.Status == InvoiceStatus.Rejected || i.Status == InvoiceStatus.ValidationFailed
                                || i.Status == InvoiceStatus.ManualReview || i.Status == InvoiceStatus.MarketplaceFailed)))),
                "FATURA_KONTROLDE" => query.Where(x =>
                    db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                        && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                        && (i.Status == InvoiceStatus.Submitted || i.Status == InvoiceStatus.Accepted || i.Status == InvoiceStatus.MarketplacePending))
                    || db.ShipmentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Received)),
                "FATURA_REDDEDILDI" => query.Where(x =>
                    db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                        && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                        && (i.Status == InvoiceStatus.Rejected || i.Status == InvoiceStatus.ValidationFailed || i.Status == InvoiceStatus.ManualReview))
                    || db.ShipmentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Rejected)),
                "FATURA_IPTAL" => query.Where(x => db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && (i.Status == InvoiceStatus.Cancelled || i.Status == InvoiceStatus.CancelledLocal))),
                "FATURA_BEKLIYOR" => query.Where(x =>
                    db.ShipmentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.NotInvoiced)
                    || x.CustomerSnapshotJson.Contains("NOTINVOICED") || x.CustomerSnapshotJson.Contains("NOT_INVOICED")),
                "FATURA_ISLENIYOR" => query.Where(x => db.Invoices.Any(i => i.TenantId == x.TenantId && i.OrderId == x.Id && i.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == i.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                    && i.Status != InvoiceStatus.Completed && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.CancelledLocal && i.Status != InvoiceStatus.Rejected && i.Status != InvoiceStatus.ValidationFailed && i.Status != InvoiceStatus.ManualReview)),
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
        if (!string.IsNullOrWhiteSpace(status) && status != "ALL")
        {
            var derivedStatuses = DerivedStatusesForOrderTab(status);
            var packageStatuses = PackageStatusesForOrderTab(status);
            query = status == "CANCELLED"
                ? query.Where(order => order.DerivedStatus == "CANCELLED"
                    || (!db.PlatformConnections.Any(connection => connection.TenantId == order.TenantId && connection.Id == order.ConnectionId && connection.PlatformCode == "SHOPIFY")
                        && db.ShipmentPackages.Any(package => package.TenantId == order.TenantId && package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Cancelled)))
                : status == "UNVERIFIED"
                ? query.Where(_ => false)
                : derivedStatuses is not null
                ? query.Where(order => derivedStatuses.Contains(order.DerivedStatus))
                : packageStatuses is not null
                ? status switch
                {
                    "NEW" => query.Where(order => db.ShipmentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && packageStatuses.Contains(package.Status))
                        || HepsiburadaUnpackagedNewOrders(tenantId).Any(newOrder => newOrder.Id == order.Id)),
                    "ON_HOLD" => query.Where(order => db.ShipmentPackages.Any(package => package.TenantId == order.TenantId
                            && package.OrderId == order.Id
                            && (packageStatuses.Contains(package.Status)
                                || package.Status == ShipmentPackageStatus.Undelivered
                                    && db.PlatformConnections.Any(connection => connection.TenantId == package.TenantId
                                        && connection.Id == package.ConnectionId
                                        && connection.PlatformCode == "HEPSIBURADA")))
                        || HepsiburadaUnpackagedOnHoldOrders(tenantId).Any(holdOrder => holdOrder.Id == order.Id)),
                    "DELIVERED" => query.Where(order =>
                        db.PlatformConnections.Any(connection => connection.TenantId == order.TenantId
                            && connection.Id == order.ConnectionId
                            && connection.PlatformCode == "SHOPIFY")
                        && db.OrderStatusHistory.Any(history => history.TenantId == order.TenantId
                            && history.OrderId == order.Id
                            && history.RawStatus.StartsWith("MANUAL_SHOPIFY_STATUS:")
                            && history.CanonicalStatus == order.DerivedStatus
                            && history.OccurredAt >= order.LastRemoteModifiedAt)
                            ? order.DerivedStatus == "DELIVERED"
                            : db.ShipmentPackages.Any(package => package.TenantId == order.TenantId
                                && package.OrderId == order.Id
                                && packageStatuses.Contains(package.Status))
                                || HepsiburadaUnpackagedDeliveredOrders(tenantId).Any(deliveredOrder => deliveredOrder.Id == order.Id)),
                    "SHIPPED" => query.Where(order =>
                        db.PlatformConnections.Any(connection => connection.TenantId == order.TenantId
                            && connection.Id == order.ConnectionId
                            && connection.PlatformCode == "SHOPIFY")
                        && db.OrderStatusHistory.Any(history => history.TenantId == order.TenantId
                            && history.OrderId == order.Id
                            && history.RawStatus.StartsWith("MANUAL_SHOPIFY_STATUS:")
                            && history.CanonicalStatus == order.DerivedStatus
                            && history.OccurredAt >= order.LastRemoteModifiedAt)
                            ? order.DerivedStatus == "SHIPPED"
                            : db.ShipmentPackages.Any(package => package.TenantId == order.TenantId
                                && package.OrderId == order.Id
                                && packageStatuses.Contains(package.Status)
                                && (package.Status != ShipmentPackageStatus.Undelivered
                                    || !db.PlatformConnections.Any(connection => connection.TenantId == package.TenantId
                                        && connection.Id == package.ConnectionId
                                        && connection.PlatformCode == "HEPSIBURADA")))),
                    _ => query.Where(order => db.ShipmentPackages.Any(package => package.TenantId == order.TenantId
                        && package.OrderId == order.Id
                        && packageStatuses.Contains(package.Status)))
                }
                : status switch
                {
                    // originPackageIds is also present for split/cancel packages;
                    // only Trendyol's explicit creator marker identifies a resend.
                    "RESENT" => query.Where(x => db.ShipmentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.OriginExternalPackageId != null && package.Status != ShipmentPackageStatus.Cancelled && package.CreatedBy != null && resendCreators.Contains(package.CreatedBy))),
                    _ => query.Where(x => x.DerivedStatus == status)
                };
        }

        var search = options.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x => x.OrderNumber.Contains(search) || x.ExternalOrderId.Contains(search) || x.CustomerSnapshotJson.Contains(search)
                || db.OrderLines.Any(line => line.TenantId == x.TenantId && line.OrderId == x.Id
                    && (line.TitleSnapshot.Contains(search) || line.Sku.Contains(search) || (line.Barcode != null && line.Barcode.Contains(search)))));
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
            query = query.Where(x => db.ShipmentPackages.Any(package => package.TenantId == x.TenantId && package.OrderId == x.Id && package.CargoProviderExternalId == cargo));

        if (options.DateFrom is { } dateFrom) query = query.Where(x => x.OrderedAt >= dateFrom);
        if (options.DateTo is { } dateTo) query = query.Where(x => x.OrderedAt <= dateTo);
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
        _ => null
    };

    internal static string[]? DerivedStatusesForOrderTab(string status) => status switch
    {
        "PENDING" => DashboardMetricPolicy.PendingOrderStatuses,
        _ => null
    };

    private static string NormalizeOrderSort(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "DATE_ASC" => "DATE_ASC",
        "DUE_ASC" => "DUE_ASC",
        "DUE_DESC" => "DUE_DESC",
        _ => "DATE_DESC"
    };

    private static string NormalizeCatalogKey(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var form = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(form.Length);
        foreach (var ch in form)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToUpperInvariant(ch));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var normalized = builder.ToString().Trim('-');
        return normalized[..Math.Min(maximum, normalized.Length)];
    }

    internal static string[] CatalogLookupKeys(string? value, int maximum = 160)
    {
        var normalized = NormalizeCatalogKey(value, maximum);
        if (normalized.Length == 0) return [];
        var keys = new List<string> { normalized };
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

    public async Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, string? platform, CancellationToken cancellationToken)
    {
        // Marketplace status tabs are package-based. Counting Orders here made
        // split packages and the provider's package counters incomparable.
        var resendCreators = new[] { "transfer", "resend", "replacement" };
        var manuallyOverriddenShopifyOrders = CurrentManualShopifyOrderStatuses(tenantId);
        var packages = db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                && !db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && connection.PlatformCode == "SHOPIFY"
                    && x.ExternalPackageId.StartsWith("order:") && x.ExternalPackageId.EndsWith(":remainder")));
        var platformCode = platform?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(platformCode) && platformCode != "ALL")
            packages = packages.Where(x => db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED") && connection.PlatformCode == platformCode));

        var summary = await packages
            .GroupBy(_ => 1)
            .Select(group => new OrderSummaryView(
                group.Count(),
                group.Count(x => x.Status == ShipmentPackageStatus.New),
                group.Count(x => x.Status == ShipmentPackageStatus.Processing || x.Status == ShipmentPackageStatus.ReadyToShip),
                group.Count(x => manuallyOverriddenShopifyOrders.Any(order => order.Id == x.OrderId)
                    ? manuallyOverriddenShopifyOrders.Any(order => order.Id == x.OrderId && order.DerivedStatus == "SHIPPED")
                    : x.Status == ShipmentPackageStatus.Shipped
                        || x.Status == ShipmentPackageStatus.Undelivered
                            && !db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId
                                && connection.Id == x.ConnectionId
                                && connection.PlatformCode == "HEPSIBURADA")),
                group.Count(x => manuallyOverriddenShopifyOrders.Any(order => order.Id == x.OrderId)
                    ? manuallyOverriddenShopifyOrders.Any(order => order.Id == x.OrderId && order.DerivedStatus == "DELIVERED")
                    : x.Status == ShipmentPackageStatus.Delivered),
                group.Count(x => x.OriginExternalPackageId != null && x.Status != ShipmentPackageStatus.Cancelled && x.CreatedBy != null && resendCreators.Contains(x.CreatedBy)),
                group.Count(x => x.Status == ShipmentPackageStatus.OnHold
                    || x.Status == ShipmentPackageStatus.Undelivered
                        && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId
                            && connection.Id == x.ConnectionId
                            && connection.PlatformCode == "HEPSIBURADA")),
                group.Count(x => x.Status == ShipmentPackageStatus.Cancelled
                    && (!db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == x.ConnectionId && connection.PlatformCode == "SHOPIFY")
                        || db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && order.DerivedStatus == "CANCELLED"))),
                group.Count(x => x.Status == ShipmentPackageStatus.Returned),
                group.Count(x => x.Status == ShipmentPackageStatus.ReturnInTransit),
                group.Count(x => x.Status == ShipmentPackageStatus.PartiallyCancelled),
                group.Count(x => x.Status == ShipmentPackageStatus.ManualReview)))
            .SingleOrDefaultAsync(cancellationToken) ?? new OrderSummaryView(0, 0, 0, 0, 0, 0, 0);

        var pendingOrders = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId
            && DashboardMetricPolicy.PendingOrderStatuses.Contains(order.DerivedStatus)
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == order.ConnectionId
                && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")));
        pendingOrders = ExcludeStaleUnpackagedHepsiburadaOrders(pendingOrders, tenantId);
        if (!string.IsNullOrWhiteSpace(platformCode) && platformCode != "ALL")
            pendingOrders = pendingOrders.Where(order => db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                && connection.Id == order.ConnectionId && connection.PlatformCode == platformCode
                && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")));

        var unpackagedNewOrderCount = platformCode is null or "" or "ALL" or "HEPSIBURADA"
            ? await HepsiburadaUnpackagedNewOrders(tenantId).CountAsync(cancellationToken)
            : 0;
        var unpackagedOnHoldOrderCount = platformCode is null or "" or "ALL" or "HEPSIBURADA"
            ? await HepsiburadaUnpackagedOnHoldOrders(tenantId).CountAsync(cancellationToken)
            : 0;
        var unpackagedDeliveredOrderCount = platformCode is null or "" or "ALL" or "HEPSIBURADA"
            ? await HepsiburadaUnpackagedDeliveredOrders(tenantId).CountAsync(cancellationToken)
            : 0;
        const int unverifiedOrderCount = 0;
        return summary with
        {
            All = summary.All + unpackagedNewOrderCount + unpackagedOnHoldOrderCount + unpackagedDeliveredOrderCount + unverifiedOrderCount,
            New = summary.New + unpackagedNewOrderCount,
            OnHold = summary.OnHold + unpackagedOnHoldOrderCount,
            Delivered = summary.Delivered + unpackagedDeliveredOrderCount,
            Pending = await pendingOrders.CountAsync(cancellationToken),
            Unverified = unverifiedOrderCount
        };
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
                && !db.ShipmentPackages.Any(package => package.TenantId == tenantId && package.OrderId == order.Id));
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
                && !db.ShipmentPackages.Any(package => package.TenantId == tenantId && package.OrderId == order.Id));
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
                && !db.ShipmentPackages.Any(package => package.TenantId == tenantId && package.OrderId == order.Id));
    }

    internal IQueryable<Order> ExcludeStaleUnpackagedHepsiburadaOrders(IQueryable<Order> query, Guid tenantId)
    {
        return query.ExcludeStaleUnpackaged(db, tenantId, timeProvider.GetUtcNow());
    }

    internal IQueryable<ReturnClaim> ExcludeStaleHepsiburadaReturns(IQueryable<ReturnClaim> query, Guid tenantId)
    {
        var anchor = timeProvider.GetUtcNow();
        var lookbackStart = HepsiburadaReturnHistoryPolicy.InitialStatusChangeStart(anchor);
        return query.Where(claim => !db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                && connection.Id == claim.ConnectionId
                && connection.PlatformCode == "HEPSIBURADA")
            || db.Orders.Any(order => order.TenantId == tenantId
                && order.Id == claim.OrderId
                && order.OrderedAt >= lookbackStart
                && order.OrderedAt <= anchor));
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
        var allocationRows = await db.PackageLineAllocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && activeLineIds.Contains(x.OrderLineId))
            .Select(x => new { x.OrderLineId, x.AllocatedQuantity, x.CancelledQuantity })
            .ToListAsync(cancellationToken);
        var allocatedQuantities = allocationRows.GroupBy(row => row.OrderLineId)
            .ToDictionary(group => group.Key, group => group.Sum(row => Math.Max(0m, row.AllocatedQuantity - row.CancelledQuantity)));
        var variantIds = activeOrderLines.Where(x => x.VariantId is not null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var lineSkus = activeOrderLines.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var lineSkuKeys = activeOrderLines.Select(x => NormalizeCatalogKey(x.Sku, 160)).Where(x => x.Length > 0).Distinct().ToArray();
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
            return new OrderLineView(x.Id, x.Sku, x.Barcode, x.TitleSnapshot, x.OrderedQuantity, x.CancelledQuantity, x.ShippedQuantity, x.DeliveredQuantity, x.ReturnedQuantity, x.UnitPrice, x.VatRate, x.RawStatus, x.VariantId, variant?.ModelCode ?? source.ModelCode, variant?.OptionSignature ?? source.OptionSignature, source.ImageUrl ?? (variant is null ? null : imageUrls.GetValueOrDefault(variant.Id)), allocatedQuantities.GetValueOrDefault(x.Id));
        }).ToList();
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == order.ConnectionId, cancellationToken);
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
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
            order.ShipmentAddressSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentDueAt ?? OperationalDueAt(order.CustomerSnapshotJson), InvoiceLabelForPlatform(invoice, displayPackage?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, order.CustomerSnapshotJson, packages.Select(x => x.RawStatus), connection?.PlatformCode),
            customer.Phone, customer.IsEInvoiceAvailable, invoiceDocumentUrl));
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
            var linkedVariantId = connection is null
                ? null
                : await db.MarketplaceVariantLinks.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId == connection.Id
                        && lookupKeys.Contains(x.ExternalId.Trim().ToUpper()))
                    .Select(x => (Guid?)x.VariantId)
                    .SingleOrDefaultAsync(cancellationToken);
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

    public async Task<PageResult<ShipmentView>> ShipmentsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0); if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ShipmentPackageStatus>(status, true, out var parsed)) query = query.Where(x => x.Status == parsed);
        var rows = await (from package in query orderby package.Id join order in db.Orders.AsNoTracking() on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id } select new { Package = package, order.OrderNumber }).Take(limit + 1).ToListAsync(cancellationToken);
        return Page(rows.Select(x => Map(x.Package, x.OrderNumber)).ToList(), limit, x => x.Id);
    }

    public async Task<ServiceResult<ShipmentDetailView>> ShipmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var row = await (from package in db.ShipmentPackages.AsNoTracking() where package.TenantId == tenantId && package.Id == id && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == package.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")) join order in db.Orders.AsNoTracking() on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id } join connection in db.PlatformConnections.AsNoTracking() on new { package.TenantId, ConnectionId = package.ConnectionId } equals new { connection.TenantId, ConnectionId = connection.Id } select new { Package = package, order.OrderNumber, connection.PlatformCode }).SingleOrDefaultAsync(cancellationToken); if (row is null) return NotFound<ShipmentDetailView>();
        var stage = await IsStageConnection(tenantId, row.Package.ConnectionId, cancellationToken);
        var actions = stage
            ? row.PlatformCode == "HEPSIBURADA" ? HepsiburadaShipmentActionsFor(row.Package) : ShipmentActions
            : await CapabilityValues(tenantId, row.Package.ConnectionId, MarketplaceCapabilities.ShipmentWrite, "allowedActions", cancellationToken);
        if (!stage && row.PlatformCode == "HEPSIBURADA")
        {
            var evidencedActions = actions;
            actions = HepsiburadaShipmentActionsFor(row.Package)
                .Where(action => evidencedActions.Contains(action, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
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
        var allocationRows = await db.PackageLineAllocations.AsNoTracking().Where(x => x.TenantId == tenantId && selectedIds.Contains(x.OrderLineId))
            .Select(x => new { x.OrderLineId, x.AllocatedQuantity, x.CancelledQuantity }).ToListAsync(cancellationToken);
        var allocations = allocationRows.GroupBy(item => item.OrderLineId).ToDictionary(group => group.Key, group => group.Sum(item => Math.Max(0m, item.AllocatedQuantity - item.CancelledQuantity)));
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

        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectPrefix, IdempotencyKey = effectPrefix, CreatedAt = timeProvider.GetUtcNow() };
        db.ExternalEffectRecords.Add(effect);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Bu paket isteği başka bir işlemde ele alınıyor; sipariş durumunu kontrol edin.", 409);
        }

        var result = await orders.CreateOrderPackageAsync(
            new AdapterContext(tenantId, connection.Id, correlationId, normalizedKey, timeProvider.GetUtcNow().AddSeconds(30)),
            new(command.Barcode.Trim(), command.CargoCompany.Trim(), command.Carrier.Trim(), command.CreationReason.Trim(), command.Deci, command.ParcelQuantity,
                command.ShippingAddressLabel.Trim(), command.ShippingModel.Trim(), remoteLines),
            cancellationToken);
        if (!result.IsSuccess)
        {
            var error = result.Error;
            if (error is null || IsAmbiguous(error)) return ServiceResult<CreateOrderPackageResult>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", error?.SafeMessage ?? "Hepsiburada paket oluşturma isteğine yanıt vermedi; tekrar gönderim engellendi.", 409);
            db.ExternalEffectRecords.Remove(effect);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<CreateOrderPackageResult>.Fail(error.Code, error.SafeMessage, error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502);
        }

        effect.EffectType = $"{effectPrefix}:{Uri.EscapeDataString(result.Value!.PackageNumber)}";
        effect.IdempotencyKey = normalizedKey;
        effect.CompletedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        _ = await EnqueueOrderSyncAsync(tenantId, connection.Id, order.OrderNumber, false, correlationId, cancellationToken);
        return ServiceResult<CreateOrderPackageResult>.Ok(result.Value!);
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
        updateExistingPricesAndStock = updateExistingPricesAndStock && (full || existingOnly) && !newOnly && !mappingOnly && !optionsOnly && !string.Equals(platform, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var type = MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.ProductSync);
        return await EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ProductRead, type, JsonSerializer.Serialize(new { connectionId, full, newOnly, existingOnly, mappingOnly, optionsOnly, includeArchived, includeDrafts, includePendingApproval, updateExistingProducts, updateExistingPricesAndStock, productLookup }), correlationId, cancellationToken);
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
        if (action == "PICKING" && IsEmptyJsonObject(commandPayload))
        {
            var packageLines = await (from allocation in db.PackageLineAllocations.AsNoTracking()
                                      where allocation.TenantId == tenantId && allocation.PackageId == package.Id && allocation.AllocatedQuantity > 0
                                      join line in db.OrderLines.AsNoTracking() on new { allocation.TenantId, Id = allocation.OrderLineId } equals new { line.TenantId, line.Id }
                                      select new { line.ExternalLineId, allocation.AllocatedQuantity }).ToListAsync(cancellationToken);
            if (packageLines.Count == 0 || packageLines.Any(line => !long.TryParse(line.ExternalLineId, out _) || line.AllocatedQuantity != decimal.Truncate(line.AllocatedQuantity) || line.AllocatedQuantity > int.MaxValue))
                return ServiceResult<Guid>.Fail("PICKING_LINES_REQUIRED", "Paket satırları Trendyol Picking isteği için hazırlanamadı.", 422);
            commandPayload = JsonSerializer.Serialize(new { lines = packageLines.Select(line => new { lineId = long.Parse(line.ExternalLineId), quantity = (int)line.AllocatedQuantity }), @params = new { }, status = "Picking" });
        }
        var validation = platform == "HEPSIBURADA"
            ? ValidateHepsiburadaShipmentAction(package, action, commandPayload)
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
        const string effectType = "TRENDYOL_INSTANT_PICKING";
        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null) return NotFound<ShipmentView>();
        if (package.Version != expectedVersion) return Precondition<ShipmentView>(package.Version);
        if (package.Status != ShipmentPackageStatus.New) return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_NOT_ALLOWED", "Yalnız yeni durumdaki paketler işleme alınabilir.", 409);

        var packageLines = await (from allocation in db.PackageLineAllocations.AsNoTracking()
                                  where allocation.TenantId == tenantId && allocation.PackageId == package.Id && allocation.AllocatedQuantity > 0
                                  join line in db.OrderLines.AsNoTracking() on new { allocation.TenantId, Id = allocation.OrderLineId } equals new { line.TenantId, line.Id }
                                  select new { line.ExternalLineId, allocation.AllocatedQuantity }).ToListAsync(cancellationToken);
        if (packageLines.Count == 0 || packageLines.Any(line => !long.TryParse(line.ExternalLineId, out _) || line.AllocatedQuantity != decimal.Truncate(line.AllocatedQuantity) || line.AllocatedQuantity > int.MaxValue))
            return ServiceResult<ShipmentView>.Fail("PICKING_LINES_REQUIRED", "Paket satırları Trendyol Picking isteği için hazırlanamadı.", 422);
        var payload = JsonSerializer.Serialize(new { lines = packageLines.Select(line => new { lineId = long.Parse(line.ExternalLineId), quantity = (int)line.AllocatedQuantity }), @params = new { }, status = "Picking" });
        var validation = ValidateShipmentAction(package, action, payload);
        if (validation is not null) return ServiceResult<ShipmentView>.Fail(validation.Code, validation.Message, validation.Status, validation.FieldErrors);
        var stage = await IsStageConnection(tenantId, package.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("ENVIRONMENT_INVALID", "Shipment işlemi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, package.ConnectionId, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (!await ExternalWritePolicyEnabledAsync(tenantId, package.ConnectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) return ServiceResult<ShipmentView>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı.", 422);
        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.OrderId, cancellationToken);
        if (order is null) return NotFound<ShipmentView>();
        var orderNumber = order.OrderNumber;

        var normalizedKey = idempotencyKey.Trim();
        var existing = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.CompletedAt is null) return ServiceResult<ShipmentView>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki işleme alma sonucunun kesinleşmesi bekleniyor.", 409);
            return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
        }
        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = normalizedKey, CreatedAt = timeProvider.GetUtcNow() };
        db.ExternalEffectRecords.Add(effect);
        await db.SaveChangesAsync(cancellationToken);
        var result = await orders.ExecutePackageActionAsync(new AdapterContext(tenantId, package.ConnectionId, correlationId, normalizedKey, timeProvider.GetUtcNow().AddSeconds(30)), new(package.ExternalPackageId, action, payload), cancellationToken);
        if (!result.IsSuccess)
        {
            var error = result.Error;
            if (error is null || IsAmbiguous(error)) return ServiceResult<ShipmentView>.Fail(error is null ? "PICKING_FAILED" : "EXTERNAL_EFFECT_AMBIGUOUS", error?.SafeMessage ?? "Trendyol işleme alma isteğine yanıt vermedi; paneldeki mevcut bilgi korundu.", error is null ? 502 : 409);
            db.ExternalEffectRecords.Remove(effect);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<ShipmentView>.Fail("PICKING_FAILED", error.SafeMessage, error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502);
        }

        package.Status = ShipmentPackageStatus.Processing;
        package.RawStatus = "Processing";
        package.StatusOccurredAt = timeProvider.GetUtcNow();
        package.UpdatedAt = package.StatusOccurredAt;
        package.Version++;
        // Orders are filtered by DerivedStatus while the counters are calculated
        // from shipment packages. Keep both read models in sync before returning
        // so the order moves between tabs in the same response.
        var packageStatuses = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == order.Id)
            .Select(x => new { x.Id, x.Status })
            .ToListAsync(cancellationToken);
        var acceptedStatuses = packageStatuses.ToDictionary(x => x.Id, x => x.Status);
        acceptedStatuses[package.Id] = package.Status;
        var derivedStatus = Wire(ShipmentPackageStatusPolicy.Aggregate(acceptedStatuses.Values));
        if (!string.Equals(order.DerivedStatus, derivedStatus, StringComparison.Ordinal))
        {
            order.DerivedStatus = derivedStatus;
            order.UpdatedAt = package.UpdatedAt;
            order.Version++;
        }
        effect.CompletedAt = package.StatusOccurredAt;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
    }

    public async Task<ServiceResult<ShipmentView>> ChangeCargoProviderInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        const string effectType = "TRENDYOL_INSTANT_CARGO_PROVIDER_CHANGE";
        var action = command.Action.Trim().ToUpperInvariant();
        if (action != "CHANGE_CARGO_PROVIDER") return ServiceResult<ShipmentView>.Fail("SHIPMENT_ACTION_UNSUPPORTED", "Bu anlık endpoint yalnız kargo firması değişikliği için kullanılabilir.", 422);

        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null) return NotFound<ShipmentView>();
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

        var normalizedKey = idempotencyKey.Trim();
        var existing = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.CompletedAt is null)
                return ServiceResult<ShipmentView>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki kargo firması değişikliğinin sonucu kesinleştirilemedi; paneldeki mevcut bilgi korundu.", 409);

            if (!string.Equals(package.CargoProviderExternalId, cargoProvider, StringComparison.OrdinalIgnoreCase))
            {
                package.CargoProviderExternalId = cargoProvider;
                package.UpdatedAt = timeProvider.GetUtcNow();
                package.Version++;
                await db.SaveChangesAsync(cancellationToken);
            }
            return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
        }

        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = normalizedKey, CreatedAt = timeProvider.GetUtcNow() };
        db.ExternalEffectRecords.Add(effect);
        await db.SaveChangesAsync(cancellationToken);

        var result = await orders.ExecutePackageActionAsync(
            new AdapterContext(tenantId, package.ConnectionId, correlationId, normalizedKey, timeProvider.GetUtcNow().AddSeconds(30)),
            new(package.ExternalPackageId, action, command.PayloadJson),
            cancellationToken);
        if (!result.IsSuccess)
        {
            var error = result.Error;
            if (error is null) return ServiceResult<ShipmentView>.Fail("CARGO_PROVIDER_CHANGE_FAILED", "Trendyol kargo firması değişikliğine yanıt vermedi; paneldeki mevcut bilgi korundu.", 502);
            if (IsAmbiguous(error))
                return ServiceResult<ShipmentView>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", "Trendyol’a gönderilen kargo firması değişikliğinin sonucu kesinleştirilemedi; paneldeki mevcut bilgi korundu.", 409);

            db.ExternalEffectRecords.Remove(effect);
            await db.SaveChangesAsync(cancellationToken);
            var status = error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502;
            return ServiceResult<ShipmentView>.Fail("CARGO_PROVIDER_CHANGE_FAILED", error.SafeMessage, status);
        }

        package.CargoProviderExternalId = cargoProvider;
        package.UpdatedAt = timeProvider.GetUtcNow();
        package.Version++;
        effect.CompletedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ShipmentView>.Ok(Map(package, orderNumber));
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
        if (connection.PlatformCode == "HEPSIBURADA")
        {
            barcode = await (from listing in db.ChannelListingVariants.AsNoTracking()
                             join profile in db.ChannelListingProfiles.AsNoTracking() on new { listing.TenantId, ProfileId = listing.ProfileId } equals new { profile.TenantId, ProfileId = profile.Id }
                             join state in db.MarketplaceListingStates.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { state.TenantId, state.ConnectionId, state.VariantId }
                             join link in db.MarketplaceVariantLinks.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { link.TenantId, link.ConnectionId, link.VariantId }
                             where listing.TenantId == tenantId && profile.ConnectionId == connectionId && profile.Enabled && state.ActualStatus == "LIVE"
                                   && listing.ExternalBarcode != null && !string.IsNullOrWhiteSpace(link.ExternalId)
                             orderby listing.VariantId
                             select listing.ExternalBarcode!).FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(barcode)) return ServiceResult<Guid>.Fail("HEPSIBURADA_STAGE_LISTING_REQUIRED", "Test siparişi için Stage ortamında bağlı Hepsiburada SKU’su bulunan LIVE bir ürün gerekir.", 409);
        }
        var normalizedKey = idempotencyKey.Trim();
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.StageTestOrder && x.EffectIdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var now = timeProvider.GetUtcNow(); var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new StageTestOrderJobPayload(jobId, actorUserId, barcode, now));
        var job = NewJob(tenantId, connectionId, MarketplaceJobTypes.StageTestOrder, $"stage-test-order:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}", payload, correlationId);
        job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.MaxAttempts = 1; db.IntegrationJobs.Add(job);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = actorUserId, Action = "STAGE_TEST_ORDER_ENQUEUED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = $"official-stage-test-order-fixture:{connection.PlatformCode}", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(jobId);
    }

    public async Task<PageResult<ReturnListView>> ReturnsAsync(Guid tenantId, int limit, string? after, ReturnListQuery options, bool latest, CancellationToken cancellationToken)
    {
        var hasOperationalReturnConnection = await db.PlatformConnections.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId
                && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA")
                && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (!hasOperationalReturnConnection) return new([], null, false);

        var query = db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId
            && x.Status != ReturnClaimStatus.Cancelled
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")));
        query = ExcludeStaleHepsiburadaReturns(query, tenantId);
        ApplyReturnFilters(ref query, options);
        var totalCount = await query.CountAsync(cancellationToken);
        var afterId = latest ? Guid.Empty : Decode(after);
        if (!latest && afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0);
        var claims = latest
            ? await query.OrderByDescending(x => x.LastRemoteModifiedAt).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken)
            : await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
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
                    && returnLineSkuKeys.Contains(x.ExternalId.Trim().ToUpper()))
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
            .GroupBy(link => (link.ConnectionId, ExternalId: MarketplaceVariantLinkCoverage.Normalize(link.ExternalId)))
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
                return new OrderLineView(line.Id, line.Sku, line.Barcode ?? variant?.Barcode, line.TitleSnapshot, returnLine.Quantity, line.CancelledQuantity, line.ShippedQuantity, line.DeliveredQuantity, line.ReturnedQuantity, line.UnitPrice, line.VatRate, line.RawStatus, line.VariantId ?? variant?.Id, ReturnLineModelCode(variant, source.ModelCode), variant?.OptionSignature ?? source.OptionSignature, source.ImageUrl ?? (variant is null ? null : returnImageUrls.GetValueOrDefault(variant.Id)));
            }).Where(line => line is not null).Select(line => line!).ToList();
            return new ReturnListView(claim.Id, claim.ExternalClaimId, order?.OrderNumber ?? "—", Wire(claim.Status), claim.RawStatus, claim.ReasonText, ReturnActionDueAt(platformCode, claim.RawStatus, claim.ActionDueAt, claim.LastRemoteModifiedAt), claim.Version,
                order is null ? "—" : Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).Name,
                order?.OrderedAt, order?.NetAmount ?? 0, order?.Currency ?? "TRY", claim.CargoProviderName, claim.CargoTrackingNumber, image, claimLines.Count, firstLine?.Barcode ?? firstVariant?.Barcode,
                lineViews, package?.ExternalPackageId, order is null ? "FATURA_BEKLIYOR" : ReturnInvoiceLabel(invoice, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, order.CustomerSnapshotJson, package is null ? [] : [package.RawStatus]), order?.GrossAmount ?? 0, order?.DiscountAmount ?? 0,
                order is not null && Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).IsMicroExport,
                claimConnection?.Id ?? connection?.Id, platformCode, claimConnection?.DisplayName ?? connection?.DisplayName ?? "Trendyol", outboundPackage?.CargoProviderExternalId, outboundPackage?.CargoTrackingNumber, claim.ReasonCode, approvedAt, claim.CargoTrackingLink);
        }).ToList();
        var hasMore = rows.Count > limit;
        var pageRows = rows.Take(limit).ToList();
        var next = !latest && hasMore && pageRows.Count > 0 ? cursors.Encode(pageRows[^1].Id) : null;
        return new(pageRows, next, hasMore, totalCount);
    }

    internal static string? ReturnLineModelCode(ProductVariant? variant, string? sourceModelCode) =>
        string.IsNullOrWhiteSpace(variant?.ModelCode) ? sourceModelCode : variant.ModelCode;

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
                "APPROVED" => query.Where(x => x.Status == ReturnClaimStatus.Approved || x.Status == ReturnClaimStatus.Completed),
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
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(claim => claim.ExternalClaimId.Contains(search)
                || (claim.ReasonCode != null && claim.ReasonCode.Contains(search))
                || (claim.ReasonText != null && claim.ReasonText.Contains(search))
                || db.Orders.Any(order => order.TenantId == claim.TenantId && order.Id == claim.OrderId
                    && (order.OrderNumber.Contains(search) || order.CustomerSnapshotJson.Contains(search)))
                || db.ReturnLines.Any(returnLine => returnLine.TenantId == claim.TenantId && returnLine.ClaimId == claim.Id
                    && db.OrderLines.Any(line => line.TenantId == returnLine.TenantId && line.Id == returnLine.OrderLineId
                        && ((line.Barcode != null && line.Barcode.Contains(search)) || line.Sku.Contains(search)))));

        var customer = options.Customer?.Trim();
        if (!string.IsNullOrWhiteSpace(customer))
            query = query.Where(x => db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && (order.CustomerSnapshotJson.Contains(customer) || order.OrderNumber.Contains(customer))));
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
        if (options.DateFrom is { } dateFrom) query = query.Where(x => db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && order.OrderedAt >= dateFrom));
        if (options.DateTo is { } dateTo) query = query.Where(x => db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && order.OrderedAt <= dateTo));
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
        var pendingDecision = await db.ReturnDecisions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id && (x.Status == "PENDING" || x.Status == "RETRY_SCHEDULED" || x.Status == "MANUAL_REVIEW" || (x.Status == "SUBMITTED" && db.ExternalEffectRecords.Any(effect => effect.TenantId == x.TenantId && effect.IdempotencyKey == x.IdempotencyKey))))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var decisionPending = pendingDecision != Guid.Empty;
        var actions = claim.Status switch
        {
            ReturnClaimStatus.Requested when awaitingPreApproval => HepsiburadaReturnActionPolicy.AllowedActions(claim.RawStatus, decisionPending),
            ReturnClaimStatus.Requested or ReturnClaimStatus.InTransit => ["RECEIVE"],
            ReturnClaimStatus.AwaitingShipment when isHepsiburada => Array.Empty<string>(),
            ReturnClaimStatus.ActionRequired when isHepsiburada => HepsiburadaReturnActionPolicy.AllowedActions(claim.RawStatus, decisionPending),
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
        var sourceLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var orderLineIds = sourceLines.Select(x => x.OrderLineId).ToArray();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var dispositions = await db.ReturnStockDispositions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == id).GroupBy(x => x.ReturnLineId).Select(x => new { ReturnLineId = x.Key, Quantity = x.Sum(y => y.Quantity) }).ToDictionaryAsync(x => x.ReturnLineId, x => x.Quantity, cancellationToken);
        var lineSkuKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lineBarcodeKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var variantLinks = lineSkuKeys.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == claim.ConnectionId && lineSkuKeys.Contains(x.ExternalId.Trim().ToUpper()))
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
            .GroupBy(x => MarketplaceVariantLinkCoverage.Normalize(x.ExternalId), StringComparer.Ordinal)
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
                variant?.ModelCode ?? sourceSnapshot?.ModelCode);
        }).ToList();
        var package = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderByDescending(x => x.StatusOccurredAt).FirstOrDefaultAsync(cancellationToken);
        var customer = Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
        return ServiceResult<ReturnDetailView>.Ok(new(claim.Id, claim.ExternalClaimId, order.OrderNumber, Wire(claim.Status), claim.RawStatus, claim.ReasonCode, claim.ReasonText, ReturnActionDueAt(platformCode, claim.RawStatus, claim.ActionDueAt, claim.LastRemoteModifiedAt), actions, claim.Version,
            customer.Name, order.OrderedAt, order.NetAmount, order.Currency, claim.CargoProviderName, claim.CargoTrackingNumber, lines, claim.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed, approvedAt, externalWritesEnabled, decisionPending, platformCode, claim.CargoTrackingLink));
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
        if (preApprovalConfirm && !awaitingPreApproval) return ServiceResult<ReturnDetailView>.Fail("HEPSIBURADA_PREAPPROVAL_ACTION_NOT_ALLOWED", "İnceleme için geri gönderim yalnız Hepsiburada AwaitingPreApproval durumunda kullanılabilir.", 409);
        if (claim.Status != ReturnClaimStatus.ActionRequired && !(awaitingPreApproval && claim.Status == ReturnClaimStatus.Requested)) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "İade aksiyonu yalnız sağlayıcının aksiyon beklediği durumda kullanılabilir.", 409);

        var claimLineIds = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claimId).Select(x => x.Id).ToListAsync(cancellationToken);
        if (claimLineIds.Count == 0) return Invalid<ReturnDetailView>("returnLineIds", "İade işleminde en az bir ürün satırı bulunmalıdır.");
        var returnLineIds = command.ReturnLineIds?.Distinct().ToArray() ?? claimLineIds.ToArray();
        if (returnLineIds.Length == 0 || returnLineIds.Except(claimLineIds).Any()) return Invalid<ReturnDetailView>("returnLineIds", "İşlem yapılacak ürün satırları bu iadeye ait olmalıdır.");
        if (isHepsiburada && returnLineIds.Length != claimLineIds.Count) return Invalid<ReturnDetailView>("returnLineIds", "Hepsiburada talep kabul/red işlemi talebin tüm ürün satırlarına uygulanır; kısmi satır seçimi desteklenmez.");
        var partialLineDecision = returnLineIds.Length < claimLineIds.Count;
        var finalizedWith = command.FinalizedWith?.Trim();
        if (isHepsiburada && action == "APPROVE" && finalizedWith is not ("Refund" or "Change")) return Invalid<ReturnDetailView>("finalizedWith", "Hepsiburada talep kabulünde iade veya ürün değişimi seçilmelidir.");
        if ((!isHepsiburada || action != "APPROVE") && !string.IsNullOrWhiteSpace(finalizedWith)) return Invalid<ReturnDetailView>("finalizedWith", "Kabul türü yalnız Hepsiburada talebini kabul ederken gönderilebilir.");
        if (isHepsiburada && action == "APPROVE" && string.Equals(claim.ReasonCode, "MissingInvoice", StringComparison.OrdinalIgnoreCase)) return ServiceResult<ReturnDetailView>.Fail("HEPSIBURADA_MISSING_INVOICE_MANUAL", "Eksik fatura taleplerinin kabul işlemi Hepsiburada panelinden yapılmalıdır.", 409);
        var activeDecision = await db.ReturnDecisions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claimId && (x.Status == "PENDING" || x.Status == "RETRY_SCHEDULED" || x.Status == "MANUAL_REVIEW" || (x.Status == "SUBMITTED" && db.ExternalEffectRecords.Any(effect => effect.TenantId == x.TenantId && effect.IdempotencyKey == x.IdempotencyKey)))).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (activeDecision is not null) return ServiceResult<ReturnDetailView>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade kararı pazaryerine gönderildi; sonuç kesinleşene kadar yeni bir karar gönderilemez.", 409);
        if (action == "REJECT" && (string.IsNullOrWhiteSpace(command.ReasonCode) || string.IsNullOrWhiteSpace(command.Explanation) || command.Explanation.Trim().Length > 500)) return Invalid<ReturnDetailView>("explanation", "REJECT için reasonCode ve en fazla 500 karakter açıklama gerekir.");
        var evidenceOptional = command.ReasonCode is "1651" or "451" or "2101";
        if (action == "REJECT" && !isHepsiburada && !evidenceOptional && (command.EvidenceAssetIds is null || command.EvidenceAssetIds.Count == 0)) return Invalid<ReturnDetailView>("evidenceAssetIds", "Seçilen ret nedeni için en az bir kanıt dosyası gerekir.");
        if (preApprovalConfirm && (!string.IsNullOrWhiteSpace(command.FinalizedWith) || !string.IsNullOrWhiteSpace(command.ReasonCode) || !string.IsNullOrWhiteSpace(command.Explanation) || command.EvidenceAssetIds is { Count: > 0 })) return Invalid<ReturnDetailView>("action", "Hepsiburada ön onay incelemesi ek kabul, ret veya kanıt alanı almaz.");

        var stage = await IsStageConnection(tenantId, claim.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("ENVIRONMENT_INVALID", "İade aksiyonu yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!stage && !await WritesEnabled(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (isHepsiburada && !stage && !await Supported(tenantId, claim.ConnectionId, MarketplaceCapabilities.ReturnWrite, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("CAPABILITY_EVIDENCE_REQUIRED", "Hepsiburada talep yazması için aynı mağaza ve ortamda doğrulanmış SIT yetenek kanıtı gerekir.", 422);
        if (!await ExternalWritePolicyEnabledAsync(tenantId, claim.ConnectionId, MarketplaceExternalWritePolicies.Return, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "İade dış yazma akışı kapalı.", 422);

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
            CreatedAt = now
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

        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = normalizedKey, CreatedAt = now };
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
                decision.Status = "MANUAL_REVIEW";
                await db.SaveChangesAsync(cancellationToken);
                return ServiceResult<ReturnDetailView>.Fail("EXTERNAL_EFFECT_AMBIGUOUS", error?.SafeMessage ?? "Pazaryeri iade isteğine yanıt vermedi; tekrar gönderim engellendi.", 409);
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
        claim.Status = ReturnClaimDecisionPolicy.StatusAfterLineDecision(claim.Status, action, returnLineIds.Length, claimLineIds.Count);
        claim.RawStatus = partialLineDecision ? "PARTIAL_LINE_DECISION_PENDING" : result.Value?.Status ?? (action switch
        {
            "APPROVE" => "ACCEPTED",
            "REJECT" => "REJECTED",
            _ => "PREAPPROVAL_CONFIRM_SUBMITTED"
        });
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
                await CancelledReturnClaimCleanup.RemoveAsync(db, tenantId, claimId, cancellationToken);
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
            var confirmed = action == "APPROVE" && remoteStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || action == "REJECT" && remoteStatus is ReturnClaimStatus.Rejected or ReturnClaimStatus.Disputed
                || preApprovalConfirm && (remoteStatus is ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit
                    || remoteStatus == ReturnClaimStatus.ActionRequired && !HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(readback.Value.RawStatus));
            var conflicting = action == "APPROVE" && remoteStatus is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled
                || action == "REJECT" && remoteStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || preApprovalConfirm && remoteStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled;
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
            if (claim.Status is not (ReturnClaimStatus.Approved or ReturnClaimStatus.Completed)) return ServiceResult<ReturnDetailView>.Fail("RETURN_DISPOSITION_NOT_ALLOWED", "Stok disposition yalnız onaylanmış veya tamamlanmış iade için uygulanabilir.", 409);
            var line = await db.ReturnLines.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claimId && x.Id == command.ReturnLineId, cancellationToken);
            if (line is null) return NotFound<ReturnDetailView>();
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
        static bool Text(string? value, int maxLength) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= maxLength;
        return Text(command.Barcode, 128) && !command.Barcode.Any(char.IsWhiteSpace)
            && Text(command.CargoCompany, 100) && Text(command.Carrier, 100) && Text(command.CreationReason, 100)
            && Text(command.ShippingAddressLabel, 100) && Text(command.ShippingModel, 100)
            && command.Deci is >= 0 and <= 10000 && command.ParcelQuantity is >= 1 and <= 100
            && command.LineItems is { Count: > 0 and <= 100 }
            && command.LineItems.All(line => line.OrderLineId != Guid.Empty && line.Quantity is > 0 and <= 10000);
    }

    private static ServiceError? ValidateShipmentAction(ShipmentPackage package, string action, string payloadJson)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal) { "PICKING", "INVOICED", "TRACKING_NUMBER", "CANCEL_ITEMS", "SPLIT", "MULTI_SPLIT", "CHANGE_CARGO_PROVIDER", "ALTERNATIVE_DELIVERY", "MANUAL_DELIVER", "MANUAL_RETURN" };
        if (!supported.Contains(action)) return new("SHIPMENT_ACTION_UNSUPPORTED", "Paket aksiyonu tanınmıyor.", 422);
        if (action == "PICKING" && package.Status is not (ShipmentPackageStatus.New or ShipmentPackageStatus.OnHold)) return new("SHIPMENT_STATE_CONFLICT", "PICKING yalnız NEW/ON_HOLD paket için gönderilebilir.", 409);
        if (action == "INVOICED" && package.Status != ShipmentPackageStatus.Processing) return new("SHIPMENT_STATE_CONFLICT", "INVOICED yalnız PROCESSING paket için gönderilebilir.", 409);
        if (action == "TRACKING_NUMBER" && package.Status is not (ShipmentPackageStatus.Processing or ShipmentPackageStatus.ReadyToShip)) return new("SHIPMENT_STATE_CONFLICT", "TRACKING_NUMBER yalnız PICKING/INVOICED paket için gönderilebilir.", 409);
        if ((action is "CANCEL_ITEMS" or "SPLIT" or "MULTI_SPLIT" or "CHANGE_CARGO_PROVIDER") && package.Status is (ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Returned or ShipmentPackageStatus.Cancelled)) return new("SHIPMENT_STATE_CONFLICT", "Bu aksiyon sevk edilmiş veya terminal paket için kullanılamaz.", 409);
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
                "CANCEL_ITEMS" => NonEmptyArray(root, "lines"),
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
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA") && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Aktif veya doğrulanmış marketplace bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.AllowsManualRead(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Read işlemi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        type = MarketplaceJobTypes.ForPlatform(connection.PlatformCode, type);
        return await Enqueue(tenantId, connectionId, type, $"{type.ToLowerInvariant()}:{connectionId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}", payload, correlationId, cancellationToken);
    }
    private async Task<ServiceResult<Guid>> Enqueue(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId, CancellationToken cancellationToken)
    {
        var recurringRead = type is MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.HepsiburadaReferenceSync or MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation or MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.QuestionSync;
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
        if ((jobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync)
            && TargetedExternalOrderId(payload) is not null)
            job.MaxAttempts = Math.Max(job.MaxAttempts, 30);
    }
    private static bool IsFullOrderScanRequest(string jobType, string payload)
    {
        if (jobType is not (MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync)) return false;
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
    private IntegrationJob NewJob(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = type, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup, Priority = Priority(type), AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
    internal static int Priority(string type) => type switch
    {
        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.ShipmentAction or MarketplaceJobTypes.CommonLabel => 0,
        MarketplaceJobTypes.HepsiburadaOrderRecoverySync => 2,
        MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync => 6,
        MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync or MarketplaceJobTypes.ReturnAction => 2,
        MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync or MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.HepsiburadaReferenceSync => 5,
        _ => 3
    };
    private Task<bool> Supported(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken) => db.PlatformCapabilities.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken);
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

    private static DateTimeOffset? OperationalDueAt(string json) =>
        JsonInstant(json, "agreedDeliveryDate", "estimatedDeliveryEndDate", "lastDeliveryDate", "deliveryDate", "estimatedDeliveryStartDate", "packageLastModifiedDate", "packageDeliveryDate", "packageEstimatedDeliveryDate", "dueDate", "shipmentDueDate", "deliveryDueAt");

    internal static string InvoiceLabel(Invoice? invoice, string customerJson) =>
        InvoiceLabel(invoice, customerJson, []);

    internal static string InvoiceLabel(Invoice? invoice, string customerJson, IEnumerable<string?> packageRawStatuses) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, customerJson, packageRawStatuses);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, string customerJson, IEnumerable<string?> packageRawStatuses)
    {
        // The local fiscal invoice and the marketplace delivery are separate
        // facts. A fiscal invoice number does not prove that Trendyol accepted
        // the document, so Submitted/Accepted/MarketplacePending stay visible
        // as an in-progress marketplace delivery.
        if (invoiceStatus is { } status)
        {
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview or InvoiceStatus.MarketplaceFailed) return "FATURA_REDDEDILDI";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal) return "FATURA_IPTAL";
            if (status == InvoiceStatus.Completed)
                return "FATURA_KESILDI";
            if (status is InvoiceStatus.Submitted or InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending)
                return "FATURA_KONTROLDE";
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
            return "FATURA_KESILDI";

        var remote = JsonText(customerJson, "marketplaceInvoiceStatus", "invoiceStatus")?.Trim().ToUpperInvariant();
        if (remote is "INVOICED") return "FATURA_KESILDI";
        if (remote is "RECEIVED") return "FATURA_KONTROLDE";
        if (remote is "REJECTED") return "FATURA_REDDEDILDI";
        if (remote is "NOTINVOICED" or "NOT_INVOICED") return "FATURA_BEKLIYOR";
        // Missing marketplace evidence is not the same as an explicit
        // NotInvoiced response. Historical Trendyol payloads often omit the
        // invoice field entirely; presenting those packages as actionable
        // invoice work creates false Dashboard counts.
        return "FATURA_BILINMIYOR";
    }

    internal static string InvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses)
    {
        if (invoiceStatus is { } status)
        {
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview or InvoiceStatus.MarketplaceFailed)
                return "FATURA_REDDEDILDI";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal)
                return "FATURA_IPTAL";
            // The marketplace observation is authoritative for the delivery
            // leg. A local invoice number only proves that our fiscal provider
            // created a document; it does not prove Trendyol accepted it.
            if (marketplaceStatus == MarketplaceInvoiceStatus.Invoiced) return "FATURA_KESILDI";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Rejected) return "FATURA_REDDEDILDI";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Received) return "FATURA_KONTROLDE";
            if ((marketplaceStatus is MarketplaceInvoiceStatus.Unknown or MarketplaceInvoiceStatus.NotInvoiced)
                && (status is InvoiceStatus.Submitted or InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Completed))
                return "FATURA_KONTROLDE";
            return InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses);
        }
        return marketplaceStatus switch
        {
            MarketplaceInvoiceStatus.Invoiced => "FATURA_KESILDI",
            MarketplaceInvoiceStatus.Received => "FATURA_KONTROLDE",
            MarketplaceInvoiceStatus.Rejected => "FATURA_REDDEDILDI",
            MarketplaceInvoiceStatus.NotInvoiced => "FATURA_BEKLIYOR",
            _ => InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses)
        };
    }

    internal static string ReturnInvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses)
    {
        // An absent marketplace observation does not prove that the package is
        // waiting for an invoice. Keep unknown separate so the return list does
        // not report a false pending state when Trendyol has not exposed it.
        return InvoiceLabel(invoice, marketplaceStatus, customerJson, packageRawStatuses);
    }

    internal static string InvoiceLabelForPlatform(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode)
        => InvoiceLabelForPlatform(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, platformCode);

    internal static string InvoiceLabelForPlatform(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode)
    {
        var label = InvoiceLabel(invoiceStatus, invoiceNumber, marketplaceStatus, customerJson, packageRawStatuses);
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
        {
            if (invoiceStatus == InvoiceStatus.Draft) return "FATURA_BEKLIYOR";
            if (invoiceStatus == InvoiceStatus.Completed) return "FATURA_YUKLENDI";
            if (label == "FATURA_BILINMIYOR") return "FATURA_BEKLIYOR";
        }
        return label;
    }

    internal static ShipmentPackage? SelectDisplayPackage(IEnumerable<ShipmentPackage> packages, string? platformCode)
    {
        var ordered = packages.OrderByDescending(x => x.StatusOccurredAt).ThenByDescending(x => x.UpdatedAt).ToList();
        if (!string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)) return ordered.FirstOrDefault();

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

    internal static string? SourceImageUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindImageUrl(document.RootElement) ?? JsonText(json, "productImageUrl", "productImageUrlFormat", "emaproductImageUrlFormat", "imageUrl", "productImage", "image");
        }
        catch (JsonException) { return null; }
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
                if (property.Name.Equals("images", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var image in property.Value.EnumerateArray())
                    {
                        var url = image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out var value) ? value.ToString() : image.ToString();
                        if (!string.IsNullOrWhiteSpace(url)) return url;
                    }
                var nested = FindImageUrl(property.Value); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageUrl(item); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private sealed record SourceLineView(string? ImageUrl, string? ModelCode, string? OptionSignature);

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

    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<T> Page<T>(List<T> rows, int limit, Func<T, Guid> id) { var hasMore = rows.Count > limit; var items = rows.Take(limit).ToList(); return new(items, hasMore ? cursors.Encode(id(items[^1])) : null, hasMore); }
    private static ShipmentView Map(ShipmentPackage x, string orderNumber) => new(x.Id, x.OrderId, orderNumber, x.ExternalPackageId, Wire(x.Status), x.RawStatus, x.CargoTrackingNumber, x.StatusOccurredAt, x.Version, x.CargoProviderExternalId, ShipmentPackageClassification.IsResend(x.CreatedBy, x.OriginExternalPackageId));
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
    private static bool IsAmbiguous(AdapterError error) => error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug;
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
}
