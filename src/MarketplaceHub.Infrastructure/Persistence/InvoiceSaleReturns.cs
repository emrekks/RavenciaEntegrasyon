using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// The returned units of orders not invoiced yet, read from the stored return data only (owner's rule of 6 Nov 2026):
/// the order lines' returned quantity, the package lines' returned quantity, the units of a current package in Returned
/// state, the units of approved return claims (ReturnDecisionLinePolicy.StockDispositionAllowed, the rule the issued
/// invoices' return notes use, InvoiceAdjustmentTracking) and a RETURNED order. They are combined per line by
/// OrderLineReturnPolicy, the rule the reports use. An open claim keeps its units on the invoice until it is approved.
/// </summary>
internal static class InvoiceSaleReturns
{
    private sealed record LineRow(Guid Id, Guid OrderId, decimal OrderedQuantity, decimal CancelledQuantity, decimal ReturnedQuantity);
    private sealed record PackageRow(Guid Id, Guid OrderId, ShipmentPackageStatus Status);

    /// <summary>
    /// The returned units per order line of the given orders. Only orders with at least one returned unit are in the
    /// result; an order or line missing from it has none.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, decimal>>> ReturnedUnitsAsync(
        AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0) return new Dictionary<Guid, IReadOnlyDictionary<Guid, decimal>>();
        var ids = orderIds.Distinct().ToArray();
        var returnedStatuses = DashboardRevenuePolicy.ReturnedStatuses;
        // Most orders have no return at all: only those with some stored return evidence are read further.
        var evidenced = await db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId && ids.Contains(order.Id)
                && (returnedStatuses.Contains(order.DerivedStatus)
                    || db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.OrderId == order.Id && claim.Status != ReturnClaimStatus.Cancelled)
                    || db.ShipmentPackages.Any(package => package.TenantId == tenantId && package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Returned)
                    || db.OrderLines.Any(line => line.TenantId == tenantId && line.OrderId == order.Id && line.ReturnedQuantity > 0)
                    || db.PackageLineAllocations.Any(allocation => allocation.TenantId == tenantId && allocation.ReturnedQuantity > 0
                        && db.OrderLines.Any(line => line.TenantId == tenantId && line.Id == allocation.OrderLineId && line.OrderId == order.Id))))
            .Select(order => new { order.Id, order.DerivedStatus })
            .ToListAsync(cancellationToken);
        if (evidenced.Count == 0) return new Dictionary<Guid, IReadOnlyDictionary<Guid, decimal>>();
        var evidencedIds = evidenced.Select(order => order.Id).ToArray();

        var lines = await db.OrderLines.AsNoTracking()
            .Where(line => line.TenantId == tenantId && evidencedIds.Contains(line.OrderId))
            .Select(line => new LineRow(line.Id, line.OrderId, line.OrderedQuantity, line.CancelledQuantity, line.ReturnedQuantity))
            .ToListAsync(cancellationToken);
        var packages = await MarketplaceSalesService.CurrentShipmentPackages(db)
            .Where(package => package.TenantId == tenantId && evidencedIds.Contains(package.OrderId) && package.Status != ShipmentPackageStatus.Cancelled)
            .Select(package => new PackageRow(package.Id, package.OrderId, package.Status))
            .ToListAsync(cancellationToken);
        var packageIds = packages.Select(package => package.Id).ToArray();
        var allocations = packageIds.Length == 0 ? [] : await db.PackageLineAllocations.AsNoTracking()
            .Where(allocation => allocation.TenantId == tenantId && packageIds.Contains(allocation.PackageId))
            .ToListAsync(cancellationToken);
        var approved = await ApprovedClaimQuantitiesAsync(db, tenantId, evidencedIds, cancellationToken);

        var linesByOrder = lines.GroupBy(line => line.OrderId).ToDictionary(group => group.Key, group => group.ToList());
        var packagesByOrder = packages.GroupBy(package => package.OrderId).ToDictionary(group => group.Key, group => group.ToList());
        var latest = InvoicingBillingService.LatestAllocations(allocations).ToLookup(allocation => allocation.OrderLineId);
        var result = new Dictionary<Guid, IReadOnlyDictionary<Guid, decimal>>();
        foreach (var order in evidenced)
        {
            var orderPackages = packagesByOrder.GetValueOrDefault(order.Id) ?? [];
            var returnedPackageIds = orderPackages.Where(package => package.Status == ShipmentPackageStatus.Returned).Select(package => package.Id).ToHashSet();
            var orderPackageIds = orderPackages.Select(package => package.Id).ToHashSet();
            var orderReturned = returnedStatuses.Contains(order.DerivedStatus?.Trim().ToUpperInvariant() ?? string.Empty)
                || orderPackages.Count > 0 && returnedPackageIds.Count == orderPackages.Count;
            var perLine = new Dictionary<Guid, decimal>();
            foreach (var line in linesByOrder.GetValueOrDefault(order.Id) ?? [])
            {
                var lineAllocations = latest[line.Id].Where(allocation => orderPackageIds.Contains(allocation.PackageId)).ToList();
                var returned = OrderLineReturnPolicy.ReturnedUnits(
                    line.OrderedQuantity - line.CancelledQuantity,
                    orderReturned,
                    line.ReturnedQuantity,
                    lineAllocations.Sum(allocation => allocation.ReturnedQuantity),
                    lineAllocations.Where(allocation => returnedPackageIds.Contains(allocation.PackageId)).Sum(allocation => allocation.AllocatedQuantity),
                    approved.GetValueOrDefault(line.Id));
                if (returned > 0) perLine[line.Id] = returned;
            }
            if (perLine.Count > 0) result[order.Id] = perLine;
        }
        return result;
    }

    public static async Task<IReadOnlyDictionary<Guid, decimal>> ReturnedUnitsAsync(AppDbContext db, Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        (await ReturnedUnitsAsync(db, tenantId, [orderId], cancellationToken)).GetValueOrDefault(orderId) ?? new Dictionary<Guid, decimal>();

    /// <summary>
    /// How much of each order came back (InvoiceSaleReturnPolicy.Classify) with its returned units per line, for the
    /// invoice workspace lists. Only orders with at least one returned unit are in the result.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, InvoiceSaleReturnState>> StatesAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        var returned = await ReturnedUnitsAsync(db, tenantId, orderIds, cancellationToken);
        if (returned.Count == 0) return new Dictionary<Guid, InvoiceSaleReturnState>();
        var ids = returned.Keys.ToArray();
        var lines = await db.OrderLines.AsNoTracking().Where(line => line.TenantId == tenantId && ids.Contains(line.OrderId)).ToListAsync(cancellationToken);
        return lines.GroupBy(line => line.OrderId).ToDictionary(group => group.Key, group =>
            new InvoiceSaleReturnState(InvoiceSaleReturnPolicy.Classify(Units(group, returned[group.Key])), returned[group.Key]));
    }

    /// <summary>
    /// The units approved return claims accepted, per order line. A line counts once it is approved on its own or its
    /// claim is (ReturnDecisionLinePolicy.StockDispositionAllowed): a rejected, cancelled or still waiting line does not.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, decimal>> ApprovedClaimQuantitiesAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        var ids = orderIds.ToArray();
        var rows = await (from line in db.ReturnLines.AsNoTracking()
                          join claim in db.ReturnClaims.AsNoTracking() on new { line.TenantId, Id = line.ClaimId } equals new { claim.TenantId, claim.Id }
                          where claim.TenantId == tenantId && ids.Contains(claim.OrderId) && claim.Status != ReturnClaimStatus.Cancelled
                          select new { line.OrderLineId, line.Quantity, line.RawStatus, ClaimStatus = claim.Status })
            .ToListAsync(cancellationToken);
        return rows.Where(row => ReturnDecisionLinePolicy.StockDispositionAllowed(row.ClaimStatus, row.RawStatus))
            .GroupBy(row => row.OrderLineId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity));
    }

    /// <summary>The order's lines still ordered, with their returned units, as the sale invoice sees them.</summary>
    public static IReadOnlyList<InvoiceLineUnits> Units(IEnumerable<OrderLine> lines, IReadOnlyDictionary<Guid, decimal> returned) =>
        lines.Select(line => new InvoiceLineUnits(line.Id, line.UnitPrice, line.OrderedQuantity - line.CancelledQuantity, returned.GetValueOrDefault(line.Id))).ToList();

    /// <summary>
    /// What a sale invoice of the order covers: the kept units of its lines (a line returned in full leaves the invoice),
    /// the totals for those units (InvoiceSaleReturnPolicy.KeptTotals), and the returned lines it leaves out.
    /// </summary>
    public static InvoiceSalePlan Plan(IReadOnlyList<OrderLine> activeLines, IReadOnlyDictionary<Guid, decimal> returned, Func<OrderLine, decimal> vatRate, InvoiceOrderTotals totals)
    {
        var units = Units(activeLines, returned);
        var scope = InvoiceSaleReturnPolicy.Classify(units);
        var sources = activeLines.Zip(units)
            .Where(pair => pair.Second.KeptQuantity > 0)
            .Select(pair => new InvoicePackageLineSource(pair.First.Id, pair.First.TitleSnapshot, pair.First.Sku, pair.Second.KeptQuantity, pair.First.UnitPrice, vatRate(pair.First)))
            .ToArray();
        var returnedLines = activeLines.Zip(units)
            .Where(pair => pair.Second.ActiveQuantity > 0 && pair.Second.KeptQuantity < pair.Second.ActiveQuantity)
            .Select(pair => new InvoiceReturnedLine(pair.First.Id, pair.First.TitleSnapshot, pair.First.Sku, pair.Second.ActiveQuantity, pair.Second.ActiveQuantity - pair.Second.KeptQuantity))
            .ToArray();
        return new(sources, scope == InvoiceSaleReturnScope.None ? totals : InvoiceSaleReturnPolicy.KeptTotals(units, totals), scope, returnedLines);
    }
}

internal sealed record InvoiceSaleReturnState(InvoiceSaleReturnScope Scope, IReadOnlyDictionary<Guid, decimal> ReturnedByLine);

internal sealed record InvoiceReturnedLine(Guid OrderLineId, string Description, string? Sku, decimal ActiveQuantity, decimal ReturnedQuantity);

internal sealed record InvoiceSalePlan(IReadOnlyList<InvoicePackageLineSource> Lines, InvoiceOrderTotals Totals, InvoiceSaleReturnScope Scope, IReadOnlyList<InvoiceReturnedLine> ReturnedLines)
{
    public bool FullyReturned => Scope == InvoiceSaleReturnScope.Full;
}
