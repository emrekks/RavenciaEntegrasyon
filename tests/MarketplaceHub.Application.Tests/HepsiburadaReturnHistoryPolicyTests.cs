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
}
