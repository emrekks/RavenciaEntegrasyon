using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaReturnHistoryPolicyTests
{
    [Fact]
    public void Initial_return_scan_only_reads_the_recent_thirty_days()
    {
        var anchor = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(anchor.AddDays(-30), HepsiburadaReturnHistoryPolicy.InitialStatusChangeStart(anchor));
    }

    [Fact]
    public void Return_history_uses_the_order_date_and_skips_old_or_unknown_orders()
    {
        var anchor = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.True(HepsiburadaReturnHistoryPolicy.IsOrderWithinReturnHistory(anchor.AddDays(-30), anchor));
        Assert.True(HepsiburadaReturnHistoryPolicy.IsOrderWithinReturnHistory(anchor.AddDays(-1), anchor));
        Assert.False(HepsiburadaReturnHistoryPolicy.IsOrderWithinReturnHistory(anchor.AddDays(-31), anchor));
        Assert.False(HepsiburadaReturnHistoryPolicy.IsOrderWithinReturnHistory(null, anchor));
        Assert.False(HepsiburadaReturnHistoryPolicy.IsOrderWithinReturnHistory(anchor.AddSeconds(1), anchor));
    }

    [Fact]
    public void An_incremental_pass_looks_back_at_least_two_days_even_right_after_the_last_one()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 43, 0, TimeSpan.Zero);
        var overlap = TimeSpan.FromMinutes(15);

        // Three minutes after the last pass: the old window was 18 minutes, it is now two days.
        Assert.Equal(now.AddDays(-2), HepsiburadaReturnHistoryPolicy.IncrementalStatusChangeStart(now.AddMinutes(-3), overlap, now));
        // After a long pause the window still reaches back to the last pass.
        Assert.Equal(now.AddDays(-5) - overlap, HepsiburadaReturnHistoryPolicy.IncrementalStatusChangeStart(now.AddDays(-5), overlap, now));
    }

    [Fact]
    public void The_first_pass_of_each_hour_is_a_full_one()
    {
        var now = new DateTimeOffset(2026, 10, 23, 13, 1, 0, TimeSpan.Zero);

        Assert.True(HepsiburadaReturnHistoryPolicy.FullPassDue(null, now));
        Assert.True(HepsiburadaReturnHistoryPolicy.FullPassDue(new DateTimeOffset(2026, 10, 23, 12, 58, 0, TimeSpan.Zero), now));
        Assert.False(HepsiburadaReturnHistoryPolicy.FullPassDue(new DateTimeOffset(2026, 10, 23, 13, 0, 30, TimeSpan.Zero), now));
        // The same instant in Istanbul time is the same hour.
        Assert.False(HepsiburadaReturnHistoryPolicy.FullPassDue(new DateTimeOffset(2026, 10, 23, 16, 0, 30, TimeSpan.FromHours(3)), now));
    }
}
