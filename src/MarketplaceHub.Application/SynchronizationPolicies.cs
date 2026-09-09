using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public static class SynchronizationCadence
{
    public static readonly TimeSpan HotOrders = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HotReturns = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan OpenOrderLifecycle = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan OpenReturnLifecycle = TimeSpan.FromMinutes(3);
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
        => DesiredQuantity(orderedQuantity, cancelledQuantity, 0m, true);

    public static decimal DesiredQuantity(
        decimal orderedQuantity,
        decimal cancelledQuantity,
        decimal shippedQuantity,
        bool reservationEnabled)
    {
        if (orderedQuantity < 0 || cancelledQuantity < 0 || shippedQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(orderedQuantity));
        if (!reservationEnabled) return 0m;

        var afterCancellation = Math.Max(0m, orderedQuantity - Math.Min(orderedQuantity, cancelledQuantity));
        var consumed = Math.Min(afterCancellation, shippedQuantity);
        return Math.Max(0m, afterCancellation - consumed);
    }

    public static bool IsReservationEnabled(ConnectionInventoryPolicy? policy, string? rawStatus)
    {
        if (policy is null) return true;
        if (string.Equals(policy.ReservationMode?.Trim(), "NONE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(policy.ReservationMode?.Trim(), "DISABLED", StringComparison.OrdinalIgnoreCase))
            return false;

        var status = rawStatus?.Trim() ?? string.Empty;
        if (ContainsStatus(policy.ReleaseOnStatuses, status)) return false;
        return string.IsNullOrWhiteSpace(policy.ReserveOnStatuses)
            || ContainsStatus(policy.ReserveOnStatuses, status);
    }

    private static bool ContainsStatus(string? configuredStatuses, string rawStatus)
    {
        if (string.IsNullOrWhiteSpace(configuredStatuses) || string.IsNullOrWhiteSpace(rawStatus)) return false;
        return configuredStatuses
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(status => string.Equals(status, rawStatus, StringComparison.OrdinalIgnoreCase));
    }
}

public static class InventoryAuthorityPolicy
{
    public const string Central = "CENTRAL";
    public const string RemoteObservation = "REMOTE_OBSERVATION";
    public const string RemoteAuthoritative = "REMOTE_AUTHORITATIVE";

    public static string Normalize(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        RemoteObservation => RemoteObservation,
        RemoteAuthoritative => RemoteAuthoritative,
        _ => Central
    };

    // Remote-authoritative mode is an explicit compatibility escape hatch. It
    // is never inferred from a missing/unknown policy, so catalog imports cannot
    // silently replace local physical stock.
    public static bool ShouldApplyRemoteQuantityToOnHand(string? authorityMode) =>
        Normalize(authorityMode) == RemoteAuthoritative;
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

public static class PriceInventoryOutboxPolicy
{
    public static bool IsCurrent(PriceInventoryPushLine line, long projectionVersion, long priceVersion) =>
        line.ProjectionVersion == projectionVersion && line.PriceVersion == priceVersion;

    public static string DedupKey(Guid connectionId, IEnumerable<PriceInventoryPushLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var revision = string.Join("\n", lines
            .OrderBy(line => line.OfferId)
            .ThenBy(line => line.VariantId)
            .Select(line => string.Join('|',
                line.VariantId.ToString("N"),
                line.OfferId.ToString("N"),
                line.Barcode,
                line.Quantity.ToString("0.####", CultureInfo.InvariantCulture),
                line.ListPrice.ToString("0.####", CultureInfo.InvariantCulture),
                line.SalePrice.ToString("0.####", CultureInfo.InvariantCulture),
                line.Currency,
                line.ProjectionVersion.ToString(CultureInfo.InvariantCulture),
                line.PriceVersion.ToString(CultureInfo.InvariantCulture),
                line.PriceHash)));
        var revisionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision)));
        return $"price-inventory:{connectionId:N}:revision:{revisionHash}";
    }
}
