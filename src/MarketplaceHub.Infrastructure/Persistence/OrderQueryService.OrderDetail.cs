using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class OrderQueryService
{
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
        var catalog = await OrderLineCatalog.LoadAsync(db, tenantId, activeOrderLines, _ => order.ConnectionId, cancellationToken);
        var lines = activeOrderLines.Select(x =>
        {
            var variant = catalog.Variant(x);
            var source = SourceLine(x.SourceSnapshotJson);
            var images = catalog.Images(x, variant);
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
        var buyer = BuyerType(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, customer.IsMicroExport, order.BuyerType);
        return ServiceResult<OrderDetailView>.Ok(new(
            order.Id, order.OrderNumber, order.DerivedStatus, order.Currency, order.GrossAmount, order.DiscountAmount, order.NetAmount, order.OrderedAt,
            lines, visiblePackages.Select(x => Map(x, order.OrderNumber)).ToList(), order.Version,
            order.ConnectionId, connection?.PlatformCode ?? "TRENDYOL", connection?.DisplayName ?? "Trendyol",
            customer.Name, customer.Email, customer.TaxOrIdentityNumber, buyer.OrderType, customer.IsMicroExport,
            order.ShipmentAddressSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentDueAt ?? OperationalDueAt(order.CustomerSnapshotJson), PackageInvoiceLabel(displayPackage, invoice, order.CustomerSnapshotJson, packages.Select(x => x.RawStatus), connection?.PlatformCode, deliveryConfirmed),
            customer.Phone, customer.IsEInvoiceAvailable, invoiceDocumentUrl, ListAddress(order.ShipmentAddressSnapshotJson),
            UnreadableLineCount(packages), AmountMayBeIncomplete(connection?.PlatformCode, UnreadableLineCount(packages)), buyer.BuyerType));
    }

    // An order with an unread product line whose amount is the read lines' sum: PTT AVM sends no order total
    // (pttavm-api-son.md:1597-1632). Elsewhere the amount is the marketplace's own total (UnreadableOrderLines.AmountIssueCode
    // names an n11 or Pazarama read that came without it).
    internal static bool AmountMayBeIncomplete(string? platformCode, int unreadableLines) =>
        unreadableLines > 0 && string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase);

    // Product lines of the order's live packages the panel could not read (UnreadableOrderLines); a cancelled package's
    // lines no longer matter. Shopify's hidden remainder package counts too: it holds the unfulfilled lines.
    internal static int UnreadableLineCount(IEnumerable<ShipmentPackage> packages) =>
        packages.Where(package => package.Status != ShipmentPackageStatus.Cancelled).Sum(package => package.UnreadableLineCount);

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
                && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken)
            : null;
        if (connectionId is not null && connection is null) return NotFound<string>();

        // Questions sometimes have only a product title and no usable image,
        // SKU, or barcode. Reuse a unique local catalog title match in that case.
        if (!string.IsNullOrWhiteSpace(normalizedProductName) && normalizedProductName.Length <= 320)
        {
            var matchingProductIds = await EffectiveCatalog.Products(db)
                .Where(x => x.TenantId == tenantId && x.Title.ToUpper() == normalizedProductName.ToUpper())
                .Select(x => x.Id).OrderBy(id => id).Take(2).ToListAsync(cancellationToken);
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
                    .OrderBy(id => id)
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
                .OrderBy(id => id)
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
        // A store whose marketplace can be asked for a barcode's photo is asked; n11 and Pazarama have no such read, so their
        // lines (return details carry the claim's store) ask Trendyol, as a lookup without a store does.
        if (connection is null || !HasLiveImageLookup(connection.PlatformCode)) connection = await ActiveTrendyolConnection(tenantId, cancellationToken);
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

    // The marketplaces whose adapter reads a product's photo by barcode (IProductVisualLookupPort); any store's lookup is
    // accepted (its order lines and the catalog are searched first), these are also asked live.
    internal static bool HasLiveImageLookup(string? platformCode) =>
        platformCode?.Trim().ToUpperInvariant() is "HEPSIBURADA" or "TRENDYOL" or "SHOPIFY" or "PTTAVM";

    private async Task<PlatformConnection?> ActiveTrendyolConnection(Guid tenantId, CancellationToken cancellationToken) =>
        await (from connection in db.PlatformConnections.AsNoTracking()
               where connection.TenantId == tenantId && connection.PlatformCode == "TRENDYOL" && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                   && db.PlatformCredentials.Any(credential => credential.TenantId == tenantId && credential.ConnectionId == connection.Id && credential.RevokedAt == null)
               orderby connection.Id
               select connection).FirstOrDefaultAsync(cancellationToken);
}
