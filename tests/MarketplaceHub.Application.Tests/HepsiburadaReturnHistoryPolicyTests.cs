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
}
