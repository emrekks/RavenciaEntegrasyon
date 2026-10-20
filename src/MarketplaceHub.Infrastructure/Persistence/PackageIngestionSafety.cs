using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed record NormalizedPackageAllocation(
    decimal ActiveAllocatedQuantity,
    decimal CancelledQuantity,
    decimal ShippedQuantity,
    decimal DeliveredQuantity,
    decimal ReturnedQuantity);

public static class PackageLineProjectionPolicy
{
    public static IReadOnlyDictionary<Guid, NormalizedPackageAllocation> Recalculate(
        IReadOnlyCollection<ShipmentPackage> packages,
        IReadOnlyCollection<PackageLineAllocation> allocations)
    {
        var packageByExternalId = packages
            .Where(package => !string.IsNullOrWhiteSpace(package.ExternalPackageId))
            .GroupBy(package => package.ExternalPackageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(package => package.StatusOccurredAt).ThenByDescending(package => package.Version).First(), StringComparer.Ordinal);

        // A replacement/split response points back to the package it replaces.
        // When that parent is present locally, its last allocation must not be
        // added to the new child allocations. Distinct children remain separate
        // and are summed, which preserves split-package quantities.
        var replacedExternalIds = packageByExternalId.Values
            .Where(package => !string.IsNullOrWhiteSpace(package.OriginExternalPackageId)
                && packageByExternalId.ContainsKey(package.OriginExternalPackageId!))
            .Select(package => package.OriginExternalPackageId!)
            .ToHashSet(StringComparer.Ordinal);

        // A split (UnPacked) parent never carries quantities of its own: its
        // lines live on in the child packages. Until the children are read the
        // line projection keeps its previous values instead of counting the
        // parent's allocation as cancelled.
        var currentPackages = packageByExternalId.Values
            .Where(package => !replacedExternalIds.Contains(package.ExternalPackageId)
                && !ShipmentPackageSupersession.IsSplitParentRawStatus(package.RawStatus)
                && !ShipmentPackageSupersession.IsLocallyUnpacked(package.RawStatus));
        var result = new Dictionary<Guid, NormalizedPackageAllocation>();

        foreach (var package in currentPackages)
        {
            var currentEventId = EventId(package.ExternalPackageId, package.StatusOccurredAt);
            foreach (var allocation in allocations.Where(allocation =>
                         allocation.PackageId == package.Id
                         && allocation.SourceEventId == currentEventId))
            {
                var current = result.GetValueOrDefault(allocation.OrderLineId) ?? new NormalizedPackageAllocation(0, 0, 0, 0, 0);
                result[allocation.OrderLineId] = new(
                    current.ActiveAllocatedQuantity + allocation.AllocatedQuantity,
                    current.CancelledQuantity + allocation.CancelledQuantity,
                    current.ShippedQuantity + allocation.ShippedQuantity,
                    current.DeliveredQuantity + allocation.DeliveredQuantity,
                    current.ReturnedQuantity + allocation.ReturnedQuantity);
            }
        }

        return result;
    }

    private static string EventId(string externalPackageId, DateTimeOffset occurredAt) =>
        $"{externalPackageId}:{occurredAt.ToUnixTimeMilliseconds()}";
}

public static class OrderAmountProjectionPolicy
{
    // Package-shaped platforms (Trendyol, n11, Pazarama) read one sibling
    // package per stream page, so the page sum is not the order total. The
    // total is the sum of the order's stored live packages; cancelled packages
    // do not count while any package is still live, and a fully cancelled
    // order keeps the value it was placed with. Null when a stored package
    // predates package amounts and was not in this read: the sum would then
    // undercount and the caller keeps the previous totals.
    public static (decimal Gross, decimal Discount, decimal Net)? FromPackages(
        IReadOnlyCollection<ShipmentPackage> packages,
        IReadOnlySet<string> packagesInRead)
    {
        var current = ShipmentPackageSupersession.Current(packages)
            .Where(package => !ShipmentPackageSupersession.IsSplitParentRawStatus(package.RawStatus))
            .ToList();
        if (current.Count == 0) return null;
        if (current.Any(package => package.GrossAmount == 0 && package.NetAmount == 0 && !packagesInRead.Contains(package.ExternalPackageId)))
            return null;
        var billable = current.Where(package => package.Status != ShipmentPackageStatus.Cancelled).ToList();
        if (billable.Count == 0) billable = current;
        return (billable.Sum(package => package.GrossAmount), billable.Sum(package => package.DiscountAmount), billable.Sum(package => package.NetAmount));
    }
}

public static class PackageIngestionSafety
{
    public static bool TryGetOrderedQuantities(
        IReadOnlyList<RemoteOrderLine> remoteLines,
        out IReadOnlyDictionary<string, decimal> orderedQuantities)
    {
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var line in remoteLines)
        {
            if (string.IsNullOrWhiteSpace(line.ExternalLineId) || line.Quantity < 0 || !values.TryAdd(line.ExternalLineId, line.Quantity))
            {
                orderedQuantities = values;
                return false;
            }
        }
        orderedQuantities = values;
        return true;
    }

    public static bool ShouldAccept(
        ShipmentPackageStatus current,
        DateTimeOffset currentOccurredAt,
        ShipmentPackageStatus incoming,
        DateTimeOffset incomingOccurredAt,
        string? incomingRawStatus = null) =>
        incomingOccurredAt >= currentOccurredAt
        && (ShipmentPackageStateMachine.CanTransition(current, incoming)
            // A split parent is superseded from any open state (Processing,
            // ReadyToShip, Shipped...), not only from those that may cancel.
            || incoming == ShipmentPackageStatus.Cancelled
                && ShipmentPackageSupersession.IsSplitParentRawStatus(incomingRawStatus)
                && ShipmentPackageSupersession.CanSupersede(current));

    // A refused transition to an end state means the panel and the
    // marketplace disagree about the outcome; an operator has to look.
    public static bool IsTerminalConflict(ShipmentPackageStatus incoming, string? incomingRawStatus) =>
        incoming is ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Cancelled or ShipmentPackageStatus.Returned
        && !ShipmentPackageSupersession.IsSplitParentRawStatus(incomingRawStatus);

    // A package already at its own end (cancelled or returned) keeps it: an
    // older or contradicting end state from the marketplace is recorded as a
    // refused event, never turned into a manual review of a closed package.
    public static bool IsTerminalConflict(ShipmentPackageStatus current, ShipmentPackageStatus incoming, string? incomingRawStatus) =>
        current is not (ShipmentPackageStatus.Cancelled or ShipmentPackageStatus.Returned)
        && IsTerminalConflict(incoming, incomingRawStatus);

    public static string RejectedEventId(string eventId) => $"REJECTED:{eventId}";

    // The history marker the panel's instant picking writes (MarketplaceSalesService).
    public const string InstantPickingRawStatus = "PICKING_REQUESTED";

    // How long after an instant picking a remote "Created" is taken as a read made before
    // the marketplace applied it.
    public static readonly TimeSpan InstantPickingGrace = TimeSpan.FromMinutes(30);

    public static bool IsCreatedEchoAfterInstantPicking(ShipmentPackageStatus current, ShipmentPackageStatus incoming, DateTimeOffset? instantPickingAt, DateTimeOffset now) =>
        current == ShipmentPackageStatus.Processing
        && incoming == ShipmentPackageStatus.New
        && instantPickingAt is { } pickedAt
        && now - pickedAt <= InstantPickingGrace;

    // Kept as raw status while a terminal conflict waits for review. It maps to
    // ManualReview, so the ManualReview repair does not flip it back on the
    // next read; a newer marketplace event still settles it.
    public static string ConflictRawStatus(string remoteRawStatus) => $"CONFLICT:{remoteRawStatus}";

    public static string EventId(string externalPackageId, DateTimeOffset occurredAt) =>
        $"{externalPackageId}:{occurredAt.ToUnixTimeMilliseconds()}";

    public static bool AllEventsRecorded(IReadOnlyList<RemotePackage> packages, IReadOnlySet<string> recordedEventIds) =>
        packages.Count > 0 && packages.All(package => recordedEventIds.Contains(EventId(package.ExternalPackageId, package.OccurredAt)));

    public static bool TryNormalize(
        decimal orderedQuantity,
        RemotePackageAllocation remote,
        ShipmentPackageStatus packageStatus,
        out NormalizedPackageAllocation normalized)
    {
        var active = packageStatus == ShipmentPackageStatus.Cancelled ? 0 : remote.AllocatedQuantity;
        // A cancelled split package contributes only its own allocation. Using
        // the order-line total here would mark sibling packages cancelled too.
        var cancelled = packageStatus == ShipmentPackageStatus.Cancelled
            ? Math.Max(remote.AllocatedQuantity, remote.CancelledQuantity)
            : remote.CancelledQuantity;
        // An undelivered parcel was still handed to the carrier.
        var shipped = packageStatus is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered or ShipmentPackageStatus.ReturnInTransit or ShipmentPackageStatus.Returned
            ? active
            : remote.ShippedQuantity;
        var delivered = packageStatus is ShipmentPackageStatus.Delivered or ShipmentPackageStatus.ReturnInTransit or ShipmentPackageStatus.Returned
            ? active
            : packageStatus == ShipmentPackageStatus.Undelivered ? 0 : remote.DeliveredQuantity;
        var returned = packageStatus == ShipmentPackageStatus.Returned ? active : remote.ReturnedQuantity;

        normalized = new(active, cancelled, shipped, delivered, returned);
        return orderedQuantity >= 0
            && active >= 0
            && cancelled >= 0
            && shipped >= 0
            && delivered >= 0
            && returned >= 0
            && active + cancelled <= orderedQuantity
            && shipped <= active
            && delivered <= shipped
            && returned <= delivered;
    }

    public static bool TryNormalizeAll(
        IReadOnlyDictionary<string, decimal> orderedQuantities,
        IReadOnlyList<RemotePackageAllocation> remoteAllocations,
        ShipmentPackageStatus packageStatus,
        out IReadOnlyDictionary<string, NormalizedPackageAllocation> normalized)
    {
        var values = new Dictionary<string, NormalizedPackageAllocation>(StringComparer.Ordinal);
        if (orderedQuantities.Count > 0 && remoteAllocations.Count == 0)
        {
            normalized = values;
            return false;
        }
        foreach (var remote in remoteAllocations)
        {
            if (!orderedQuantities.TryGetValue(remote.ExternalLineId, out var ordered) ||
                values.ContainsKey(remote.ExternalLineId) ||
                !TryNormalize(ordered, remote, packageStatus, out var safe))
            {
                normalized = values;
                return false;
            }
            values.Add(remote.ExternalLineId, safe);
        }
        normalized = values;
        return true;
    }

    public static bool TryNormalizeOrder(
        IReadOnlyDictionary<string, decimal> orderedQuantities,
        IReadOnlyList<RemotePackage> remotePackages,
        out IReadOnlyDictionary<string, NormalizedPackageAllocation> normalized)
    {
        var latestPackages = remotePackages
            .Where(package => !string.IsNullOrWhiteSpace(package.ExternalPackageId))
            .GroupBy(package => package.ExternalPackageId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(package => package.OccurredAt).First())
            .ToList();
        var replacedExternalIds = latestPackages
            .Where(package => !string.IsNullOrWhiteSpace(package.OriginExternalPackageId)
                && latestPackages.Any(candidate => string.Equals(candidate.ExternalPackageId, package.OriginExternalPackageId, StringComparison.Ordinal)))
            .Select(package => package.OriginExternalPackageId!)
            .ToHashSet(StringComparer.Ordinal);

        var totals = new Dictionary<string, NormalizedPackageAllocation>(StringComparer.Ordinal);
        if (orderedQuantities.Count > 0 && latestPackages.Count == 0)
        {
            normalized = totals;
            return false;
        }

        foreach (var package in latestPackages.Where(package => !replacedExternalIds.Contains(package.ExternalPackageId)))
        {
            if (!TryNormalizeAll(
                    orderedQuantities,
                    package.Allocations,
                    ShipmentPackageStatusPolicy.FromRemote(package.RawStatus),
                    out var packageAllocations))
            {
                normalized = totals;
                return false;
            }

            foreach (var (lineId, allocation) in packageAllocations)
            {
                var current = totals.GetValueOrDefault(lineId) ?? new NormalizedPackageAllocation(0, 0, 0, 0, 0);
                totals[lineId] = new(
                    current.ActiveAllocatedQuantity + allocation.ActiveAllocatedQuantity,
                    current.CancelledQuantity + allocation.CancelledQuantity,
                    current.ShippedQuantity + allocation.ShippedQuantity,
                    current.DeliveredQuantity + allocation.DeliveredQuantity,
                    current.ReturnedQuantity + allocation.ReturnedQuantity);
            }
        }

        foreach (var (lineId, orderedQuantity) in orderedQuantities)
        {
            if (!totals.TryGetValue(lineId, out var total)
                || !OrderQuantityInvariant.IsValid(
                    orderedQuantity,
                    total.ActiveAllocatedQuantity,
                    total.CancelledQuantity,
                    total.ShippedQuantity,
                    total.DeliveredQuantity,
                    total.ReturnedQuantity))
            {
                normalized = totals;
                return false;
            }
        }

        normalized = totals;
        return true;
    }
}
