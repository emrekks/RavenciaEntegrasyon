using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class WebhookPollingPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NotificationsAreHealthyWhileTheyArriveAndMissAtMostOneOrderADay()
    {
        Assert.True(WebhookPollingPolicy.IsHealthy(Now.AddMinutes(-20), Now.AddDays(-3), [], Now));
        Assert.True(WebhookPollingPolicy.IsHealthy(null, Now.AddHours(-1), [], Now));
        Assert.True(WebhookPollingPolicy.IsHealthy(Now.AddMinutes(-5), Now.AddDays(-3), [Now.AddHours(-2)], Now));
        Assert.False(WebhookPollingPolicy.IsHealthy(Now.AddMinutes(-5), Now.AddDays(-3), [Now.AddHours(-2), Now.AddHours(-1)], Now));
        Assert.True(WebhookPollingPolicy.IsHealthy(Now.AddMinutes(-5), Now.AddDays(-3), [Now.AddDays(-2), Now.AddHours(-1)], Now));
        Assert.False(WebhookPollingPolicy.IsHealthy(Now.AddHours(-7), Now.AddDays(-3), [], Now));
        Assert.False(WebhookPollingPolicy.IsHealthy(null, null, [], Now));
    }

    [Fact]
    public void OnlyOrderPollsAreStretchedAndRightAfterRegistrationNothingCountsAsMissed()
    {
        Assert.Equal(900, WebhookPollingPolicy.StretchedIntervalSeconds("ORDERS"));
        Assert.Equal(3600, WebhookPollingPolicy.StretchedIntervalSeconds("ORDER_LIFECYCLE"));
        Assert.Null(WebhookPollingPolicy.StretchedIntervalSeconds("RETURNS"));
        Assert.False(WebhookPollingPolicy.CountsAsGap(Now.AddMinutes(-5), Now));
        Assert.True(WebhookPollingPolicy.CountsAsGap(Now.AddMinutes(-30), Now));
        Assert.False(WebhookPollingPolicy.CountsAsGap(null, Now));
    }
}

public sealed class QuestionCadencePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, 120)]
    [InlineData(45, 240)]
    [InlineData(70, 480)]
    [InlineData(200, 600)]
    public void QuietConnectionsAreAskedLessOften(int minutesSinceLastQuestion, int expectedSeconds) =>
        Assert.Equal(expectedSeconds, QuestionCadencePolicy.IntervalSeconds(120, Now.AddMinutes(-minutesSinceLastQuestion), Now));

    [Fact]
    public void AConnectionWithoutQuestionsIsAskedEveryTenMinutes() =>
        Assert.Equal(600, QuestionCadencePolicy.IntervalSeconds(120, null, Now));
}

public sealed class ReturnRereadPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ClaimsAreReadNarrowlyEveryRunAndWidelyEveryHalfHour()
    {
        Assert.Equal(90, ReturnRereadPolicy.LookbackDays(true, 90, 45, Now.AddMinutes(-1), Now));
        Assert.Equal(45, ReturnRereadPolicy.LookbackDays(false, 90, 45, null, Now));
        Assert.Equal(2, ReturnRereadPolicy.LookbackDays(false, 90, 45, Now.AddMinutes(-10), Now));
        Assert.Equal(45, ReturnRereadPolicy.LookbackDays(false, 90, 45, Now.AddMinutes(-31), Now));
    }
}

public sealed class PlatformHistoryLimitsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("TRENDYOL", 3)]
    [InlineData("n11", 3)]
    [InlineData("PAZARAMA", 3)]
    [InlineData("hepsiburada", 1)]
    public void FirstImportReadsThePlatformsHistoryButAtMostThreeMonths(string platform, int months)
    {
        foreach (var kind in Enum.GetValues<PlatformHistoryKind>())
        {
            Assert.Equal(months, PlatformHistoryLimits.InitialMonths(platform, kind));
            Assert.Equal(Now.AddMonths(-months), PlatformHistoryLimits.InitialStart(platform, kind, Now));
        }
    }
}

public sealed class OrderBaselinePercentTests
{
    [Fact]
    public void WindowByWindowHistoryReadReportsHowFarItCame()
    {
        const string baseline = """{"Version":"v","Mode":0,"AnchorEnd":"2026-10-18T00:00:00+00:00","StartAt":"2026-07-20T00:00:00+00:00","WindowIndex":3,"NextCursor":null}""";
        const string incremental = """{"Version":"v","Mode":1,"AnchorEnd":"2026-10-18T00:00:00+00:00","StartAt":"2026-10-17T00:00:00+00:00","WindowIndex":0,"NextCursor":null}""";

        Assert.Equal(42, MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.OrderBaselinePercent(baseline));
        Assert.Null(MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.OrderBaselinePercent(incremental));
        Assert.Null(MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.OrderBaselinePercent("{bozuk"));
        Assert.Null(MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.OrderBaselinePercent(null));
    }
}

public sealed class WebhookEventPathTests
{
    [Theory]
    [InlineData("orders", true)]
    [InlineData("/Packages/", true)]
    [InlineData("order/cancel", true)]
    [InlineData("intransit", true)]
    [InlineData("undeliver", true)]
    [InlineData("claims", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void HepsiburadaOrderEventsAreKnownByTheirLastPathSegment(string? path, bool orderEvent) =>
        Assert.Equal(orderEvent, WebhookEventPath.IsHepsiburadaOrderEvent(path));

    [Fact]
    public void AnOrderEventIsIdentifiedByItsPathAndContent()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("""{"orderNumber":"4736002251","packages":[{"packageNumber":"57b1850c"}]}""");
        var cancel = MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaWebhookIdentity.Create(body, "cancel");
        var deliver = MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaWebhookIdentity.Create(body, "deliver");

        Assert.Equal("ORDER_EVENT", cancel!.ResourceType);
        Assert.NotEqual(cancel.ExternalMessageId, deliver!.ExternalMessageId);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("4736002251", MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.FindString(document.RootElement, "OrderNumber"));
        Assert.Equal("57b1850c", MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.FindString(document.RootElement, "packageNumber"));
    }
}
