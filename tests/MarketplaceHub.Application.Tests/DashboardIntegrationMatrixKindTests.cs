using MarketplaceHub.Application;
using MarketplaceHub.Application.Tests;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class DashboardIntegrationMatrixKindTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");

    [Theory]
    [InlineData("products")]
    [InlineData("stock")]
    [InlineData("price")]
    public void Kinds_that_run_on_demand_have_no_age_rule(string key) =>
        Assert.Null(DashboardIntegrationMatrix.Kinds.Single(kind => kind.Key == key).StaleAfter);

    [Theory]
    [InlineData("orders")]
    [InlineData("returns")]
    [InlineData("invoices")]
    public void Scheduled_reads_still_turn_late(string key) =>
        Assert.NotNull(DashboardIntegrationMatrix.Kinds.Single(kind => kind.Key == key).StaleAfter);

    // T2-D5: the order flows the overview left out.
    [Theory]
    [InlineData(MarketplaceJobTypes.HepsiburadaWebhookIngest)]
    [InlineData(MarketplaceJobTypes.OrderReconciliation)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderReconciliation)]
    [InlineData(MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation)]
    public void Order_cell_covers_every_order_flow(string jobType) =>
        Assert.Contains(jobType, DashboardIntegrationMatrix.Kinds.Single(kind => kind.Key == "orders").JobTypes);

    // T2-Y2: each job type is judged by its own latest result, and the worst wins.
    [Fact]
    public void A_failure_after_the_types_own_success_is_bad_and_the_worst_type_wins()
    {
        var hourAgo = Now.AddHours(-1);
        Assert.Equal("bad", DashboardIntegrationMatrix.TypeState(hourAgo, JobStatus.Dead, TimeSpan.FromHours(2), Now));
        Assert.Equal("warn", DashboardIntegrationMatrix.TypeState(hourAgo, JobStatus.RetryScheduled, TimeSpan.FromHours(2), Now));
        Assert.Equal("ok", DashboardIntegrationMatrix.TypeState(hourAgo, null, TimeSpan.FromHours(2), Now));
        Assert.Equal("off", DashboardIntegrationMatrix.TypeState(null, null, TimeSpan.FromHours(2), Now));
        Assert.Equal("bad", DashboardIntegrationMatrix.TypeState(null, JobStatus.ManualReview, null, Now));
        Assert.Equal("bad", DashboardIntegrationMatrix.Worst(DashboardIntegrationMatrix.Worst("ok", "bad"), "warn"));
        Assert.Equal("ok", DashboardIntegrationMatrix.Worst("off", "ok"));
    }

    // T2-O13: a flow silent beyond its period is late, beyond twice its period bad (not grey after a week).
    [Theory]
    [InlineData(1, "ok")]
    [InlineData(3, "warn")]
    [InlineData(5, "bad")]
    [InlineData(24 * 9, "bad")]
    public void A_silent_scheduled_flow_turns_late_then_bad(int hoursSilent, string expected) =>
        Assert.Equal(expected, DashboardIntegrationMatrix.TypeState(Now.AddHours(-hoursSilent), null, TimeSpan.FromHours(2), Now));

    [Fact]
    public void Event_driven_flows_and_switched_off_flows_have_no_age_rule()
    {
        var periods = new Dictionary<string, TimeSpan?> { [MarketplaceJobTypes.OrderRecoverySync] = TimeSpan.FromHours(6), [MarketplaceJobTypes.OrderStatusSync] = null, [MarketplaceJobTypes.OrderSync] = TimeSpan.FromMinutes(2) };
        Assert.Null(DashboardIntegrationMatrix.Period(MarketplaceJobTypes.WebhookIngest, TimeSpan.FromHours(2), periods));
        Assert.Null(DashboardIntegrationMatrix.Period(MarketplaceJobTypes.ReturnStatusSync, TimeSpan.FromHours(12), periods));
        Assert.Null(DashboardIntegrationMatrix.Period(MarketplaceJobTypes.OrderStatusSync, TimeSpan.FromHours(2), periods));
        Assert.Null(DashboardIntegrationMatrix.Period(MarketplaceJobTypes.ProductSync, null, periods));
        // A longer policy interval is the period; a shorter one never goes below the kind's.
        Assert.Equal(TimeSpan.FromHours(6), DashboardIntegrationMatrix.Period(MarketplaceJobTypes.OrderRecoverySync, TimeSpan.FromHours(2), periods));
        Assert.Equal(TimeSpan.FromHours(2), DashboardIntegrationMatrix.Period(MarketplaceJobTypes.OrderSync, TimeSpan.FromHours(2), periods));
        Assert.Equal(TimeSpan.FromHours(2), DashboardIntegrationMatrix.Period(MarketplaceJobTypes.ShopifyOrderSync, TimeSpan.FromHours(2), periods));
    }

    [Fact]
    public void Policies_give_each_job_type_its_shortest_enabled_interval()
    {
        var connection = Guid.NewGuid();
        var periods = DashboardIntegrationMatrix.Periods("N11",
        [
            new(connection, "ORDER_RECONCILE_SHORT", 3600, true, false, null),
            new(connection, "ORDER_RECONCILE_DAILY", 86_400, true, false, null),
            new(connection, "ORDER_RECOVERY", 900, false, false, null),
            new(connection, "QUESTIONS", 120, true, true, null)
        ]);
        Assert.Equal(TimeSpan.FromHours(1), periods[MarketplaceJobTypes.OrderReconciliation]);
        Assert.Null(periods[MarketplaceJobTypes.N11OrderRecoverySync]);
        Assert.False(periods.ContainsKey(MarketplaceJobTypes.QuestionSync));
        Assert.Equal(MarketplaceJobTypes.ShopifyOrderReconciliation, DashboardIntegrationMatrix.ScheduledJobType("SHOPIFY", "ORDER_RECONCILE_MEDIUM"));
        Assert.Equal(MarketplaceJobTypes.HepsiburadaReturnSync, DashboardIntegrationMatrix.ScheduledJobType("HEPSIBURADA", "RETURNS"));
    }

    // Live 10 Nov 2026: Pazarama's and PTT AVM's open-package status scans are queued only while the connection has an
    // open package (ScheduledJobProducer.LifecycleFollowsOpenPackagesOnly). With none open they stopped at 11:48 and the
    // order cell read "Hata Var" four hours later although every order read succeeded.
    [Theory]
    [InlineData(MarketplaceJobTypes.PazaramaOrderStatusSync)]
    [InlineData(MarketplaceJobTypes.PttAvmOrderStatusSync)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderStatusSync)]
    public void A_status_scan_with_no_open_package_to_follow_has_no_age_rule(string jobType)
    {
        var success = Now.AddHours(-6);
        var (from, period) = DashboardIntegrationMatrix.OpenPackageGate(jobType, success, TimeSpan.FromHours(2), openPackagesSince: null);
        Assert.Null(period);
        Assert.Equal("ok", DashboardIntegrationMatrix.TypeState(from, null, period, Now));
    }

    [Fact]
    public void A_status_scan_is_measured_from_when_a_package_opened_after_its_last_run()
    {
        var success = Now.AddHours(-6);
        // A package opened a minute ago: the scan is due on the scheduler's next pass, not six hours late.
        var (from, period) = DashboardIntegrationMatrix.OpenPackageGate(MarketplaceJobTypes.PazaramaOrderStatusSync, success, TimeSpan.FromHours(2), Now.AddMinutes(-1));
        Assert.Equal("ok", DashboardIntegrationMatrix.TypeState(from, null, period, Now));
        // A package open since before the last run while the scan stays silent is a real stall.
        (from, period) = DashboardIntegrationMatrix.OpenPackageGate(MarketplaceJobTypes.PttAvmOrderStatusSync, success, TimeSpan.FromHours(2), Now.AddDays(-2));
        Assert.Equal("bad", DashboardIntegrationMatrix.TypeState(from, null, period, Now));
        // Never run in the window stays unknown.
        (from, period) = DashboardIntegrationMatrix.OpenPackageGate(MarketplaceJobTypes.PttAvmOrderStatusSync, null, TimeSpan.FromHours(2), Now.AddMinutes(-1));
        Assert.Equal("off", DashboardIntegrationMatrix.TypeState(from, null, period, Now));
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderStatusSync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderStatusSync)]
    [InlineData(MarketplaceJobTypes.PazaramaOrderSync)]
    public void Flows_the_scheduler_always_queues_keep_their_age_rule(string jobType)
    {
        var success = Now.AddHours(-6);
        var (from, period) = DashboardIntegrationMatrix.OpenPackageGate(jobType, success, TimeSpan.FromHours(2), openPackagesSince: null);
        Assert.Equal(TimeSpan.FromHours(2), period);
        Assert.Equal("bad", DashboardIntegrationMatrix.TypeState(from, null, period, Now));
    }

    // A failure of a gated scan still counts.
    [Fact]
    public void A_failed_status_scan_is_bad_even_with_no_open_package()
    {
        var (from, period) = DashboardIntegrationMatrix.OpenPackageGate(MarketplaceJobTypes.PazaramaOrderStatusSync, Now.AddHours(-6), TimeSpan.FromHours(2), null);
        Assert.Equal("bad", DashboardIntegrationMatrix.TypeState(from, JobStatus.Dead, period, Now));
    }

    // T2-D6: a scheduled read folded into the run already going did nothing itself and is no success.
    [Fact]
    public void A_coalesced_read_is_not_counted_as_a_success()
    {
        Assert.Equal(MarketplaceSyncExecutionLock.CoalescedCode, MarketplaceSyncExecutionLock.ContentionResult(MarketplaceJobTypes.OrderSync, "{}", null, sameTypeRunning: true).ErrorCode);
        using var db = DashboardOrderVisibilityTests.MetadataOnlyContext();
        var sql = DashboardIntegrationMatrix.SuccessQuery(db, Guid.NewGuid(), [Guid.NewGuid()], Now.AddDays(-14)).ToQueryString();
        Assert.Contains(MarketplaceSyncExecutionLock.CoalescedCode, sql, StringComparison.Ordinal);
    }
}
