using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolOrderHistoryPolicyTests
{
    [Fact]
    public void StreamBaselineUsesTheMaximumThreeCalendarMonths()
    {
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");

        Assert.Equal(DateTimeOffset.Parse("2026-07-02T00:00:00Z"), TrendyolOrderHistoryPolicy.StreamInitialStart(anchor));
    }

    [Fact]
    public void LegacyStatusEndpointNeverRequestsOlderThanThirtyDays()
    {
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");

        Assert.Equal(DateTimeOffset.Parse("2026-09-02T00:00:00Z"), TrendyolOrderHistoryPolicy.LegacyEndpointInitialStart(anchor));
    }
}
