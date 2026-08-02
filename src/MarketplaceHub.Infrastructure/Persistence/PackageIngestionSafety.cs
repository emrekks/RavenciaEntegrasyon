using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed record NormalizedPackageAllocation(
    decimal ActiveAllocatedQuantity,
    decimal CancelledQuantity,
    decimal ShippedQuantity,
    decimal DeliveredQuantity,
    decimal ReturnedQuantity);

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
        var cancelled = packageStatus == ShipmentPackageStatus.Cancelled ? orderedQuantity : remote.CancelledQuantity;
        var shipped = packageStatus is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.ReturnInTransit or ShipmentPackageStatus.Returned
            ? active
            : remote.ShippedQuantity;
        var delivered = packageStatus is ShipmentPackageStatus.Delivered or ShipmentPackageStatus.ReturnInTransit or ShipmentPackageStatus.Returned
            ? active
            : remote.DeliveredQuantity;
        var returned = packageStatus == ShipmentPackageStatus.Returned ? active : remote.ReturnedQuantity;

        normalized = new(active, cancelled, shipped, delivered, returned);
        return OrderQuantityInvariant.IsValid(orderedQuantity, active, cancelled, shipped, delivered, returned);
    }

    public static bool TryNormalizeAll(
        IReadOnlyDictionary<string, decimal> orderedQuantities,
        IReadOnlyList<RemotePackageAllocation> remoteAllocations,
        ShipmentPackageStatus packageStatus,
        out IReadOnlyDictionary<string, NormalizedPackageAllocation> normalized)
    {
        var values = new Dictionary<string, NormalizedPackageAllocation>(StringComparer.Ordinal);
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
}
