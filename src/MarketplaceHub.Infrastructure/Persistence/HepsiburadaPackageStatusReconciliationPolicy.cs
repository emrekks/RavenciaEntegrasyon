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
}
