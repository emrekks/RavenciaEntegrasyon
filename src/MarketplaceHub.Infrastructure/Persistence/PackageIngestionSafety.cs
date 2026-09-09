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

        var currentPackages = packageByExternalId.Values
            .Where(package => !replacedExternalIds.Contains(package.ExternalPackageId));
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
        DateTimeOffset incomingOccurredAt) =>
        incomingOccurredAt >= currentOccurredAt && ShipmentPackageStateMachine.CanTransition(current, incoming);

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
        var shipped = packageStatus is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.ReturnInTransit or ShipmentPackageStatus.Returned
            ? active
            : remote.ShippedQuantity;
        var delivered = packageStatus is ShipmentPackageStatus.Delivered or ShipmentPackageStatus.ReturnInTransit or ShipmentPackageStatus.Returned
            ? active
            : remote.DeliveredQuantity;
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
