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
    public void Yesterday_is_a_whole_Istanbul_day_compared_with_the_whole_day_before()
    {
        // The panel's "Dün" button asks for from = to = yesterday; just after midnight it is still a whole day.
        foreach (var now in new[] { Now, new DateTimeOffset(2026, 10, 19, 21, 5, 0, TimeSpan.Zero) })
        {
            var period = DashboardPerformancePeriod.Resolve(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 19), now, Istanbul).Value!;

            Assert.False(period.PartialDay);
            Assert.Equal(new DateTimeOffset(2026, 10, 18, 21, 0, 0, TimeSpan.Zero), period.CurrentStart);
            Assert.Equal(new DateTimeOffset(2026, 10, 19, 21, 0, 0, TimeSpan.Zero), period.CurrentEnd);
            Assert.Equal(new DateOnly(2026, 10, 18), period.PreviousFrom);
            Assert.Equal(new DateOnly(2026, 10, 18), period.PreviousTo);
            Assert.Equal(new DateTimeOffset(2026, 10, 17, 21, 0, 0, TimeSpan.Zero), period.PreviousStart);
            Assert.Equal(new DateTimeOffset(2026, 10, 18, 21, 0, 0, TimeSpan.Zero), period.PreviousEnd);
            Assert.Equal(TimeSpan.Zero, period.CurrentStart.Offset);
        }
    }

    [Fact]
    public void Last_seven_days_including_today_compare_with_the_seven_whole_days_before()
    {
        var period = DashboardPerformancePeriod.Resolve(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 20), Now, Istanbul).Value!;

        Assert.Equal(new DateOnly(2026, 10, 7), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 10, 13), period.PreviousTo);
        Assert.Equal(Now, period.CurrentEnd);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 21, 0, 0, TimeSpan.Zero), period.PreviousStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 21, 0, 0, TimeSpan.Zero), period.PreviousEnd);
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

    // Owner, 29 Oct 2026: a Trendyol micro-export return keeps its revenue until Trendyol's statement records a refund;
    // the overview asks the statement through the same query as the reports (PostgreSqlReportsTests compares the numbers).
    [Fact]
    public void A_micro_export_return_counts_on_the_overview_only_once_trendyols_statement_records_it()
    {
        using var db = DashboardOrderVisibilityTests.MetadataOnlyContext();
        var tenantId = Guid.NewGuid();
        var period = DashboardPerformancePeriod.Resolve(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 20), Now, Istanbul).Value!;
        var orders = DashboardReadService.PerformanceOrders(db, tenantId, period, "Europe/Istanbul");
        var rule = DashboardRevenuePolicy.OrdersWithUnbookedReturn(db, tenantId).ToQueryString();

        Assert.Contains("StoreFrontCode", rule, StringComparison.Ordinal);
        Assert.Contains("marketplace_finance_transactions", rule, StringComparison.Ordinal);
        foreach (var sql in new[] { orders.ToQueryString(), DashboardReadService.UnitQuery(db, orders).ToQueryString(), DashboardReadService.PackageQuery(db, orders).ToQueryString() })
            Assert.Contains("marketplace_finance_transactions", sql, StringComparison.Ordinal);
    }

    // O5 (owner, 2 Nov 2026): the panel's Ciro has the returns in it (c00eff97), so its order count has the returned
    // orders in it too; Ortalama Sepet was the returned orders' money divided by the orders without them.
    [Fact]
    public void Returned_orders_are_counted_with_the_money_they_bring_into_ciro()
    {
        var day = new DateTime(2026, 10, 20);
        var store = Guid.NewGuid();
        DashboardReadService.PerformanceOrderRow Row(decimal amount, decimal returnedAmount = 0m, bool returned = false) =>
            new() { Id = Guid.NewGuid(), ConnectionId = store, Currency = "TRY", Amount = amount, ReturnedAmount = returnedAmount, Returned = returned, Day = day };
        var orders = new[] { Row(300m), Row(200m, returnedAmount: 50m), Row(500m, returned: true), Row(0m) }.AsQueryable();

        var group = Assert.Single(DashboardReadService.RevenueQuery(orders).ToList());

        Assert.Equal(450m, group.Amount);
        Assert.Equal(550m, group.ReturnedAmount);
        Assert.Equal(2, group.ReturnedOrders);
        // Three orders with an amount, the wholly returned one included; the one without an amount is not counted.
        Assert.Equal(3, group.Orders);
        Assert.Equal(1000m / 3, (group.Amount + group.ReturnedAmount) / group.Orders);
    }

    [Fact]
    public void Returned_orders_stay_out_of_revenue_and_show_on_their_own()
    {
        var day = new DateTime(2026, 10, 20);
        var store = Guid.NewGuid();
        var rows = DashboardReadService.MergePerformanceRows(
            [new(day, store, "TRY", 900m, 3, 0m, 0), new(day, store, "TRY", 0m, 0, 150m, 1), new(day, store, "USD", 20m, 1, 0m, 0)],
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
        // What the rebuild writes (the record's own names) reads back too.
        var written = DashboardReadService.ReadBreakdown($"{{\"Pending\":{{\"{store}\":2}},\"Late\":{{}},\"Returns\":{{\"{store}\":1}}}}");
        Assert.Equal(2, written.Pending[store]);
        Assert.Equal(1, written.Returns[store]);
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
        // A snapshot from before the pending products were kept has none: they are read live, not taken as 0.
        Assert.Null(read.PendingUnits);
    }

    [Fact]
    public void The_pending_products_are_kept_with_the_breakdowns_and_a_change_in_them_announces_the_snapshot()
    {
        var store = Guid.NewGuid().ToString("D");
        var counts = new Dictionary<string, int> { [store] = 28 };
        var written = DashboardReadService.WriteBreakdown(new(counts, counts, counts, new Dictionary<string, decimal> { [store] = 35 }));

        var read = DashboardReadService.ReadBreakdown(written);

        Assert.Equal(35, read.PendingUnits![store]);
        Assert.True(DashboardReadService.SameBreakdown(read, new(counts, counts, counts, new Dictionary<string, decimal> { [store] = 35 })));
        Assert.False(DashboardReadService.SameBreakdown(read, new(counts, counts, counts, new Dictionary<string, decimal> { [store] = 34 })));
        Assert.False(DashboardReadService.SameBreakdown(read, new(counts, counts, counts)));
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

    // The outbox is read only when the answer can change IsDue, and the result is the same as when it is always read.
    [Theory]
    [InlineData(null, false)]
    [InlineData(-5, false)]
    [InlineData(10, false)]
    [InlineData(30, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(3600, false)]
    public void Snapshot_change_check_runs_only_inside_the_debounce_window(int? secondsSinceUpdate, bool needsCheck)
    {
        DateTimeOffset? updated = secondsSinceUpdate is { } seconds ? Now.AddSeconds(-seconds) : null;
        Assert.Equal(needsCheck, DashboardSnapshotRefreshPolicy.NeedsChangeCheck(updated, Now));
        if (!needsCheck)
            Assert.Equal(DashboardSnapshotRefreshPolicy.IsDue(updated, true, Now), DashboardSnapshotRefreshPolicy.IsDue(updated, false, Now));
    }

    // The snapshot time is a constant in the SQL, not a correlated column or a parameter: both of those were
    // planned as a sequential scan of the whole outbox.
    [Fact]
    public void Snapshot_change_check_writes_the_snapshot_time_into_the_query()
    {
        using var db = DashboardOrderVisibilityTests.MetadataOnlyContext();
        var sql = DashboardSnapshotRefreshPolicy.ChangesSince(db, Guid.NewGuid(), Now.AddMinutes(-1)).ToQueryString();

        Assert.DoesNotContain("dashboard.snapshot", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"""CreatedAt"" > TIMESTAMPTZ '2026-10-20[ T]07:29:00", sql);
        Assert.Matches(@"""TenantId"" = @\w+", sql);
        Assert.Contains("'IntegrationJob'", sql, StringComparison.Ordinal);
        Assert.Contains("'DashboardSnapshot'", sql, StringComparison.Ordinal);
    }
}
