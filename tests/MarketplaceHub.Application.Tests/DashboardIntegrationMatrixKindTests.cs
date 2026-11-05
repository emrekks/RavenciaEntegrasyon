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
