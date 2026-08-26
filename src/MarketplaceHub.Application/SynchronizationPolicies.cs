namespace MarketplaceHub.Application;

public static class SynchronizationCadence
{
    public static readonly TimeSpan HotOrders = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HotReturns = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan OpenOrderLifecycle = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan OpenReturnLifecycle = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ProductCatalog = TimeSpan.FromMinutes(15);
}

public static class SynchronizationWindowPolicy
{
    public static (DateTimeOffset Start, DateTimeOffset End) Incremental(
        DateTimeOffset? lastSuccessfulSync,
        DateTimeOffset now,
        TimeSpan overlap,
        TimeSpan? maximumHistory = null)
    {
        if (overlap < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(overlap));
        var start = (lastSuccessfulSync ?? now).Subtract(overlap);
        if (maximumHistory is { } history)
        {
            if (history <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumHistory));
            var oldest = now.Subtract(history);
            if (start < oldest) start = oldest;
        }
        if (start > now) start = now.Subtract(overlap);
        return (start, now);
    }

    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ForwardChunks(
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan maximumSpan)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        if (maximumSpan <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumSpan));
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        var cursor = start;
        while (cursor < end)
        {
            var next = cursor.Add(maximumSpan);
            if (next > end) next = end;
            result.Add((cursor, next));
            cursor = next;
        }
        return result;
    }
}

public enum MarketplaceSyncHealth { Healthy, Delayed, Degraded, Offline }

public static class MarketplaceSyncHealthPolicy
{
    public static MarketplaceSyncHealth Classify(
        DateTimeOffset? lastSuccessAt,
        DateTimeOffset now,
        TimeSpan delayedAfter,
        TimeSpan degradedAfter,
        TimeSpan offlineAfter)
    {
        if (delayedAfter <= TimeSpan.Zero || degradedAfter <= delayedAfter || offlineAfter <= degradedAfter)
            throw new ArgumentException("Sync health thresholds must be strictly increasing.");
        if (lastSuccessAt is null) return MarketplaceSyncHealth.Offline;
        var age = now - lastSuccessAt.Value;
        if (age < delayedAfter) return MarketplaceSyncHealth.Healthy;
        if (age < degradedAfter) return MarketplaceSyncHealth.Delayed;
        return age < offlineAfter ? MarketplaceSyncHealth.Degraded : MarketplaceSyncHealth.Offline;
    }

    public static (string Status, double? Days) RecoveryGap(
        DateTimeOffset? lastModifiedWatermark,
        DateTimeOffset now,
        TimeSpan warningAfter,
        TimeSpan criticalAfter)
    {
        if (warningAfter <= TimeSpan.Zero || criticalAfter <= warningAfter)
            throw new ArgumentException("Recovery gap thresholds must be strictly increasing.");
        if (lastModifiedWatermark is null) return ("UNKNOWN", null);
        var age = now - lastModifiedWatermark.Value;
        var days = Math.Max(0, age.TotalDays);
        if (age > criticalAfter) return ("CRITICAL", days);
        if (age > warningAfter) return ("WARNING", days);
        return ("OK", days);
    }
}

public static class OrderInventoryReservationPolicy
{
    public static decimal DesiredQuantity(decimal orderedQuantity, decimal cancelledQuantity)
    {
        if (orderedQuantity < 0 || cancelledQuantity < 0) throw new ArgumentOutOfRangeException(nameof(orderedQuantity));
        return Math.Max(0, orderedQuantity - Math.Min(orderedQuantity, cancelledQuantity));
    }
}

public static class ProductImportMergePolicy
{
    public static bool PreserveLocalChanges(long productVersion, long lastImportedProductVersion) =>
        lastImportedProductVersion > 0 && productVersion > lastImportedProductVersion;

    public static bool PreserveLocalChanges(long productVersion, long lastImportedProductVersion, string? dirtyFieldsJson) =>
        PreserveLocalChanges(productVersion, lastImportedProductVersion)
        || !string.IsNullOrWhiteSpace(dirtyFieldsJson) && !string.Equals(dirtyFieldsJson.Trim(), "[]", StringComparison.Ordinal);
}

public static class ProductUpdatePollingPolicy
{
    public static TimeSpan Delay(DateTimeOffset submittedAt, DateTimeOffset now) =>
        Delay(submittedAt, now, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30));

    public static TimeSpan Delay(
        DateTimeOffset submittedAt,
        DateTimeOffset now,
        TimeSpan firstWindow,
        TimeSpan secondWindow,
        TimeSpan thirdWindow,
        TimeSpan firstDelay,
        TimeSpan secondDelay,
        TimeSpan thirdDelay,
        TimeSpan finalDelay)
    {
        if (firstWindow <= TimeSpan.Zero || secondWindow <= firstWindow || thirdWindow <= secondWindow || firstDelay <= TimeSpan.Zero || secondDelay <= TimeSpan.Zero || thirdDelay <= TimeSpan.Zero || finalDelay <= TimeSpan.Zero)
            throw new ArgumentException("Product update polling windows and delays must be positive and ordered.");
        var age = now - submittedAt;
        if (age < firstWindow) return firstDelay;
        if (age < secondWindow) return secondDelay;
        if (age < thirdWindow) return thirdDelay;
        return finalDelay;
    }
}

public static class StockProjectionOutboxPolicy
{
    public static string DedupKey(Guid connectionId, Guid variantId, long projectionVersion) =>
        $"stock-projection:{connectionId:N}:{variantId:N}:v{projectionVersion}";
}
