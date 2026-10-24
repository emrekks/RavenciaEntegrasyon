using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolStorefrontPolicyTests
{
    private static readonly string[] Codes = ["TR", "AE", "SA", "DE", "QA"];
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActiveStorefrontsAreTurkeyAndThoseWithAnOrderInTheLast90Days()
    {
        var activity = new Dictionary<string, TrendyolStorefrontActivity>
        {
            ["AE"] = new(Now.AddDays(-10), Now.AddMinutes(-3)),
            ["SA"] = new(Now.AddDays(-91), Now.AddMinutes(-3)),
            ["DE"] = new(null, Now.AddHours(-1))
        };

        Assert.Equal(["TR", "AE"], TrendyolStorefrontPolicy.Active(Codes, activity, Now));
    }

    [Fact]
    public void ANewOrderRunReadsTurkeyAndTheStorefrontsThatSoldInTheLastDay()
    {
        // Audit 2026-10-24 Q2: reading all seven live storefronts on every run held the orders lock ~30 s.
        var activity = new Dictionary<string, TrendyolStorefrontActivity>
        {
            ["AE"] = new(Now.AddHours(-23), Now.AddMinutes(-3)),
            ["SA"] = new(Now.AddDays(-2), Now.AddMinutes(-3)),
            ["DE"] = new(null, Now.AddHours(-1))
        };

        Assert.Equal(["TR", "AE"], TrendyolStorefrontPolicy.Hot(Codes, activity, Now));
        activity["AE"] = new(Now.AddHours(-25), Now.AddMinutes(-3));
        Assert.Equal(["TR"], TrendyolStorefrontPolicy.Hot(Codes, activity, Now));
    }

    [Fact]
    public void AQuietActiveStorefrontIsReadOnePerRunWhenItsLastReadIsHalfAnHourOld()
    {
        var activity = new Dictionary<string, TrendyolStorefrontActivity>
        {
            ["AE"] = new(Now.AddDays(-5), Now.AddMinutes(-31)),
            ["SA"] = new(Now.AddDays(-5), Now.AddMinutes(-50)),
            ["DE"] = new(Now.AddHours(-2), Now.AddHours(-2)),
            ["QA"] = new(null, null)
        };

        // SA is the longest overdue and AE waits for the next run. DE sold two hours ago: it is hot and read by
        // every run, never here. QA is inactive and left to the probe.
        Assert.Equal(["SA"], TrendyolStorefrontPolicy.DueQuiet(Codes, activity, Now));
        activity["SA"] = TrendyolStorefrontPolicy.Read(activity["SA"], Now, receivedOrders: false);
        Assert.Equal(["AE"], TrendyolStorefrontPolicy.DueQuiet(Codes, activity, Now));
        activity["AE"] = TrendyolStorefrontPolicy.Read(activity["AE"], Now, receivedOrders: true);
        // AE sold: it is hot and read on every run; SA was just read.
        Assert.Empty(TrendyolStorefrontPolicy.DueQuiet(Codes, activity, Now));
        Assert.Equal(["TR", "AE", "DE"], TrendyolStorefrontPolicy.Hot(Codes, activity, Now));
        // Half an hour later SA is due again.
        Assert.Equal(["SA"], TrendyolStorefrontPolicy.DueQuiet(Codes, activity, Now + TrendyolStorefrontPolicy.QuietReadInterval));
    }

    [Fact]
    public void NextProbeIsTheLongestOverdueInactiveStorefront()
    {
        var activity = new Dictionary<string, TrendyolStorefrontActivity>
        {
            ["AE"] = new(Now.AddDays(-1), Now),
            ["SA"] = new(null, Now.AddHours(-7)),
            ["DE"] = new(null, Now.AddHours(-2))
        };

        // QA was never probed, so it comes before SA; DE was probed two hours ago and is not due.
        Assert.Equal("QA", TrendyolStorefrontPolicy.NextProbe(Codes, activity, Now));
        activity["QA"] = TrendyolStorefrontPolicy.Read(null, Now, receivedOrders: false);
        Assert.Equal("SA", TrendyolStorefrontPolicy.NextProbe(Codes, activity, Now));
        activity["SA"] = TrendyolStorefrontPolicy.Read(activity["SA"], Now, receivedOrders: false);
        Assert.Null(TrendyolStorefrontPolicy.NextProbe(Codes, activity, Now));
    }

    [Fact]
    public void UnlistedStorefrontIsPausedForThirtyDaysAfterARejection()
    {
        var rejected = TrendyolStorefrontPolicy.Rejected("QA", null, Now);
        var activity = new Dictionary<string, TrendyolStorefrontActivity> { ["QA"] = rejected };

        Assert.Equal(Now.AddDays(30), rejected.PausedUntil);
        Assert.DoesNotContain("QA", TrendyolStorefrontPolicy.Readable(Codes, activity, Now.AddDays(29)));
        Assert.Contains("QA", TrendyolStorefrontPolicy.Readable(Codes, activity, Now.AddDays(31)));
        Assert.Null(TrendyolStorefrontPolicy.Rejected("DE", null, Now).PausedUntil);
    }

    [Fact]
    public void ProbeReadsSinceTheLastProbeButAtMostOneStreamWindow()
    {
        var overlap = TimeSpan.FromMinutes(2);

        Assert.Equal(Now.AddHours(-6) - overlap, TrendyolStorefrontPolicy.ProbeStart(new(null, Now.AddHours(-6)), Now, overlap));
        Assert.Equal(Now.AddDays(-14), TrendyolStorefrontPolicy.ProbeStart(null, Now, overlap));
        Assert.Equal(Now.AddDays(-14), TrendyolStorefrontPolicy.ProbeStart(new(null, Now.AddDays(-40)), Now, overlap));
    }

    [Fact]
    public void ReadingOrdersMakesAStorefrontActive()
    {
        var seen = TrendyolStorefrontPolicy.Read(null, Now, receivedOrders: true);
        var activity = new Dictionary<string, TrendyolStorefrontActivity> { ["DE"] = seen };

        Assert.Contains("DE", TrendyolStorefrontPolicy.Active(Codes, activity, Now));
        Assert.Equal(Now, TrendyolStorefrontPolicy.Read(seen, Now.AddHours(1), receivedOrders: false).LastOrderAt);
    }
}
