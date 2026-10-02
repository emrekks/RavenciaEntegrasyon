using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaOrderHistoryPolicyTests
{
    [Fact]
    public void InitialOrderHistoryWindowUsesThreeCalendarMonths()
    {
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");

        Assert.Equal(DateTimeOffset.Parse("2026-07-02T00:00:00Z"), HepsiburadaOrderHistoryPolicy.InitialWindowStart(anchor));
    }

    [Fact]
    public void IncrementalOrderWindowStaysInsideTheThreeMonthBaseline()
    {
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var oldest = HepsiburadaOrderHistoryPolicy.InitialWindowStart(anchor);

        Assert.Equal(oldest, HepsiburadaOrderHistoryPolicy.ClampWindowStart(anchor, oldest.AddDays(-15)));
        Assert.Equal(anchor, HepsiburadaOrderHistoryPolicy.ClampWindowStart(anchor, anchor.AddDays(1)));
        Assert.Equal(anchor.AddDays(-2), HepsiburadaOrderHistoryPolicy.ClampWindowStart(anchor, anchor.AddDays(-2)));
    }
}
