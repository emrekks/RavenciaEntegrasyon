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

    // Trendyol order times are stored three hours ahead of the real instant (MarketplaceStoredClock); the age of an
    // order is judged on its real instant, so an order just past a boundary in real time is past it here too.
    [Fact]
    public void V2WindowJudgesTheTwoWeekBoundaryOnTheRealOrderTime()
    {
        // Real time: 14 days and one hour ago (stored two hours inside the two weeks).
        var justOlder = new OrderLookupHint(Now.AddDays(-14).AddHours(2), null, "TR");
        Assert.Equal((Now.AddDays(-15).AddHours(2), Now.AddDays(-1).AddHours(2)), TrendyolLookupPolicy.V2Window(justOlder, Now));

        // Real time: one hour inside the two weeks.
        var justInside = new OrderLookupHint(Now.AddDays(-14).AddHours(4), null, "TR");
        Assert.Equal((Now.AddDays(-14), Now), TrendyolLookupPolicy.V2Window(justInside, Now));
    }

    [Fact]
    public void V2WindowJudgesTheOneMonthBoundaryOnTheRealOrderTime()
    {
        // Real time: 30 days and one hour ago, beyond what V2 keeps, though the stored value is inside the month.
        Assert.Null(TrendyolLookupPolicy.V2Window(new(Now.AddDays(-30).AddHours(2), null, "TR"), Now));
        // Real time: one hour inside the month.
        Assert.Equal((Now.AddDays(-30), Now.AddDays(-16)), TrendyolLookupPolicy.V2Window(new(Now.AddDays(-30).AddHours(4), null, "TR"), Now));
    }

    [Fact]
    public void PackageBatchesJudgeTheTwoWeekBoundaryOnTheRealOrderTime()
    {
        // The first package is 14 days and one hour old in real time, so the batch needs one 13-day window and the
        // package of today does not fit in it.
        var packages = new[] { new TrendyolPackageRef("tr-edge", Now.AddDays(-14).AddHours(2), "TR"), new TrendyolPackageRef("tr-today", Now, "TR") };
        var batches = TrendyolLookupPolicy.PackageBatches(packages, Now);
        Assert.Equal(2, batches.Count);

        // One hour inside the two weeks in real time: any dates of the last two weeks share the batch.
        var recent = new[] { new TrendyolPackageRef("tr-edge", Now.AddDays(-14).AddHours(4), "TR"), new TrendyolPackageRef("tr-today", Now, "TR") };
        Assert.Single(TrendyolLookupPolicy.PackageBatches(recent, Now));
    }

    [Fact]
    public void StreamWindowWithoutARecordedChangeAnchorsOnTheRealOrderTime()
    {
        var hint = new OrderLookupHint(Now.AddDays(-40), null, "TR");
        var real = Now.AddDays(-40).AddHours(-3);
        Assert.Equal((real.AddDays(-6), real.AddDays(6)), TrendyolLookupPolicy.StreamWindow(hint, Now));
    }

    [Fact]
    public void StoredClockShiftsOnlyTheWallClockPlatforms()
    {
        Assert.Equal(Now.AddHours(3), MarketplaceStoredClock.ToStored(Now, "TRENDYOL"));
        Assert.Equal(Now.AddHours(3), MarketplaceStoredClock.ToStored(Now, " trendyol "));
        Assert.Equal(Now.AddHours(-3), MarketplaceStoredClock.ToReal(Now, "TRENDYOL"));
        Assert.Equal(Now, MarketplaceStoredClock.ToStored(Now, "HEPSIBURADA"));
        Assert.Equal(Now, MarketplaceStoredClock.ToReal(Now, null));
    }

    [Fact]
    public void OrderBeyondTheStreamHistoryIsNotReadAndUnknownOrdersHaveNoStreamWindow()
    {
        Assert.Null(TrendyolLookupPolicy.StreamWindow(new(Now.AddDays(-120), Now.AddDays(-100), null), Now));
        Assert.Null(TrendyolLookupPolicy.StreamWindow(null, Now));
        Assert.Equal(Codes, TrendyolLookupPolicy.Storefronts(new(Now, Now, null), Codes));
    }
}
