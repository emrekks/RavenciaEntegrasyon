using MarketplaceHub.Api.Dashboard;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class DashboardPerformanceTests
{
    private static readonly TimeZoneInfo Istanbul = DashboardReadService.ResolveTimezone("Europe/Istanbul");

    // 20 Oct 2026, 10:30 in Istanbul.
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 7, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Today_runs_until_now_and_is_compared_with_yesterday_until_the_same_time()
    {
        var period = DashboardPerformancePeriod.Resolve(null, null, Now, Istanbul).Value!;

        Assert.Equal(new DateOnly(2026, 10, 20), period.From);
        Assert.Equal(new DateOnly(2026, 10, 20), period.To);
        Assert.True(period.PartialDay);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 21, 0, 0, TimeSpan.Zero), period.CurrentStart);
        Assert.Equal(Now, period.CurrentEnd);
        Assert.Equal(new DateOnly(2026, 10, 19), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 10, 19), period.PreviousTo);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 21, 0, 0, TimeSpan.Zero), period.PreviousStart);
        Assert.Equal(Now.AddDays(-1), period.PreviousEnd);
    }

    [Fact]
    public void Last_seven_days_compare_with_the_seven_days_before_up_to_the_same_time_of_day()
    {
        var period = DashboardPerformancePeriod.Resolve(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 20), Now, Istanbul).Value!;

        Assert.Equal(new DateOnly(2026, 10, 7), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 10, 13), period.PreviousTo);
        Assert.Equal(period.CurrentEnd - period.CurrentStart, period.PreviousEnd - period.PreviousStart);
        Assert.Equal(Now.AddDays(-7), period.PreviousEnd);
    }

    [Fact]
    public void A_period_in_the_past_compares_whole_days()
    {
        var period = DashboardPerformancePeriod.Resolve(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), Now, Istanbul).Value!;

        Assert.False(period.PartialDay);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.Zero), period.CurrentEnd);
        Assert.Equal(new DateOnly(2026, 9, 21), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 9, 30), period.PreviousTo);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 21, 0, 0, TimeSpan.Zero), period.PreviousEnd);
    }

    [Fact]
    public void A_period_ending_in_the_future_stops_today()
    {
        var period = DashboardPerformancePeriod.Resolve(new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 25), Now, Istanbul).Value!;

        Assert.Equal(new DateOnly(2026, 10, 20), period.To);
        Assert.Equal(Now, period.CurrentEnd);
    }

    [Theory]
    [InlineData("2026-10-10", "2026-10-01", "REVENUE_RANGE_INVALID")]
    [InlineData("2026-10-21", "2026-10-22", "REVENUE_RANGE_INVALID")]
    [InlineData("2025-01-01", "2026-10-20", "REVENUE_RANGE_TOO_LARGE")]
    public void Reversed_future_or_too_long_ranges_are_refused(string from, string to, string code)
    {
        var result = DashboardPerformancePeriod.Resolve(DateOnly.Parse(from), DateOnly.Parse(to), Now, Istanbul);

        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(400, result.Error.Status);
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("2026-10-20", true, "2026-10-20")]
    [InlineData("20.10.2026", false, null)]
    [InlineData("2026-10-20T00:00:00Z", false, null)]
    public void Endpoint_reads_calendar_days_only(string? value, bool valid, string? expected)
    {
        Assert.Equal(valid, DashboardEndpoints.TryDay(value, out var day));
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), day);
    }

    [Fact]
    public void Revenue_is_what_the_customer_paid()
    {
        Assert.Equal(800m, DashboardRevenuePolicy.Amount(800m, 1000m));
        Assert.Equal(1000m, DashboardRevenuePolicy.Amount(0m, 1000m));
        Assert.Contains("RETURNED", DashboardRevenuePolicy.ReturnedStatuses);
        Assert.DoesNotContain("RETURNED", DashboardRevenuePolicy.CancelledStatuses);
    }

    [Fact]
    public void Trendyol_stored_times_are_three_hours_ahead_and_others_are_real()
    {
        var stored = new DateTimeOffset(2026, 10, 20, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(3), MarketplaceStoredClock.StoredClockAhead("TRENDYOL"));
        Assert.Equal(TimeSpan.FromHours(3), MarketplaceStoredClock.StoredClockAhead("trendyol"));
        Assert.Equal(TimeSpan.Zero, MarketplaceStoredClock.StoredClockAhead("HEPSIBURADA"));
        Assert.Equal(TimeSpan.Zero, MarketplaceStoredClock.StoredClockAhead(null));
        // A Trendyol order stored as 23:30 "UTC" was placed at 23:30 Istanbul, i.e. 20:30 UTC: still the 20th.
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 20, 30, 0, TimeSpan.Zero), MarketplaceStoredClock.RealInstant(stored, "TRENDYOL"));
    }

    [Fact]
    public void Performance_queries_group_by_day_store_and_currency_in_the_database()
    {
        using var db = DashboardOrderVisibilityTests.MetadataOnlyContext();
        var period = DashboardPerformancePeriod.Resolve(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 20), Now, Istanbul).Value!;
        var orders = DashboardReadService.PerformanceOrders(db, Guid.NewGuid(), period, "Europe/Istanbul");

        var rowsSql = orders.ToQueryString();
        var revenueSql = DashboardReadService.RevenueQuery(orders).ToQueryString();
        var unitSql = DashboardReadService.UnitQuery(db, orders).ToQueryString();
        var packageSql = DashboardReadService.PackageQuery(db, orders).ToQueryString();

        Assert.Contains("AT TIME ZONE", rowsSql, StringComparison.Ordinal);
        Assert.Contains("TRENDYOL", rowsSql, StringComparison.Ordinal);
        Assert.Contains("NetAmount", rowsSql, StringComparison.Ordinal);
        Assert.Contains("RETURNED", rowsSql, StringComparison.Ordinal);
        foreach (var sql in new[] { revenueSql, unitSql, packageSql })
            Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains("CancelledQuantity", unitSql, StringComparison.Ordinal);
        Assert.Contains("shipment_packages", packageSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Returned_orders_stay_out_of_revenue_and_show_on_their_own()
    {
        var day = new DateTime(2026, 10, 20);
        var store = Guid.NewGuid();
        var rows = DashboardReadService.MergePerformanceRows(
            [new(day, store, "TRY", false, 900m, 3), new(day, store, "TRY", true, 150m, 1), new(day, store, "USD", false, 20m, 1)],
            [new(day, store, "TRY", 5m)],
            [new(day, store, "TRY", 3)]);

        var lira = Assert.Single(rows, x => x.Currency == "TRY");
        Assert.Equal(900m, lira.Amount);
        Assert.Equal(3, lira.OrderCount);
        Assert.Equal(150m, lira.ReturnedAmount);
        Assert.Equal(1, lira.ReturnedOrderCount);
        Assert.Equal(5m, lira.ProductQuantity);
        Assert.Equal(3, lira.PackageCount);
        // Other currencies are never added into the lira total.
        Assert.Equal(20m, Assert.Single(rows, x => x.Currency == "USD").Amount);
    }

    [Fact]
    public void Breakdowns_are_read_per_store_and_an_old_flat_map_is_ignored()
    {
        var store = Guid.NewGuid().ToString("D");
        var current = DashboardReadService.ReadBreakdown($"{{\"pending\":{{\"{store}\":4}},\"late\":{{\"{store}\":1}},\"returns\":{{}}}}");
        var old = DashboardReadService.ReadBreakdown("{\"Trendyol\":4}");

        Assert.Equal(4, current.Pending[store]);
        Assert.Equal(1, current.Late[store]);
        Assert.Empty(current.Returns);
        Assert.Empty(old.Pending);
        Assert.Empty(DashboardReadService.ReadBreakdown("not json").Late);
    }

    // Live 20 Oct 2026: the worker writes "Pending"/"Late"/"Returns"; the reader looked for "pending" only and every
    // hover breakdown was empty.
    [Fact]
    public void Breakdowns_survive_the_form_the_worker_writes()
    {
        var store = Guid.NewGuid().ToString("D");
        var written = DashboardReadService.WriteBreakdown(new(
            new Dictionary<string, int> { [store] = 8 }, new Dictionary<string, int> { [store] = 1 }, new Dictionary<string, int>()));

        var read = DashboardReadService.ReadBreakdown(written);

        Assert.Equal(8, read.Pending[store]);
        Assert.Equal(1, read.Late[store]);
        Assert.Empty(read.Returns);
    }

    [Fact]
    public void Integration_matrix_reads_the_newest_problem_per_store_and_job_type_without_policy_blocks()
    {
        using var db = DashboardOrderVisibilityTests.MetadataOnlyContext();
        var sql = DashboardIntegrationMatrix.ProblemQuery(db, Guid.NewGuid(), [Guid.NewGuid()], Now.AddDays(-7)).ToQueryString();

        Assert.DoesNotContain("LIMIT 300", sql, StringComparison.Ordinal);
        Assert.True(sql.Contains("ROW_NUMBER", StringComparison.Ordinal) || sql.Contains("DISTINCT ON", StringComparison.Ordinal), sql);
        Assert.Contains("EXTERNAL_WRITES_DISABLED", sql, StringComparison.Ordinal);
        Assert.Contains("_DISABLED", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(10, true, false)]
    [InlineData(31, true, true)]
    [InlineData(31, false, false)]
    [InlineData(301, false, true)]
    public void Snapshot_rebuild_is_debounced_per_tenant(int? secondsSinceUpdate, bool changed, bool due)
    {
        DateTimeOffset? updated = secondsSinceUpdate is { } seconds ? Now.AddSeconds(-seconds) : null;
        Assert.Equal(due, DashboardSnapshotRefreshPolicy.IsDue(updated, changed, Now));
    }
}
