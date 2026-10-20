using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class DashboardReadService(AppDbContext db, TimeProvider timeProvider) : IDashboardReadService
{
    private static readonly JsonSerializerOptions BreakdownJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly ReturnClaimStatus[] AcceptedClaims = DashboardRevenuePolicy.AcceptedReturnStatuses;
    private static readonly string[] AcceptedLines = DashboardRevenuePolicy.AcceptedReturnLineRawStatuses;

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
        // Every store the report can hold a row for, so a store waiting for credentials or switched off keeps its name.
        var excluded = DashboardRevenuePolicy.ExcludedConnectionStatuses;
        var channels = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !excluded.Contains(x.Status))
            .OrderBy(x => x.DisplayName)
            .Select(x => new DashboardChannelView(x.Id, x.PlatformCode, x.DisplayName, x.Status))
            .ToListAsync(cancellationToken);
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

    // Amount is what was sold and kept; ReturnedAmount what went back (a whole returned order, or the returned part of one).
    internal sealed record RevenueGroup(DateTime Day, Guid ConnectionId, string Currency, decimal Amount, int Orders, decimal ReturnedAmount, int ReturnedOrders);
    internal sealed record UnitGroup(DateTime Day, Guid ConnectionId, string Currency, decimal Quantity);
    internal sealed record PackageGroup(DateTime Day, Guid ConnectionId, string Currency, int Count);

    internal static IQueryable<RevenueGroup> RevenueQuery(IQueryable<PerformanceOrderRow> orders) => orders
        .Select(x => new
        {
            x.Day,
            x.ConnectionId,
            x.Currency,
            Kept = x.Returned ? 0m : (x.Amount > x.ReturnedAmount ? x.Amount - x.ReturnedAmount : 0m),
            Counted = !x.Returned && x.Amount > 0 ? 1 : 0,
            Back = x.Returned ? x.Amount : (x.ReturnedAmount < x.Amount ? x.ReturnedAmount : x.Amount),
            BackCounted = x.Returned || x.ReturnedAmount > 0 ? 1 : 0
        })
        .GroupBy(x => new { x.Day, x.ConnectionId, x.Currency })
        .Select(group => new RevenueGroup(group.Key.Day, group.Key.ConnectionId, group.Key.Currency,
            group.Sum(x => x.Kept), group.Sum(x => x.Counted), group.Sum(x => x.Back), group.Sum(x => x.BackCounted)));

    // Units still ordered and kept: a line's cancelled and returned quantities are not sold products.
    internal static IQueryable<UnitGroup> UnitQuery(AppDbContext db, IQueryable<PerformanceOrderRow> orders) =>
        from row in orders
        join line in db.OrderLines.AsNoTracking() on new { row.TenantId, OrderId = row.Id } equals new { line.TenantId, line.OrderId }
        where !row.Returned
        let active = line.OrderedQuantity - line.CancelledQuantity
        let accepted = db.ReturnLines.Where(returned => returned.TenantId == line.TenantId && returned.OrderLineId == line.Id
                && db.ReturnClaims.Any(claim => claim.TenantId == returned.TenantId && claim.Id == returned.ClaimId
                    && (returned.RawStatus == null || returned.RawStatus == ""
                        ? AcceptedClaims.Contains(claim.Status)
                        : AcceptedLines.Contains(returned.RawStatus.ToUpper()))))
            .Sum(returned => (decimal?)returned.Quantity) ?? 0m
        let back = line.ReturnedQuantity > accepted ? line.ReturnedQuantity : accepted
        group new { active, back } by new { row.Day, row.ConnectionId, row.Currency } into grouped
        select new UnitGroup(grouped.Key.Day, grouped.Key.ConnectionId, grouped.Key.Currency,
            grouped.Sum(x => x.active > x.back ? x.active - x.back : 0m));

    // Packages as the order screens count them: current packages only (no split parents or placeholders), not cancelled
    // and not sent back.
    internal static IQueryable<PackageGroup> PackageQuery(AppDbContext db, IQueryable<PerformanceOrderRow> orders) =>
        from row in orders
        join package in MarketplaceSalesService.CurrentShipmentPackages(db) on new { row.TenantId, OrderId = row.Id } equals new { package.TenantId, package.OrderId }
        where !row.Returned && package.Status != ShipmentPackageStatus.Cancelled && package.Status != ShipmentPackageStatus.Returned
        group package by new { row.Day, row.ConnectionId, row.Currency } into grouped
        select new PackageGroup(grouped.Key.Day, grouped.Key.ConnectionId, grouped.Key.Currency, grouped.Count());

    internal static List<DashboardPerformanceRowView> MergePerformanceRows(IReadOnlyList<RevenueGroup> revenue, IReadOnlyList<UnitGroup> units, IReadOnlyList<PackageGroup> packages)
    {
        return revenue
            .GroupBy(x => (Day: DateOnly.FromDateTime(x.Day), x.ConnectionId, x.Currency))
            .Select(group =>
            {
                var key = group.Key;
                return new DashboardPerformanceRowView(
                    key.Day, key.ConnectionId, key.Currency,
                    group.Sum(x => x.Amount), group.Sum(x => x.Orders),
                    units.Where(x => DateOnly.FromDateTime(x.Day) == key.Day && x.ConnectionId == key.ConnectionId && x.Currency == key.Currency).Sum(x => x.Quantity),
                    packages.Where(x => DateOnly.FromDateTime(x.Day) == key.Day && x.ConnectionId == key.ConnectionId && x.Currency == key.Currency).Sum(x => x.Count),
                    group.Sum(x => x.ReturnedAmount), group.Sum(x => x.ReturnedOrders));
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
        public decimal ReturnedAmount { get; init; }
        public bool Returned { get; init; }
        public DateTime Day { get; init; }
    }

    // One row per order of both periods. The amount is the order's current packages that are not cancelled (a
    // Trendyol order total also holds its cancelled packages); an order whose packages carry no amount keeps the
    // order total. The returned amount is the larger of the packages sent back and the returned share of the lines
    // (returned quantities and accepted return claims), so a partial return is taken off too.
    internal static IQueryable<PerformanceOrderRow> PerformanceOrders(AppDbContext db, Guid tenantId, DashboardPerformancePeriod period, string timezone)
    {
        var wallClockPlatforms = MarketplaceStoredClock.WallClockPlatforms;
        var ahead = MarketplaceStoredClock.TurkeyWallClockAhead;
        var cancelled = DashboardRevenuePolicy.CancelledStatuses;
        var returnedStatuses = DashboardRevenuePolicy.ReturnedStatuses;
        var excludedConnections = DashboardRevenuePolicy.ExcludedConnectionStatuses;
        var acceptedClaims = DashboardRevenuePolicy.AcceptedReturnStatuses;
        var acceptedLines = DashboardRevenuePolicy.AcceptedReturnLineRawStatuses;
        var previousStart = period.PreviousStart;
        var previousEnd = period.PreviousEnd;
        var currentStart = period.CurrentStart;
        var currentEnd = period.CurrentEnd;
        var scanEnd = period.CurrentEnd + ahead;
        var currentPackages = MarketplaceSalesService.CurrentShipmentPackages(db).Where(package => package.TenantId == tenantId);
        var lines = db.OrderLines.AsNoTracking().Where(line => line.TenantId == tenantId);
        return from order in db.Orders.AsNoTracking()
               join connection in db.PlatformConnections.AsNoTracking() on new { order.TenantId, Id = order.ConnectionId } equals new { connection.TenantId, connection.Id }
               // Every store the tenant had counts, also one waiting for new credentials or switched off; only stores
               // whose data was hidden or deleted are left out.
               where order.TenantId == tenantId
                   && !excludedConnections.Contains(connection.Status)
                   && order.OrderedAt >= previousStart && order.OrderedAt < scanEnd
                   && !cancelled.Contains(order.DerivedStatus)
                   && (order.NetAmount > 0 || order.GrossAmount > 0)
               let realAt = wallClockPlatforms.Contains(connection.PlatformCode) ? order.OrderedAt - ahead : order.OrderedAt
               where (realAt >= previousStart && realAt < previousEnd) || (realAt >= currentStart && realAt < currentEnd)
               let priced = currentPackages.Any(package => package.OrderId == order.Id && (package.NetAmount > 0 || package.GrossAmount > 0))
               // DashboardRevenuePolicy.Amount: what the customer paid; the price before discounts only when no net is known.
               let amount = priced
                   ? currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                       .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                   : (order.NetAmount > 0 ? order.NetAmount : order.GrossAmount)
               let packagesBack = priced
                   ? currentPackages.Where(package => package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Returned)
                       .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                   : 0m
               let lineValue = lines.Where(line => line.OrderId == order.Id).Sum(line => line.UnitPrice * (line.OrderedQuantity - line.CancelledQuantity))
               let lineBack = lines.Where(line => line.OrderId == order.Id)
                   .Select(line => new
                   {
                       line.UnitPrice,
                       Active = line.OrderedQuantity - line.CancelledQuantity,
                       line.ReturnedQuantity,
                       Accepted = db.ReturnLines.Where(returned => returned.TenantId == tenantId && returned.OrderLineId == line.Id
                               && db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.Id == returned.ClaimId
                                   && (returned.RawStatus == null || returned.RawStatus == ""
                                       ? acceptedClaims.Contains(claim.Status)
                                       : acceptedLines.Contains(returned.RawStatus.ToUpper()))))
                           .Sum(returned => (decimal?)returned.Quantity) ?? 0m
                   })
                   .Sum(line => line.UnitPrice * Math.Min(line.Active, Math.Max(line.ReturnedQuantity, line.Accepted)))
               let linesBack = lineValue > 0 ? amount * lineBack / lineValue : 0m
               select new PerformanceOrderRow
               {
                   Id = order.Id,
                   TenantId = order.TenantId,
                   ConnectionId = order.ConnectionId,
                   Currency = order.Currency,
                   Amount = amount,
                   ReturnedAmount = packagesBack > linesBack ? packagesBack : linesBack,
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
        snapshot.PendingByPlatformJson = JsonSerializer.Serialize(breakdown, BreakdownJson);
        snapshot.DeadJobCount = deadJobCount;
        snapshot.ManualReviewJobCount = manualReviewJobCount;
        snapshot.RecentJobCount = recentJobCount;
        snapshot.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    // The three per-store breakdowns live in the snapshot's one JSON column. A snapshot written before they were
    // grouped by store holds a flat name → count map; it is ignored until the next rebuild.
    internal sealed record DashboardBreakdown(IReadOnlyDictionary<string, int> Pending, IReadOnlyDictionary<string, int> Late, IReadOnlyDictionary<string, int> Returns);

    internal static DashboardBreakdown ReadBreakdown(string? json)
    {
        var empty = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(json)) return new(empty, empty, empty);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("pending", out var pendingElement) || pendingElement.ValueKind != JsonValueKind.Object)
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
// (net); cancelled orders and packages are left out; what was returned is left out of revenue and shown on its own line.
public static class DashboardRevenuePolicy
{
    public static readonly string[] CancelledStatuses = ["CANCELLED", "CANCELED"];
    public static readonly string[] ReturnedStatuses = ["RETURNED"];
    // A store's past sales stay in the report while it waits for new credentials or is switched off; only a
    // store whose data the owner hid, or deleted, leaves it.
    public static readonly string[] ExcludedConnectionStatuses = ["DELETED", "HIDDEN"];
    // A return claim takes its units off the sale once the return is accepted.
    public static readonly ReturnClaimStatus[] AcceptedReturnStatuses = [ReturnClaimStatus.Approved, ReturnClaimStatus.Completed];
    // The marketplace's own per-line words for an accepted return (upper case), which decide over the claim's status.
    public static readonly string[] AcceptedReturnLineRawStatuses = ["ACCEPTED", "APPROVED", "COMPLETED", "REFUNDED"];

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
