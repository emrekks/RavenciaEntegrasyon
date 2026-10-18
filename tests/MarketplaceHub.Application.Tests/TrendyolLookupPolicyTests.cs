using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolLookupPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Codes = ["TR", "AE", "SA"];

    [Fact]
    public void UnknownOrRecentOrderIsReadFromTheLastTwoWeeks()
    {
        Assert.Equal((Now.AddDays(-14), Now), TrendyolLookupPolicy.V2Window(null, Now));
        Assert.Equal((Now.AddDays(-14), Now), TrendyolLookupPolicy.V2Window(new(Now.AddDays(-5), Now.AddDays(-1), "TR"), Now));
    }

    [Fact]
    public void TwoToFourWeekOldOrderIsReadFromTheTwoWeeksAfterItsOrderDate()
    {
        var hint = new OrderLookupHint(Now.AddDays(-20), Now.AddDays(-2), "TR");

        Assert.Equal((Now.AddDays(-21), Now.AddDays(-7)), TrendyolLookupPolicy.V2Window(hint, Now));
        Assert.Equal((Now.AddDays(-30), Now.AddDays(-16)), TrendyolLookupPolicy.V2Window(hint with { OrderedAt = Now.AddDays(-29.5) }, Now));
    }

    [Fact]
    public void OrderOlderThanAMonthSkipsV2AndReadsTheStreamAroundItsLastChange()
    {
        var hint = new OrderLookupHint(Now.AddDays(-45), Now.AddDays(-40), "SA");

        Assert.Null(TrendyolLookupPolicy.V2Window(hint, Now));
        Assert.Equal((Now.AddDays(-46), Now.AddDays(-34)), TrendyolLookupPolicy.StreamWindow(hint, Now));
        Assert.Equal(["SA"], TrendyolLookupPolicy.Storefronts(hint, Codes));
    }

    [Fact]
    public void OrderBeyondTheStreamHistoryIsNotReadAndUnknownOrdersHaveNoStreamWindow()
    {
        Assert.Null(TrendyolLookupPolicy.StreamWindow(new(Now.AddDays(-120), Now.AddDays(-100), null), Now));
        Assert.Null(TrendyolLookupPolicy.StreamWindow(null, Now));
        Assert.Equal(Codes, TrendyolLookupPolicy.Storefronts(new(Now, Now, null), Codes));
    }
}
