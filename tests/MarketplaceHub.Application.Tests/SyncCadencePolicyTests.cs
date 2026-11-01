using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class SyncCadencePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    // The cadences live on 1 Nov 2026: every one lies within its bounds, so no saved flow is refused when switched.
    [Theory]
    [InlineData("TRENDYOL", "ORDERS", 120)]
    [InlineData("TRENDYOL", "ORDER_LIFECYCLE", 1800)]
    [InlineData("TRENDYOL", "ORDER_CARGO_INFO", 1800)]
    [InlineData("TRENDYOL", "ORDER_INVOICE_RECONCILIATION", 900)]
    [InlineData("TRENDYOL", "ORDER_RECONCILE_SHORT", 3600)]
    [InlineData("TRENDYOL", "ORDER_RECONCILE_MEDIUM", 21_600)]
    [InlineData("TRENDYOL", "ORDER_RECONCILE_DAILY", 86_400)]
    [InlineData("TRENDYOL", "RETURN_LIFECYCLE", 1800)]
    [InlineData("TRENDYOL", "RETURNS", 600)]
    [InlineData("TRENDYOL", "QUESTIONS", 120)]
    [InlineData("TRENDYOL", "FINANCE", 86_400)]
    [InlineData("HEPSIBURADA", "ORDER_LIFECYCLE", 120)]
    [InlineData("HEPSIBURADA", "ORDER_INVOICE_RECONCILIATION", 1800)]
    [InlineData("N11", "QUESTIONS", 120)]
    [InlineData("N11", "RETURNS", 600)]
    [InlineData("PAZARAMA", "ORDER_LIFECYCLE", 120)]
    [InlineData("PTTAVM", "ORDER_LIFECYCLE", 900)]
    [InlineData("SHOPIFY", "ORDER_RECONCILE_DAILY", 259_200)]
    [InlineData("SHOPIFY", "ORDER_RECONCILE_SHORT", 2700)]
    [InlineData("SHOPIFY", "ORDER_RECOVERY", 3600)]
    public void Live_cadences_lie_within_their_bounds(string platform, string resourceType, int intervalSeconds)
    {
        var bounds = SyncCadencePolicy.For(platform, resourceType);

        Assert.NotNull(bounds);
        Assert.InRange(intervalSeconds, bounds.MinimumSeconds, bounds.MaximumSeconds);
        Assert.False(string.IsNullOrWhiteSpace(bounds.Basis));
    }

    [Theory]
    [InlineData("PRICE_WRITE")]
    [InlineData("STOCK_WRITE")]
    [InlineData("LISTING_VERIFY")]
    [InlineData("PRODUCTS")]
    public void Permissions_and_fixed_windows_have_no_chosen_cadence(string resourceType) =>
        Assert.Null(SyncCadencePolicy.For("TRENDYOL", resourceType));

    [Fact]
    public void Trendyol_order_quota_keeps_new_order_reads_at_one_minute_or_more()
    {
        Assert.Equal(60, SyncCadencePolicy.For("TRENDYOL", "ORDERS")!.MinimumSeconds);
        Assert.Equal(120, SyncCadencePolicy.For("N11", "QUESTIONS")!.MinimumSeconds);
    }

    [Fact]
    public void Healthy_notifications_thin_order_reads_to_the_chosen_idle_interval()
    {
        Assert.Equal(480, SyncCadencePolicy.EffectiveSeconds("TRENDYOL", "ORDERS", "PRODUCTION", 120, true, null, true, null, Now));
        Assert.Equal(900, SyncCadencePolicy.EffectiveSeconds("TRENDYOL", "ORDERS", "PRODUCTION", 120, true, 900, true, null, Now));
        Assert.Equal(120, SyncCadencePolicy.EffectiveSeconds("TRENDYOL", "ORDERS", "PRODUCTION", 120, true, 900, false, null, Now));
        Assert.Equal(120, SyncCadencePolicy.EffectiveSeconds("TRENDYOL", "ORDERS", "PRODUCTION", 120, false, 900, true, null, Now));
    }

    [Fact]
    public void Idle_interval_never_goes_below_the_base()
    {
        Assert.Equal(1800, SyncCadencePolicy.EffectiveSeconds("SHOPIFY", "ORDER_LIFECYCLE", "PRODUCTION", 1800, true, 600, true, null, Now));
        Assert.Equal(600, SyncCadencePolicy.EffectiveSeconds("SHOPIFY", "ORDERS", "PRODUCTION", 600, true, null, true, null, Now));
    }

    [Fact]
    public void Questions_stretch_up_to_the_chosen_ceiling_while_none_arrive()
    {
        Assert.Equal(600, SyncCadencePolicy.EffectiveSeconds("N11", "QUESTIONS", "PRODUCTION", 120, true, null, false, null, Now));
        Assert.Equal(1800, SyncCadencePolicy.EffectiveSeconds("N11", "QUESTIONS", "PRODUCTION", 120, true, 1800, false, null, Now));
        Assert.Equal(120, SyncCadencePolicy.EffectiveSeconds("N11", "QUESTIONS", "PRODUCTION", 120, true, 1800, false, Now.AddMinutes(-5), Now));
        Assert.Equal(120, SyncCadencePolicy.EffectiveSeconds("N11", "QUESTIONS", "PRODUCTION", 120, false, null, false, null, Now));
    }

    [Fact]
    public void Only_flows_with_an_idle_rule_thin_out()
    {
        Assert.Equal(SyncCadencePolicy.IdleWebhook, SyncCadencePolicy.IdleKind("HEPSIBURADA", "ORDERS"));
        Assert.Null(SyncCadencePolicy.IdleKind("HEPSIBURADA", "ORDER_LIFECYCLE"));
        Assert.Null(SyncCadencePolicy.IdleKind("N11", "ORDERS"));
        Assert.Equal(SyncCadencePolicy.IdleQuiet, SyncCadencePolicy.IdleKind("PAZARAMA", "QUESTIONS"));
        Assert.Equal(180, SyncCadencePolicy.EffectiveSeconds("N11", "ORDERS", "PRODUCTION", 180, true, null, true, null, Now));
    }

    [Fact]
    public void Test_connections_keep_their_ten_minute_floor() =>
        Assert.Equal(600, SyncCadencePolicy.EffectiveSeconds("TRENDYOL", "ORDERS", "STAGE", 120, true, null, false, null, Now));

    // Returns: the panel's lower bound is the worker's ten-minute floor, and the idle rule is the return cadence.
    [Fact]
    public void Return_reads_never_run_below_the_ten_minute_floor()
    {
        Assert.Equal(ReturnCadencePolicy.BaseSeconds, SyncCadencePolicy.For("TRENDYOL", "RETURNS")!.MinimumSeconds);
        Assert.Equal(600, SyncCadencePolicy.EffectiveSeconds("TRENDYOL", "RETURNS", "PRODUCTION", 180, false, null, false, null, Now, hasOpenReturns: false));
        Assert.Equal(600, SyncCadencePolicy.EffectiveSeconds("TRENDYOL", "RETURNS", "PRODUCTION", 180, true, null, false, null, Now, hasOpenReturns: true));
    }

    [Fact]
    public void Return_reads_thin_out_as_the_worker_does_by_default()
    {
        Assert.Equal(SyncCadencePolicy.IdleReturns, SyncCadencePolicy.IdleKind("N11", "RETURNS"));
        Assert.Equal(1200, SyncCadencePolicy.EffectiveSeconds("N11", "RETURNS", "PRODUCTION", 600, true, null, false, null, Now, false, Now.AddDays(-2)));
        Assert.Equal(1800, SyncCadencePolicy.EffectiveSeconds("N11", "RETURNS", "PRODUCTION", 600, true, null, false, null, Now, false, Now.AddDays(-8)));
        Assert.Equal(1800, SyncCadencePolicy.EffectiveSeconds("HEPSIBURADA", "RETURNS", "PRODUCTION", 600, true, null, true, null, Now, true, Now));
        Assert.Equal(3600, SyncCadencePolicy.EffectiveSeconds("HEPSIBURADA", "RETURNS", "PRODUCTION", 600, true, null, true, null, Now, false, Now));
    }

    [Fact]
    public void A_chosen_idle_interval_becomes_the_longest_return_step()
    {
        Assert.Equal(3600, SyncCadencePolicy.EffectiveSeconds("N11", "RETURNS", "PRODUCTION", 600, true, 3600, false, null, Now, false, Now.AddDays(-8)));
        Assert.Equal(900, SyncCadencePolicy.EffectiveSeconds("N11", "RETURNS", "PRODUCTION", 600, true, 900, false, null, Now, false, Now.AddDays(-2)));
        Assert.Equal(900, SyncCadencePolicy.EffectiveSeconds("HEPSIBURADA", "RETURNS", "PRODUCTION", 600, true, 900, true, null, Now, true, Now));
        Assert.Equal(600, SyncCadencePolicy.EffectiveSeconds("N11", "RETURNS", "PRODUCTION", 600, false, 3600, false, null, Now, false, Now.AddDays(-8)));
    }
}
