using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService
{
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
            // "Teslim Aldım" opens Onayla/Reddet only where the decision covers the whole claim; Trendyol, n11 and Pazarama
            // decide by the lines' own statuses, which stay in transit until the marketplace says otherwise (SIPARIS-STOK-1).
            ReturnClaimStatus.Requested or ReturnClaimStatus.InTransit => ReturnReceiptAllowed(platformCode) ? ["RECEIVE"] : Array.Empty<string>(),
            ReturnClaimStatus.AwaitingShipment when isHepsiburada => Array.Empty<string>(),
            ReturnClaimStatus.ActionRequired when isHepsiburada => HepsiburadaReturnActionPolicy.AllowedActions(claim.RawStatus, decisionPending),
            // n11 approvals and denials both go through the API (ClaimReturnApprove / ClaimReturnDeny); a denial always
            // carries the shipment back to the buyer or reason 41 (N11ReturnDenyRule) and is never sent on its own.
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
        var pendingStockLineIds = (await db.ReturnDecisions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ClaimId == id && x.StockStatus == ReturnPendingStockPolicy.Pending)
                .Select(x => x.StockLinesJson)
                .ToListAsync(cancellationToken))
            .SelectMany(json => ReturnPendingStock.Read(json).Select(line => line.ReturnLineId))
            .ToHashSet();
        var lineSkuKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lineBarcodeKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var linkKeys = lineSkuKeys.Concat(orderLines.Values.SelectMany(LineMarketplaceIdentities)).Distinct(StringComparer.Ordinal).ToArray();
        var variantLinks = linkKeys.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == claim.ConnectionId
                    && (linkKeys.Contains(x.ExternalId.Trim().ToUpper()) || x.ExternalSku != null && linkKeys.Contains(x.ExternalSku.Trim().ToUpper())))
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
            foreach (var identity in LineMarketplaceIdentities(line))
                if (variantsByExternalSku.TryGetValue(identity, out var linkedId) && variantsById.GetValueOrDefault(linkedId) is { } linked) return linked;
            return null;
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
            var images = ReturnDetailLineImageUrls(sourceSnapshot?.ImageUrl, variant is null ? null : imageUrls.GetValueOrDefault(variant.Id), string.IsNullOrWhiteSpace(source?.Barcode) ? source?.Sku : source.Barcode, claim.ConnectionId);
            return new ReturnLineView(line.Id, line.ExternalLineId, line.OrderLineId, source?.Sku ?? "—", source?.Barcode, source?.TitleSnapshot ?? "—", line.Quantity, disposed, Math.Max(0, line.Quantity - disposed), source?.UnitPrice ?? 0,
                images.ImageUrl,
                variant is not null && inventoryVariants.Contains(variant.Id),
                variant?.OptionSignature ?? sourceSnapshot?.OptionSignature,
                variant?.ModelCode ?? sourceSnapshot?.ModelCode,
                string.IsNullOrWhiteSpace(line.RawStatus) ? null : Wire(MarketplaceReturnStatus.Canonicalize(line.RawStatus)),
                source is null ? null : ReturnMarketplaceAmounts.Line(platformCode, source.SourceSnapshotJson, line.Quantity, claim.ExternalClaimId),
                line.AcceptDetail, line.MarketplaceNote, line.Resolved, line.AcceptedBySeller,
                HeldByDecision(line.Id), ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, line.RawStatus), pendingStockLineIds.Contains(line.Id),
                images.FallbackImageUrl);
        }).ToList();
        var stockDispositionAvailable = sourceLines.Count == 0
            ? ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, null)
            : lines.Any(line => line.StockDispositionAvailable);
        var package = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderByDescending(x => x.StatusOccurredAt).FirstOrDefaultAsync(cancellationToken);
        var customer = Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
        return ServiceResult<ReturnDetailView>.Ok(new(claim.Id, claim.ExternalClaimId, order.OrderNumber, ReturnStatusWire(claim.Status, sourceLines.Select(line => line.RawStatus).ToList()), claim.RawStatus, claim.ReasonCode, claim.ReasonText, ReturnActionDueAt(platformCode, claim.RawStatus, claim.ActionDueAt, claim.LastRemoteModifiedAt), actions, claim.Version,
            customer.Name, order.OrderedAt, order.NetAmount, order.Currency, claim.CargoProviderName, claim.CargoTrackingNumber, lines, stockDispositionAvailable, approvedAt, externalWritesEnabled, decisionPending, platformCode, claim.CargoTrackingLink, IsEstimatedReturnDueAt(platformCode, claim),
            ReturnMarketplaceAmounts.Total(lines.Select(line => line.ReturnAmounts).ToList()), claim.CustomerRequest,
            ReturnDecisionScope.IsWholeClaim(platformCode), IsN11(platformCode)));
    }

    private static bool IsN11(string? platformCode) => string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase);

    // Marking a return received only helps where the decision is sent for the whole claim (Hepsiburada, Shopify); elsewhere
    // the approve/reject call needs lines the marketplace itself reports as waiting, so the step would open dead buttons.
    internal static bool ReturnReceiptAllowed(string? platformCode) => ReturnDecisionScope.IsWholeClaim(platformCode);

    // The carriers an n11 denial can name for the shipment back to the buyer; other marketplaces list none.
    public async Task<ServiceResult<IReadOnlyList<ReturnShipmentCompany>>> ReturnShipmentCompaniesAsync(Guid tenantId, Guid id, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<IReadOnlyList<ReturnShipmentCompany>>();
        var context = new AdapterContext(tenantId, claim.ConnectionId, correlationId, $"return-shipment-companies:{claim.ConnectionId:N}", timeProvider.GetUtcNow().AddSeconds(30));
        var result = await returns.ReturnShipmentCompaniesAsync(context, cancellationToken);
        return result.IsSuccess
            ? ServiceResult<IReadOnlyList<ReturnShipmentCompany>>.Ok(result.Value!)
            : ServiceResult<IReadOnlyList<ReturnShipmentCompany>>.Fail(result.Error!.Code, result.Error.SafeMessage, result.Error.HttpStatus ?? 502);
    }

    // A claim's amounts over all its return lines: a line whose order line is missing counts as one without amounts.
    internal static ReturnAmountsView? ReturnClaimAmounts(int returnLineCount, IReadOnlyCollection<OrderLineView> lineViews) =>
        lineViews.Count < returnLineCount ? null : ReturnMarketplaceAmounts.Total(lineViews.Select(line => line.ReturnAmounts).ToList());

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
}
