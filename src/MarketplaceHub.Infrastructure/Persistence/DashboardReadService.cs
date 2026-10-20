using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class DashboardReadService(AppDbContext db, TimeProvider timeProvider) : IDashboardReadService
{
    private static readonly JsonSerializerOptions BreakdownJson = new() { PropertyNameCaseInsensitive = true };

    public async Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var snapshot = await db.DashboardSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (snapshot is null)
        {
            // The worker keeps the snapshot fresh; only a tenant that never had one is built here, once.
            try { await RebuildTenantAsync(tenantId, cancellationToken); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
            snapshot = await db.DashboardSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        }

        var channels = await ChannelsAsync(tenantId, cancellationToken);
        if (snapshot is null) return new(EmptyMetrics(), channels, timeProvider.GetUtcNow(), 0);
        var breakdown = ReadBreakdown(snapshot.PendingByPlatformJson);
        return new(
            new DashboardMetricsView(snapshot.PendingOrders, snapshot.LateOrders, snapshot.PendingReturns, breakdown.Pending, breakdown.Late, breakdown.Returns,
                snapshot.DeadJobCount, snapshot.ManualReviewJobCount, snapshot.RecentJobCount),
            channels,
            snapshot.UpdatedAt,
            snapshot.Version);
    }

    private Task<List<DashboardChannelView>> ChannelsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(x.Status))
            .OrderBy(x => x.DisplayName)
            .Select(x => new DashboardChannelView(x.Id, x.PlatformCode, x.DisplayName, x.Status))
            .ToListAsync(cancellationToken);

    // The warehouse queue: orders waiting to be packed or shipped. The same query gives the card total and its
    // per-store breakdown, and the order list's "pending" count (OrderSummaryView.Pending) uses the same rule.
    internal static IQueryable<Order> PendingOrderMetricsQuery(AppDbContext db, Guid tenantId, DateTimeOffset now)
    {
        var query = db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(connection.Status))
            && DashboardMetricPolicy.PendingOrderStatuses.Contains(x.DerivedStatus));
        return query.ExcludeStaleUnpackaged(db, tenantId, now);
    }

    // Pending orders whose ship-by time has passed. Trendyol stores that time three hours ahead (Turkey wall time
    // labelled UTC), so its orders are late only once the stored value is behind now plus that offset.
    internal static IQueryable<Order> LateOrderMetricsQuery(AppDbContext db, Guid tenantId, DateTimeOffset now)
    {
        var wallClockPlatforms = MarketplaceStoredClock.WallClockPlatforms;
        var wallClockNow = now + MarketplaceStoredClock.TurkeyWallClockAhead;
        return PendingOrderMetricsQuery(db, tenantId, now).Where(x => DashboardMetricPolicy.LateOrderStatuses.Contains(x.DerivedStatus)
            && x.ShipmentDueAt != null
            && (x.ShipmentDueAt < now
                || (x.ShipmentDueAt < wallClockNow && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && wallClockPlatforms.Contains(connection.PlatformCode)))));
    }

    // Returns waiting for the seller's decision, with the same Hepsiburada history rule as the return list: a
    // Hepsiburada claim whose order is older than the return history the marketplace still serves is not listed.
    internal static IQueryable<ReturnClaim> PendingReturnMetricsQuery(AppDbContext db, Guid tenantId, DateTimeOffset now)
    {
        var lookbackStart = HepsiburadaReturnHistoryPolicy.InitialStatusChangeStart(now);
        return db.ReturnClaims.AsNoTracking().Where(claim => claim.TenantId == tenantId
            && DashboardMetricPolicy.PendingReturnStatuses.Contains(claim.Status)
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == claim.ConnectionId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(connection.Status))
            && (!db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == claim.ConnectionId && connection.PlatformCode == "HEPSIBURADA")
                || db.Orders.Any(order => order.TenantId == tenantId && order.Id == claim.OrderId && order.OrderedAt >= lookbackStart && order.OrderedAt <= now)));
    }

    public async Task<ServiceResult<DashboardPerformanceView>> PerformanceAsync(Guid tenantId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var timezoneId = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Timezone).SingleOrDefaultAsync(cancellationToken);
        var timezone = ResolveTimezone(timezoneId);
        var resolved = DashboardPerformancePeriod.Resolve(from, to, timeProvider.GetUtcNow(), timezone);
        if (!resolved.Succeeded) return ServiceResult<DashboardPerformanceView>.Fail(resolved.Error!.Code, resolved.Error.Message, resolved.Error.Status);
        var period = resolved.Value!;
        var rows = await PerformanceRowsAsync(tenantId, period, IanaId(timezone), cancellationToken);
        var channels = await ChannelsAsync(tenantId, cancellationToken);
        var current = rows.Where(x => x.Day >= period.From && x.Day <= period.To).ToList();
        var previous = rows.Where(x => x.Day >= period.PreviousFrom && x.Day <= period.PreviousTo).ToList();
        return ServiceResult<DashboardPerformanceView>.Ok(new(period.From, period.To, period.PreviousFrom, period.PreviousTo,
            period.CurrentStart, period.CurrentEnd, period.PreviousStart, period.PreviousEnd, period.PartialDay, channels, current, previous));
    }

    // Both periods in three grouped queries (revenue, product units, packages), one row per day × store × currency.
    // A day is the tenant's calendar day of the real order time (Trendyol's stored time minus its offset).
    internal async Task<List<DashboardPerformanceRowView>> PerformanceRowsAsync(Guid tenantId, DashboardPerformancePeriod period, string timezone, CancellationToken cancellationToken)
    {
        var orders = PerformanceOrders(db, tenantId, period, timezone);
        var revenue = await RevenueQuery(orders).ToListAsync(cancellationToken);
        var units = await UnitQuery(db, orders).ToListAsync(cancellationToken);
        var packages = await PackageQuery(db, orders).ToListAsync(cancellationToken);
        return MergePerformanceRows(revenue, units, packages);
    }

    internal sealed record RevenueGroup(DateTime Day, Guid ConnectionId, string Currency, bool Returned, decimal Amount, int Orders);
    internal sealed record UnitGroup(DateTime Day, Guid ConnectionId, string Currency, decimal Quantity);
    internal sealed record PackageGroup(DateTime Day, Guid ConnectionId, string Currency, int Count);

    internal static IQueryable<RevenueGroup> RevenueQuery(IQueryable<PerformanceOrderRow> orders) => orders
        .GroupBy(x => new { x.Day, x.ConnectionId, x.Currency, x.Returned })
        .Select(group => new RevenueGroup(group.Key.Day, group.Key.ConnectionId, group.Key.Currency, group.Key.Returned, group.Sum(x => x.Amount), group.Count()));

    // Units still ordered: a line's cancelled quantity is not a sold product.
    internal static IQueryable<UnitGroup> UnitQuery(AppDbContext db, IQueryable<PerformanceOrderRow> orders) =>
        from row in orders
        join line in db.OrderLines.AsNoTracking() on new { row.TenantId, OrderId = row.Id } equals new { line.TenantId, line.OrderId }
        where !row.Returned
        group line by new { row.Day, row.ConnectionId, row.Currency } into grouped
        select new UnitGroup(grouped.Key.Day, grouped.Key.ConnectionId, grouped.Key.Currency, grouped.Sum(line => line.OrderedQuantity - line.CancelledQuantity));

    // Packages as the order screens count them: current packages only (no split parents or placeholders), not cancelled.
    internal static IQueryable<PackageGroup> PackageQuery(AppDbContext db, IQueryable<PerformanceOrderRow> orders) =>
        from row in orders
        join package in MarketplaceSalesService.CurrentShipmentPackages(db) on new { row.TenantId, OrderId = row.Id } equals new { package.TenantId, package.OrderId }
        where !row.Returned && package.Status != ShipmentPackageStatus.Cancelled
        group package by new { row.Day, row.ConnectionId, row.Currency } into grouped
        select new PackageGroup(grouped.Key.Day, grouped.Key.ConnectionId, grouped.Key.Currency, grouped.Count());

    internal static List<DashboardPerformanceRowView> MergePerformanceRows(IReadOnlyList<RevenueGroup> revenue, IReadOnlyList<UnitGroup> units, IReadOnlyList<PackageGroup> packages)
    {
        return revenue
            .GroupBy(x => (Day: DateOnly.FromDateTime(x.Day), x.ConnectionId, x.Currency))
            .Select(group =>
            {
                var sold = group.Where(x => !x.Returned).ToArray();
                var returned = group.Where(x => x.Returned).ToArray();
                var key = group.Key;
                return new DashboardPerformanceRowView(
                    key.Day, key.ConnectionId, key.Currency,
                    sold.Sum(x => x.Amount), sold.Sum(x => x.Orders),
                    units.Where(x => DateOnly.FromDateTime(x.Day) == key.Day && x.ConnectionId == key.ConnectionId && x.Currency == key.Currency).Sum(x => x.Quantity),
                    packages.Where(x => DateOnly.FromDateTime(x.Day) == key.Day && x.ConnectionId == key.ConnectionId && x.Currency == key.Currency).Sum(x => x.Count),
                    returned.Sum(x => x.Amount), returned.Sum(x => x.Orders));
            })
            .OrderBy(x => x.Day).ThenBy(x => x.ConnectionId).ThenBy(x => x.Currency)
            .ToList();
    }

    // Set through an object initializer, not a constructor, so the queries composed on it can still be translated.
    internal sealed class PerformanceOrderRow
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public Guid ConnectionId { get; init; }
        public string Currency { get; init; } = "TRY";
        public decimal Amount { get; init; }
        public bool Returned { get; init; }
        public DateTime Day { get; init; }
    }

    internal static IQueryable<PerformanceOrderRow> PerformanceOrders(AppDbContext db, Guid tenantId, DashboardPerformancePeriod period, string timezone)
    {
        var wallClockPlatforms = MarketplaceStoredClock.WallClockPlatforms;
        var ahead = MarketplaceStoredClock.TurkeyWallClockAhead;
        var cancelled = DashboardRevenuePolicy.CancelledStatuses;
        var returnedStatuses = DashboardRevenuePolicy.ReturnedStatuses;
        var previousStart = period.PreviousStart;
        var previousEnd = period.PreviousEnd;
        var currentStart = period.CurrentStart;
        var currentEnd = period.CurrentEnd;
        var scanEnd = period.CurrentEnd + ahead;
        return from order in db.Orders.AsNoTracking()
               join connection in db.PlatformConnections.AsNoTracking() on new { order.TenantId, Id = order.ConnectionId } equals new { connection.TenantId, connection.Id }
               where order.TenantId == tenantId
                   && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(connection.Status)
                   && order.OrderedAt >= previousStart && order.OrderedAt < scanEnd
                   && !cancelled.Contains(order.DerivedStatus)
                   && (order.NetAmount > 0 || order.GrossAmount > 0)
               let realAt = wallClockPlatforms.Contains(connection.PlatformCode) ? order.OrderedAt - ahead : order.OrderedAt
               where (realAt >= previousStart && realAt < previousEnd) || (realAt >= currentStart && realAt < currentEnd)
               // DashboardRevenuePolicy.Amount: what the customer paid; the price before discounts only when no net is known.
               select new PerformanceOrderRow
               {
                   Id = order.Id,
                   TenantId = order.TenantId,
                   ConnectionId = order.ConnectionId,
                   Currency = order.Currency,
                   Amount = order.NetAmount > 0 ? order.NetAmount : order.GrossAmount,
                   Returned = returnedStatuses.Contains(order.DerivedStatus),
                   Day = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(realAt.UtcDateTime, timezone).Date
               };
    }

    public async Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenantId, cancellationToken)) return;
        var now = timeProvider.GetUtcNow();
        var rateLimitWindowStart = now.AddHours(-DashboardMetricPolicy.OperationalRateLimitWindowHours);

        // Counts only: every number is a GROUP BY or COUNT in the database, nothing is loaded row by row.
        var pending = await PendingOrderMetricsQuery(db, tenantId, now).GroupBy(x => x.ConnectionId).Select(x => new { x.Key, Count = x.Count() }).ToListAsync(cancellationToken);
        var late = await LateOrderMetricsQuery(db, tenantId, now).GroupBy(x => x.ConnectionId).Select(x => new { x.Key, Count = x.Count() }).ToListAsync(cancellationToken);
        var returns = await PendingReturnMetricsQuery(db, tenantId, now).GroupBy(x => x.ConnectionId).Select(x => new { x.Key, Count = x.Count() }).ToListAsync(cancellationToken);
        var deadJobCount = await db.IntegrationJobs.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.Status == JobStatus.Dead, cancellationToken);
        var manualReviewJobCount = await db.IntegrationJobs.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.Status == JobStatus.ManualReview, cancellationToken);
        var recentJobCount = await db.IntegrationJobs.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.CreatedAt >= rateLimitWindowStart, cancellationToken);

        var breakdown = new DashboardBreakdown(
            pending.ToDictionary(x => x.Key.ToString("D"), x => x.Count),
            late.ToDictionary(x => x.Key.ToString("D"), x => x.Count),
            returns.ToDictionary(x => x.Key.ToString("D"), x => x.Count));
        var snapshot = await db.DashboardSnapshots.SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (snapshot is null)
        {
            snapshot = new DashboardSnapshot { TenantId = tenantId, Version = 1 };
            db.DashboardSnapshots.Add(snapshot);
        }
        else snapshot.Version++;
        snapshot.PendingOrders = pending.Sum(x => x.Count);
        snapshot.LateOrders = late.Sum(x => x.Count);
        snapshot.PendingReturns = returns.Sum(x => x.Count);
        snapshot.PendingByPlatformJson = WriteBreakdown(breakdown);
        snapshot.DeadJobCount = deadJobCount;
        snapshot.ManualReviewJobCount = manualReviewJobCount;
        snapshot.RecentJobCount = recentJobCount;
        snapshot.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    // The three per-store breakdowns live in the snapshot's one JSON column. A snapshot written before they were
    // grouped by store holds a flat name → count map; it is ignored until the next rebuild.
    internal sealed record DashboardBreakdown(IReadOnlyDictionary<string, int> Pending, IReadOnlyDictionary<string, int> Late, IReadOnlyDictionary<string, int> Returns);

    internal static string WriteBreakdown(DashboardBreakdown breakdown) => JsonSerializer.Serialize(breakdown, BreakdownJson);

    // The writer keeps the record's property names ("Pending"); the check for the per-store shape must not depend on
    // their case (live 20 Oct 2026: it looked for "pending" only, so every card's hover breakdown came back empty).
    internal static DashboardBreakdown ReadBreakdown(string? json)
    {
        var empty = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(json)) return new(empty, empty, empty);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.EnumerateObject().Any(property => string.Equals(property.Name, "pending", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object))
                return new(empty, empty, empty);
            var parsed = JsonSerializer.Deserialize<DashboardBreakdown>(json, BreakdownJson);
            return new(parsed?.Pending ?? empty, parsed?.Late ?? empty, parsed?.Returns ?? empty);
        }
        catch (JsonException) { return new(empty, empty, empty); }
    }

    private static DashboardMetricsView EmptyMetrics() => new(0, 0, 0, new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>());

    // PostgreSQL wants the IANA name; a Windows id from an old setting is converted.
    internal static string IanaId(TimeZoneInfo timezone)
    {
        if (timezone.HasIanaId) return timezone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(timezone.Id, out var iana) ? iana : "UTC";
    }

    internal static TimeZoneInfo ResolveTimezone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (TimeZoneNotFoundException)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timezone == "Europe/Istanbul" ? "Turkey Standard Time" : "Europe/Istanbul"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}

// What the performance panel counts as revenue. Owner decision (20 Oct 2026): revenue is what the customer paid
// (net); cancelled orders are left out; returned orders are left out of revenue and shown on their own line.
public static class DashboardRevenuePolicy
{
    public static readonly string[] CancelledStatuses = ["CANCELLED", "CANCELED"];
    public static readonly string[] ReturnedStatuses = ["RETURNED"];

    public static decimal Amount(decimal netAmount, decimal grossAmount) => netAmount > 0 ? netAmount : grossAmount;
}

// The chosen calendar days and the previous period of equal elapsed time. A period that ends today runs until now,
// so the previous one stops at the same time of day: "today until 10:00" is compared with "yesterday until 10:00".
public sealed record DashboardPerformancePeriod(
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    DateTimeOffset CurrentStart,
    DateTimeOffset CurrentEnd,
    DateTimeOffset PreviousStart,
    DateTimeOffset PreviousEnd,
    bool PartialDay)
{
    public const int MaximumDays = 366;

    public static ServiceResult<DashboardPerformancePeriod> Resolve(DateOnly? from, DateOnly? to, DateTimeOffset now, TimeZoneInfo timezone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timezone).Date);
        var end = to ?? today;
        var start = from ?? end;
        if (start > end) return ServiceResult<DashboardPerformancePeriod>.Fail("REVENUE_RANGE_INVALID", "Başlangıç tarihi bitiş tarihinden sonra olamaz.", 400);
        if (start > today) return ServiceResult<DashboardPerformancePeriod>.Fail("REVENUE_RANGE_INVALID", "Rapor dönemi gelecekte başlayamaz.", 400);
        if (end > today) end = today;
        var days = end.DayNumber - start.DayNumber + 1;
        if (days > MaximumDays) return ServiceResult<DashboardPerformancePeriod>.Fail("REVENUE_RANGE_TOO_LARGE", $"Ciro aralığı en fazla {MaximumDays} gün olabilir.", 400);

        var currentStart = LocalMidnight(start, timezone);
        var dayAfter = LocalMidnight(end.AddDays(1), timezone);
        var partial = dayAfter > now;
        var currentEnd = partial ? now : dayAfter;
        var previousFrom = start.AddDays(-days);
        var previousStart = LocalMidnight(previousFrom, timezone);
        return ServiceResult<DashboardPerformancePeriod>.Ok(new(start, end, previousFrom, end.AddDays(-days),
            currentStart, currentEnd, previousStart, previousStart + (currentEnd - currentStart), partial));
    }

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo timezone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timezone), TimeSpan.Zero);
}
