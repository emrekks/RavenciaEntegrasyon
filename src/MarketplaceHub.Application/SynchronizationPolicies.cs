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

public static class TrendyolOrderHistoryPolicy
{
    public const int StreamInitialLookbackMonths = 3;
    public const int LegacyEndpointMaxHistoryDays = 30;

    public static DateTimeOffset StreamInitialStart(DateTimeOffset anchor) =>
        anchor.AddMonths(-StreamInitialLookbackMonths);

    public static DateTimeOffset LegacyEndpointInitialStart(DateTimeOffset anchor) =>
        anchor.AddDays(-LegacyEndpointMaxHistoryDays);
}

// n11 lists shipment packages by last modification in windows of at most 15
// days and has no data before November 2024. A full scan reads the last three
// months like Trendyol; incremental reads start at the watermark minus overlap.
public static class N11OrderHistoryPolicy
{
    public const int InitialLookbackMonths = 3;
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(15);
    public static readonly DateTimeOffset EarliestAvailable = new(2024, 11, 1, 0, 0, 0, TimeSpan.FromHours(3));

    public static DateTimeOffset Start(bool full, DateTimeOffset? watermark, TimeSpan overlap, DateTimeOffset now)
    {
        var start = full || watermark is null ? now.AddMonths(-InitialLookbackMonths) : watermark.Value - overlap;
        return start < EarliestAvailable ? EarliestAvailable : start;
    }

    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Windows(DateTimeOffset start, DateTimeOffset end)
    {
        var windows = new List<(DateTimeOffset, DateTimeOffset)>();
        for (var cursor = start; cursor < end; cursor += MaxWindow)
            windows.Add((cursor, cursor + MaxWindow < end ? cursor + MaxWindow : end));
        return windows;
    }
}

// Pazarama lists orders by order date only (no modification filter) in windows of at most one
// month; the end date is exclusive. A full scan reads the last three months; incremental runs
// re-read a short fixed lookback because the day filter cannot name only the new orders.
// Status changes of older orders come from the open-order lifecycle scan.
public static class PazaramaOrderHistoryPolicy
{
    public const int InitialLookbackMonths = 3;
    public const int IncrementalLookbackDays = 2;
    public const int PageSize = 100;
    // Pazarama refuses ranges above one month; 28 days keeps a safe margin.
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(28);
    public static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);

    // First day (Turkey time) the run reads.
    public static DateTime StartDay(bool full, DateTimeOffset? watermark, DateTimeOffset now)
    {
        var today = now.ToOffset(TurkeyOffset).Date;
        if (full || watermark is null) return today.AddMonths(-InitialLookbackMonths);
        var fromWatermark = watermark.Value.ToOffset(TurkeyOffset).Date.AddDays(-IncrementalLookbackDays);
        var floor = today.AddDays(-IncrementalLookbackDays);
        return fromWatermark < floor ? fromWatermark : floor;
    }

    // Consecutive [start, end) day windows up to tomorrow (the end day is excluded by Pazarama).
    public static IReadOnlyList<(DateTime Start, DateTime End)> Windows(DateTime startDay, DateTimeOffset now)
    {
        var end = now.ToOffset(TurkeyOffset).Date.AddDays(1);
        var windows = new List<(DateTime, DateTime)>();
        for (var cursor = startDay; cursor < end; cursor = cursor.Add(MaxWindow))
            windows.Add((cursor, cursor.Add(MaxWindow) < end ? cursor.Add(MaxWindow) : end));
        return windows;
    }
}

public static class HepsiburadaOrderHistoryPolicy
{
    public const int DefaultInitialLookbackMonths = 1;

    public static DateTimeOffset InitialWindowStart(DateTimeOffset anchor) =>
        anchor.AddMonths(-DefaultInitialLookbackMonths);

    public static bool ShouldImportPaidOrder(string? lifecycleStatus, DateTimeOffset orderedAt, DateTimeOffset anchor)
    {
        // The paid-order feed returns Open/Unpacked rows. The package status
        // feeds cover only the last month, so an older package-less order
        // cannot be verified even when its line has just become Unpacked.
        var status = lifecycleStatus?.Trim();
        return orderedAt >= InitialWindowStart(anchor)
            && (string.Equals(status, "OPEN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "UNPACKED", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldImportPaidOrder(RemoteOrder order, DateTimeOffset anchor) =>
        ShouldImportPaidOrder(order.LifecycleStatus, order.OrderedAt, anchor)
        && (order.Packages.Count > 0 || order.Lines.Count > 0 &&
            !order.Lines.All(line => string.Equals(line.RawStatus?.Trim(), "ClaimCreated", StringComparison.OrdinalIgnoreCase)));

    public static DateTimeOffset ClampWindowStart(DateTimeOffset anchor, DateTimeOffset candidate)
    {
        var oldest = InitialWindowStart(anchor);
        if (candidate > anchor) return anchor;
        return candidate < oldest ? oldest : candidate;
    }
}

public enum MarketplaceSyncHealth { Healthy, Delayed, Degraded, Offline }

public static class HepsiburadaReturnHistoryPolicy
{
    public static readonly TimeSpan InitialLookback = TimeSpan.FromDays(30);

    public static DateTimeOffset InitialStatusChangeStart(DateTimeOffset anchor) => anchor - InitialLookback;

    public static bool IsOrderWithinReturnHistory(DateTimeOffset? orderCreatedAt, DateTimeOffset anchor) =>
        orderCreatedAt is { } orderedAt && orderedAt >= InitialStatusChangeStart(anchor) && orderedAt <= anchor;
}

public static class MarketplaceSyncHealthPolicy
{
    public static MarketplaceSyncHealth Classify(
        DateTimeOffset? lastSuccessAt,
        DateTimeOffset now,
        TimeSpan delayedAfter,
        TimeSpan degradedAfter,
        TimeSpan offlineAfter,
        int consecutiveFailureCount = 0,
        int lastFailedCount = 0,
        TimeSpan? expectedCadence = null)
    {
        if (delayedAfter <= TimeSpan.Zero || degradedAfter <= delayedAfter || offlineAfter <= degradedAfter)
            throw new ArgumentException("Sync health thresholds must be strictly increasing.");
        if (consecutiveFailureCount < 0 || lastFailedCount < 0)
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailureCount), "Sync failure counts cannot be negative.");
        if (expectedCadence < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expectedCadence), "Expected sync cadence cannot be negative.");
        if (lastSuccessAt is null) return MarketplaceSyncHealth.Offline;
        var age = now - lastSuccessAt.Value;
        var cadence = expectedCadence ?? TimeSpan.Zero;
        if (age >= cadence + offlineAfter) return MarketplaceSyncHealth.Offline;
        if (consecutiveFailureCount > 0 || lastFailedCount > 0) return MarketplaceSyncHealth.Degraded;
        if (age < cadence + delayedAfter) return MarketplaceSyncHealth.Healthy;
        if (age < cadence + degradedAfter) return MarketplaceSyncHealth.Delayed;
        return MarketplaceSyncHealth.Degraded;
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

public enum MarketplaceCursorProgressStatus { Unknown, NoData, Current, Stalled }

public static class MarketplaceCursorProgressPolicy
{
    public static MarketplaceCursorProgressStatus Classify(
        DateTimeOffset? lastSuccessAt,
        DateTimeOffset? lastCursorAdvancedAt,
        DateTimeOffset? cursorStagnantSince,
        int lastReceivedCount,
        DateTimeOffset now,
        TimeSpan expectedCadence,
        TimeSpan minimumStallAfter)
    {
        if (lastReceivedCount < 0) throw new ArgumentOutOfRangeException(nameof(lastReceivedCount));
        if (expectedCadence < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expectedCadence));
        if (minimumStallAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumStallAfter));
        if (lastSuccessAt is null) return MarketplaceCursorProgressStatus.Unknown;
        if (lastReceivedCount == 0) return MarketplaceCursorProgressStatus.NoData;
        if (cursorStagnantSince is null)
            return lastCursorAdvancedAt is null ? MarketplaceCursorProgressStatus.Unknown : MarketplaceCursorProgressStatus.Current;

        var threeCyclesTicks = expectedCadence.Ticks > TimeSpan.MaxValue.Ticks / 3
            ? TimeSpan.MaxValue.Ticks
            : expectedCadence.Ticks * 3;
        var threeCycles = TimeSpan.FromTicks(threeCyclesTicks);
        var staleAfter = threeCycles > minimumStallAfter ? threeCycles : minimumStallAfter;
        return now - cursorStagnantSince.Value > staleAfter
            ? MarketplaceCursorProgressStatus.Stalled
            : MarketplaceCursorProgressStatus.Current;
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
        // Reservations are opt-in. Without an explicit connection policy,
        // order synchronization must not create reserved stock implicitly.
        if (policy is null) return false;
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

public static class OrderInventoryStockPolicy
{
    public static decimal TargetCommittedQuantity(decimal orderedQuantity, decimal cancelledQuantity, decimal shippedQuantity)
    {
        if (orderedQuantity < 0 || cancelledQuantity < 0 || shippedQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(orderedQuantity));

        var ordered = orderedQuantity;
        var active = Math.Max(0m, ordered - Math.Min(ordered, cancelledQuantity));
        var shipped = Math.Min(ordered, shippedQuantity);
        return Math.Max(active, shipped);
    }

    // Shipments recorded before order-time stock deduction already reduced
    // physical stock. Treat them as the opening baseline, not as a new debit.
    public static decimal InitialBaselineQuantity(decimal legacyShippedQuantity, decimal observedShippedQuantity)
    {
        if (legacyShippedQuantity < 0 || observedShippedQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(legacyShippedQuantity));
        return Math.Max(legacyShippedQuantity, observedShippedQuantity);
    }

    // Ledger deltas use the stock direction: negative means stock was consumed,
    // positive means it was restored. The baseline is not a new stock movement.
    public static decimal OnHandDelta(decimal baselineQuantity, decimal receivedLedgerDelta, decimal targetCommittedQuantity)
    {
        if (baselineQuantity < 0 || targetCommittedQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(baselineQuantity));
        var currentlyCommitted = Math.Max(0m, baselineQuantity - receivedLedgerDelta);
        return currentlyCommitted - targetCommittedQuantity;
    }

    public static decimal LimitToAvailableStock(decimal onHand, decimal available, decimal sourceReservation, decimal requestedDelta, bool negativeStockAllowed)
    {
        if (available < 0 || sourceReservation < 0)
            throw new ArgumentOutOfRangeException(nameof(available));
        if (requestedDelta >= 0 || negativeStockAllowed) return requestedDelta;
        var consumable = Math.Max(0m, Math.Min(onHand, available + sourceReservation));
        return Math.Max(requestedDelta, -consumable);
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

    // An untouched inventory row created by the catalog importer has no local
    // physical-stock decision behind it yet. Seed that first projection from
    // the catalog snapshot; later imports still follow the configured
    // authority mode and cannot silently overwrite local stock.
    public static bool ShouldSeedInitialOnHand(InventoryItem inventory) =>
        inventory.OnHand == 0m
        && inventory.Reserved == 0m
        && inventory.Version == 1
        && inventory.ProjectionVersion == 1;
}

public static class ProductImportMergePolicy
{
    public static bool PreserveLocalChanges(long productVersion, long lastImportedProductVersion) =>
        lastImportedProductVersion > 0 && productVersion > lastImportedProductVersion;

    public static bool PreserveLocalChanges(long productVersion, long lastImportedProductVersion, string? dirtyFieldsJson) =>
        PreserveLocalChanges(productVersion, lastImportedProductVersion)
        || !string.IsNullOrWhiteSpace(dirtyFieldsJson) && !string.Equals(dirtyFieldsJson.Trim(), "[]", StringComparison.Ordinal);
}

public static class ProductImportScanPolicy
{
    // An inactive-product mapping is a backfill. It must not inherit the
    // incremental cursor because older archived/draft products may never
    // appear in a modified-since page again.
    public static bool RequiresFullCatalogForMapping(bool mappingOnly, bool includeArchived, bool includeDrafts) =>
        mappingOnly && (includeArchived || includeDrafts);
}

public static class ProductImportConcurrencyPolicy
{
    public static bool IsProductImportJob(string jobType) =>
        string.Equals(jobType, "PRODUCT_SYNC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(jobType, "SHOPIFY_PRODUCT_SYNC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(jobType, "HEPSIBURADA_PRODUCT_SYNC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(jobType, "CATALOG_STRUCTURE_IMPORT", StringComparison.OrdinalIgnoreCase);

    public static bool RejectsModeCollision(string requestedJobType, string activeJobType) =>
        IsProductImportJob(requestedJobType) && IsProductImportJob(activeJobType);
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

public sealed record TrendyolStorefrontActivity(DateTimeOffset? LastOrderAt, DateTimeOffset? LastProbedAt, DateTimeOffset? PausedUntil = null);

// Trendyol serves international orders under separate storefront headers, and every stream
// request is spaced five seconds apart. Order reads therefore visit Türkiye and the storefronts
// that had an order in the last 90 days; each other storefront is probed now and then (one per
// run) over the time since its last probe, so a storefront that starts selling becomes active.
public static class TrendyolStorefrontPolicy
{
    public const string CursorResource = "TRENDYOL_STOREFRONTS";
    public const string Home = "TR";
    // Storefronts in Trendyol's published list; the others were read so far but are not listed.
    public static readonly string[] Listed = ["TR", "DE", "SA", "AE", "GR", "SK", "RO", "CZ", "BG", "KW"];
    public static readonly TimeSpan ActiveFor = TimeSpan.FromDays(90);
    public static readonly TimeSpan ListedProbeInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan UnlistedProbeInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan UnlistedRejectionPause = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaximumProbeWindow = TimeSpan.FromDays(14);

    public static bool IsListed(string code) => Listed.Contains(code, StringComparer.OrdinalIgnoreCase);

    // Storefronts read on every run: Türkiye and those with an order in the last 90 days.
    public static IReadOnlyList<string> Active(IReadOnlyList<string> codes, IReadOnlyDictionary<string, TrendyolStorefrontActivity> activity, DateTimeOffset now) =>
        codes.Where(code => string.Equals(code, Home, StringComparison.OrdinalIgnoreCase)
            || activity.TryGetValue(code, out var seen)
                && seen.LastOrderAt is { } lastOrder && now - lastOrder <= ActiveFor
                && !(seen.PausedUntil > now))
            .ToArray();

    // Storefronts read by a full scan: every storefront except those paused after a rejection.
    public static IReadOnlyList<string> Readable(IReadOnlyList<string> codes, IReadOnlyDictionary<string, TrendyolStorefrontActivity> activity, DateTimeOffset now) =>
        codes.Where(code => string.Equals(code, Home, StringComparison.OrdinalIgnoreCase)
            || !(activity.TryGetValue(code, out var seen) && seen.PausedUntil > now))
            .ToArray();

    // The inactive storefront whose probe is longest overdue, or none.
    public static string? NextProbe(IReadOnlyList<string> codes, IReadOnlyDictionary<string, TrendyolStorefrontActivity> activity, DateTimeOffset now)
    {
        var active = Active(codes, activity, now);
        return codes
            .Where(code => !active.Contains(code, StringComparer.OrdinalIgnoreCase))
            .Select(code => (Code: code, Seen: activity.GetValueOrDefault(code)))
            .Where(item => !(item.Seen?.PausedUntil > now)
                && (item.Seen?.LastProbedAt is not { } probed
                    || now - probed >= (IsListed(item.Code) ? ListedProbeInterval : UnlistedProbeInterval)))
            .OrderBy(item => item.Seen?.LastProbedAt ?? DateTimeOffset.MinValue)
            .Select(item => item.Code)
            .FirstOrDefault();
    }

    // A probe reads the time since the storefront's last probe, at most one stream window.
    public static DateTimeOffset ProbeStart(TrendyolStorefrontActivity? seen, DateTimeOffset now, TimeSpan overlap)
    {
        var oldest = now - MaximumProbeWindow;
        var start = (seen?.LastProbedAt ?? oldest) - overlap;
        return start < oldest ? oldest : start;
    }

    public static TrendyolStorefrontActivity Read(TrendyolStorefrontActivity? seen, DateTimeOffset now, bool receivedOrders) =>
        new(receivedOrders ? now : seen?.LastOrderAt, now, null);

    // A storefront the seller does not have answers 400/404. Listed ones are simply probed again
    // later; unlisted ones are left alone for 30 days.
    public static TrendyolStorefrontActivity Rejected(string code, TrendyolStorefrontActivity? seen, DateTimeOffset now) =>
        new(seen?.LastOrderAt, now, IsListed(code) ? null : now + UnlistedRejectionPause);
}

// Where a targeted Trendyol read looks. Order V2 keeps one month of packages and takes at most
// two weeks per request; the order stream keeps three months and filters by last change.
public static class TrendyolLookupPolicy
{
    public const string HomeStoreFront = "TR";
    public static readonly TimeSpan V2History = TimeSpan.FromDays(30);
    public static readonly TimeSpan V2MaximumSpan = TimeSpan.FromDays(14);
    public static readonly TimeSpan StreamMargin = TimeSpan.FromDays(6);

    // A known order is read from its own storefront; an unknown one from every storefront, Türkiye first.
    public static IReadOnlyList<string> Storefronts(OrderLookupHint? hint, IReadOnlyList<string> codes) =>
        hint?.StoreFrontCode is { Length: > 0 } code ? [code.Trim().ToUpperInvariant()] : codes;

    // The last two weeks for an unknown or recent order, the two weeks from its order date for a
    // two-to-four-week-old order, and no V2 read at all for an order older than a month.
    public static (DateTimeOffset Start, DateTimeOffset End)? V2Window(OrderLookupHint? hint, DateTimeOffset now)
    {
        if (hint?.OrderedAt is not { } orderedAt || orderedAt >= now - V2MaximumSpan) return (now - V2MaximumSpan, now);
        var oldest = now - V2History;
        if (orderedAt < oldest) return null;
        var start = orderedAt.AddDays(-1);
        if (start < oldest) start = oldest;
        var end = start + V2MaximumSpan;
        return (start, end > now ? now : end);
    }

    // A known order V2 cannot serve is read from the stream six days either side of its last
    // recorded change; an order whose last change is older than the stream keeps is not read.
    public static (DateTimeOffset Start, DateTimeOffset End)? StreamWindow(OrderLookupHint? hint, DateTimeOffset now)
    {
        if ((hint?.LastModifiedAt ?? hint?.OrderedAt) is not { } anchor) return null;
        var oldest = now.AddMonths(-TrendyolOrderHistoryPolicy.StreamInitialLookbackMonths);
        if (anchor < oldest || anchor > now) return anchor > now ? (now - StreamMargin, now) : null;
        var start = anchor - StreamMargin;
        var end = anchor + StreamMargin;
        return (start < oldest ? oldest : start, end > now ? now : end);
    }
}
