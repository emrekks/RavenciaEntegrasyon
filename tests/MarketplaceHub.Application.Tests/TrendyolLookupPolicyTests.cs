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
    public void PackageBatchesShareAStorefrontAndOneRequestWindowAndHoldAtMostFiftyPackages()
    {
        var recentTurkey = Enumerable.Range(1, 55).Select(index => new TrendyolPackageRef($"tr-{index}", Now.AddDays(-index % 10), "TR"));
        var olderTurkey = new[] { new TrendyolPackageRef("tr-old-1", Now.AddDays(-28), "TR"), new TrendyolPackageRef("tr-old-2", Now.AddDays(-16), "TR") };
        var gulf = new[] { new TrendyolPackageRef("ae-1", Now.AddDays(-2), "AE") };

        var batches = TrendyolLookupPolicy.PackageBatches(recentTurkey.Concat(olderTurkey).Concat(gulf), Now);

        Assert.All(batches, batch => Assert.True(batch.PackageIds.Count <= 50));
        Assert.Equal(58, batches.Sum(batch => batch.PackageIds.Count));
        var aeBatch = Assert.Single(batches, batch => batch.Hint.StoreFrontCode == "AE");
        Assert.Equal(["ae-1"], aeBatch.PackageIds);
        // 28 and 16 days old are twelve days apart, so one window from the older date covers both.
        var olderBatch = Assert.Single(batches, batch => batch.PackageIds.Contains("tr-old-1"));
        Assert.Contains("tr-old-2", olderBatch.PackageIds);
        Assert.Equal(Now.AddDays(-28), olderBatch.Hint.OrderedAt);
    }

    [Fact]
    public void OrderBeyondTheStreamHistoryIsNotReadAndUnknownOrdersHaveNoStreamWindow()
    {
        Assert.Null(TrendyolLookupPolicy.StreamWindow(new(Now.AddDays(-120), Now.AddDays(-100), null), Now));
        Assert.Null(TrendyolLookupPolicy.StreamWindow(null, Now));
        Assert.Equal(Codes, TrendyolLookupPolicy.Storefronts(new(Now, Now, null), Codes));
    }
}
