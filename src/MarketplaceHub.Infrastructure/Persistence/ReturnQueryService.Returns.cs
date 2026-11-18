using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderQueryService;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class ReturnQueryService
{
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
        var claims = await query.OrderBy(x => x.LastRemoteModifiedAt == null).ThenByDescending(x => x.LastRemoteModifiedAt).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var rows = await ReturnViewsAsync(tenantId, claims, cancellationToken);
        return new(rows.Take(limit).ToList(), null, rows.Count > limit, totalCount);
    }

    private Task<bool> HasOperationalReturnConnectionAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId
                && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM")
                && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);

    internal IQueryable<ReturnClaim> ReturnBaseQuery(Guid tenantId) =>
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
        var connectionIds = claims.Select(x => x.ConnectionId).Concat(orders.Values.Select(x => x.ConnectionId)).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        // In the order the lines were stored (version 7 ids grow with time; the panel stores a claim's lines in the
        // marketplace's order), as the return detail lists them: without an order the database could hand them back
        // differently from one request to the next (RETURN-ORDER), and with them the first line's photo and barcode.
        var returnLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && claimIds.Contains(x.ClaimId)).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var orderLineIds = returnLines.Select(x => x.OrderLineId).Distinct().ToArray();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var returnLineSkus = orderLines.Values.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLineSkuKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLineBarcodes = orderLines.Values.Select(x => x.Barcode).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLineBarcodeKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLinkKeys = returnLineSkuKeys.Concat(orderLines.Values.SelectMany(LineMarketplaceIdentities)).Distinct(StringComparer.Ordinal).ToArray();
        var returnVariantLinks = returnLinkKeys.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId
                    && connectionIds.Contains(x.ConnectionId)
                    && (returnLinkKeys.Contains(x.ExternalId.Trim().ToUpper()) || x.ExternalSku != null && returnLinkKeys.Contains(x.ExternalSku.Trim().ToUpper())))
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
            foreach (var identity in LineMarketplaceIdentities(line))
                if (returnVariantsByMarketplaceIdentity.TryGetValue((connectionId ?? Guid.Empty, identity), out var linkedId) && returnVariantsById.GetValueOrDefault(linkedId) is { } linked) return linked;
            return null;
        }
        // The latest package and invoice by date, then by id (ties had no order): the invoice label filter reads them alike
        // (ReturnInvoiceLabelRows).
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).OrderByDescending(x => x.StatusOccurredAt).ThenByDescending(x => x.Id).ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId) && x.OriginalInvoiceId == null
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync(cancellationToken);
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
        // "Kısmen Onaylandı" at order level: every claim of the listed orders (also those outside this page), with
        // their lines' own statuses (OrderReturnPartialApprovalPolicy).
        var siblingClaims = orderIds.Length == 0 ? [] : await db.ReturnClaims.AsNoTracking()
            .Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId))
            .Select(x => new { x.Id, x.OrderId, x.Status })
            .ToListAsync(cancellationToken);
        var siblingClaimIds = siblingClaims.Select(x => x.Id).ToArray();
        var siblingLineStatuses = siblingClaimIds.Length == 0 ? [] : (await db.ReturnLines.AsNoTracking()
                .Where(x => x.TenantId == tenantId && siblingClaimIds.Contains(x.ClaimId))
                .Select(x => new { x.ClaimId, x.RawStatus })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.ClaimId)
            .ToDictionary(x => x.Key, x => (IReadOnlyCollection<string?>)x.Select(line => line.RawStatus).ToList());
        var partiallyApprovedOrders = siblingClaims
            .GroupBy(x => x.OrderId)
            .Where(group => OrderReturnPartialApprovalPolicy.IsPartiallyApproved(group.Select(x => (x.Status, siblingLineStatuses.GetValueOrDefault(x.Id) ?? []))))
            .Select(group => group.Key)
            .ToHashSet();
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
            var lineViews = claimLines.Select(returnLine =>
            {
                var line = orderLines.GetValueOrDefault(returnLine.OrderLineId);
                if (line is null) return null;
                var source = SourceLine(line.SourceSnapshotJson);
                var variant = ResolveReturnVariant(line);
                // The marketplace's photo with the catalog's behind it: a marketplace address that stops loading (a
                // Pazarama img.pzrmcdn.com link answered 403 on 6 Nov 2026) falls back to our own picture.
                // With neither a marketplace nor a catalog photo (the catalog may be empty) the barcode lookup the order
                // screens use gives the photo, so a return line shows what its order line shows.
                var images = ReturnDetailLineImageUrls(source.ImageUrl, variant is null ? null : returnImageUrls.GetValueOrDefault(variant.Id),
                    string.IsNullOrWhiteSpace(line.Barcode) ? string.IsNullOrWhiteSpace(line.Sku) ? variant?.Barcode : line.Sku : line.Barcode, claim.ConnectionId);
                return new OrderLineView(line.Id, line.Sku, line.Barcode ?? variant?.Barcode, line.TitleSnapshot, returnLine.Quantity, line.CancelledQuantity, line.ShippedQuantity, line.DeliveredQuantity, line.ReturnedQuantity, line.UnitPrice, line.VatRate, line.RawStatus, line.VariantId ?? variant?.Id, ReturnLineModelCode(variant, source.ModelCode), variant?.OptionSignature ?? source.OptionSignature, images.ImageUrl,
                    FallbackImageUrl: images.FallbackImageUrl,
                    ReturnAmounts: ReturnMarketplaceAmounts.Line(platformCode, line.SourceSnapshotJson, returnLine.Quantity, claim.ExternalClaimId));
            }).Where(line => line is not null).Select(line => line!).ToList();
            return new ReturnListView(claim.Id, claim.ExternalClaimId, order?.OrderNumber ?? "—", ReturnStatusWire(claim.Status, claimLines.Select(x => x.RawStatus).ToList()), claim.RawStatus, claim.ReasonText, ReturnActionDueAt(platformCode, claim.RawStatus, claim.ActionDueAt, claim.LastRemoteModifiedAt), claim.Version,
                order is null ? "—" : Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).Name,
                order?.OrderedAt, order?.NetAmount ?? 0, order?.Currency ?? "TRY", claim.CargoProviderName, claim.CargoTrackingNumber, lineViews.FirstOrDefault()?.ImageUrl, claimLines.Count, firstLine?.Barcode ?? firstVariant?.Barcode,
                lineViews, package?.ExternalPackageId, order is null ? "FATURA_BEKLIYOR" : InvoiceLabelPolicy.ReturnInvoiceLabel(invoice, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, order.CustomerSnapshotJson, package is null ? [] : [package.RawStatus], packageDeliveryConfirmed), order?.GrossAmount ?? 0, order?.DiscountAmount ?? 0,
                order is not null && Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).IsMicroExport,
                claimConnection?.Id ?? connection?.Id, platformCode, claimConnection?.DisplayName ?? connection?.DisplayName ?? "Trendyol", outboundPackage?.CargoProviderExternalId, outboundPackage?.CargoTrackingNumber, claim.ReasonCode, approvedAt, claim.CargoTrackingLink,
                claimLines.Where(x => !string.IsNullOrWhiteSpace(x.RawStatus)).GroupBy(x => x.OrderLineId).ToDictionary(x => x.Key, x => Wire(MarketplaceReturnStatus.Canonicalize(x.First().RawStatus))),
                IsEstimatedReturnDueAt(platformCode, claim),
                ReturnClaimAmounts(claimLines.Count, lineViews),
                CustomerRequest: claim.CustomerRequest,
                OrderReturnStatus: partiallyApprovedOrders.Contains(claim.OrderId) ? ReturnClaimPartialApprovalPolicy.PartiallyApproved : null,
                RequestedAt: claim.RequestedAt,
                ExchangeApproved: ReturnCustomerRequestPolicy.IsTrendyolExchangeMade(platformCode, claim.CustomerRequest, claim.ResolvedWith));
        }).ToList();
        return rows;
    }

    // The status the return screens show: PARTIALLY_APPROVED ("Kısmen Onaylandı") for an approved claim whose lines
    // were decided both ways (ReturnClaimPartialApprovalPolicy), otherwise the stored status.
    internal static string ReturnStatusWire(ReturnClaimStatus status, IReadOnlyCollection<string?> lineStatuses) =>
        ReturnClaimPartialApprovalPolicy.IsPartiallyApproved(status, lineStatuses) ? ReturnClaimPartialApprovalPolicy.PartiallyApproved : Wire(status);

    internal static string? ReturnLineModelCode(ProductVariant? variant, string? sourceModelCode) =>
        string.IsNullOrWhiteSpace(variant?.ModelCode) ? sourceModelCode : variant.ModelCode;

    // The deadline is an estimate when the marketplace sent none: stored as
    // derived, or derived here from the status.
    internal static bool IsEstimatedReturnDueAt(string? platformCode, ReturnClaim claim) =>
        claim.ActionDueAt is null
            ? ReturnActionDueAt(platformCode, claim.RawStatus, null, claim.LastRemoteModifiedAt) is not null
            : claim.ActionDueAtEstimated;

    internal static DateTimeOffset? ReturnActionDueAt(string? platformCode, string? rawStatus, DateTimeOffset? actionDueAt, DateTimeOffset? lastRemoteModifiedAt)
    {
        if (actionDueAt is not null) return actionDueAt;
        var isTrendyolWaitingInAction = string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(rawStatus?.Trim(), "WaitingInAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawStatus?.Trim(), "WAITING_IN_ACTION", StringComparison.OrdinalIgnoreCase));
        if (isTrendyolWaitingInAction) return lastRemoteModifiedAt?.AddHours(48);
        var isTrendyolRejected = string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(rawStatus?.Trim(), "Rejected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawStatus?.Trim(), "REJECTED", StringComparison.OrdinalIgnoreCase));
        return isTrendyolRejected ? lastRemoteModifiedAt?.AddDays(7) : null;
    }

    internal void ApplyReturnFilters(ref IQueryable<ReturnClaim> query, ReturnListQuery options)
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
        // "İade Talep Tarihi" (owner, 11 Nov 2026): the marketplace's own request date of the claim (ReturnClaim.RequestedAt), the
        // page sending Istanbul day bounds; a claim the marketplace has not dated is left out only while a range is set.
        if (options.DateFrom?.ToUniversalTime() is { } dateFrom) query = query.Where(x => x.RequestedAt != null && x.RequestedAt >= dateFrom);
        if (options.DateTo?.ToUniversalTime() is { } dateTo) query = query.Where(x => x.RequestedAt != null && x.RequestedAt <= dateTo);
    }
}
