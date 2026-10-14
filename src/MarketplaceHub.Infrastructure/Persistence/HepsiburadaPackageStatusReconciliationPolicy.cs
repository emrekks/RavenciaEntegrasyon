using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

public static class HepsiburadaPackageStatusReconciliationPolicy
{
    public const string CursorResourceType = "HEPSIBURADA_PACKAGE_STATUS_RECONCILIATION";
    public const int DefaultBatchSize = 10;
    public const int MaximumBatchSize = 50;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(24);

    public static bool ShouldRun(DateTimeOffset? lastCompletedAt, bool hasContinuation, DateTimeOffset now, TimeSpan interval) =>
        hasContinuation
        || lastCompletedAt is null
        || now - lastCompletedAt.Value >= interval;

    public static bool ShouldAcceptStatusObservation(
        ShipmentPackageStatus currentStatus,
        DateTimeOffset currentStatusTimestamp,
        ShipmentPackageStatus remoteStatus,
        DateTimeOffset remoteStatusTimestamp,
        bool isAuthoritativeObservation)
    {
        if (remoteStatusTimestamp < currentStatusTimestamp) return false;

        // Hepsiburada's current package tracking/status endpoints are the
        // source of truth for its delivery bucket. A newer authoritative
        // Undelivered observation can correct an earlier Delivered projection.
        if (isAuthoritativeObservation
            && currentStatus == ShipmentPackageStatus.Delivered
            && remoteStatus == ShipmentPackageStatus.Undelivered)
            return true;

        return PackageIngestionSafety.ShouldAccept(
            currentStatus,
            currentStatusTimestamp,
            remoteStatus,
            remoteStatusTimestamp);
    }

    public static DateTimeOffset StatusTimestamp(
        ShipmentPackageStatus currentStatus,
        ShipmentPackageStatus remoteStatus,
        DateTimeOffset currentStatusTimestamp,
        DateTimeOffset? remoteStatusTimestamp,
        DateTimeOffset observedAt)
    {
        if (remoteStatus == currentStatus) return currentStatusTimestamp;
        return remoteStatusTimestamp is { } sourceTimestamp && sourceTimestamp > currentStatusTimestamp
            ? sourceTimestamp
            : observedAt;
    }

    public static bool TryHydrateStatusObservationLines(
        RemoteOrder remoteOrder,
        IReadOnlyList<RemoteOrderLine> persistedLines,
        out RemoteOrder hydratedOrder)
    {
        hydratedOrder = remoteOrder;
        if (remoteOrder.Lines.Count != 0
            || remoteOrder.Packages.Count == 0
            || remoteOrder.Packages.Any(package => !package.IsStatusObservation)
            || persistedLines.Count == 0)
            return false;

        hydratedOrder = remoteOrder with { Lines = persistedLines.ToArray() };
        return true;
    }
}
