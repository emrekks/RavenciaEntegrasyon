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
    [InlineData("lineitems/b17553de/cancel", true)]
    [InlineData("packages/013105889/intransit", true)]
    [InlineData("packages/013105889/undeliver", true)]
    [InlineData("orders/869304129/shippingaddress", true)]
    [InlineData("claims", false)]
    [InlineData("claims/packages", false)]
    [InlineData("claims/awaitingpreapproval", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void HepsiburadaOrderEventsAreKnownByTheirLastPathSegment(string? path, bool orderEvent) =>
        Assert.Equal(orderEvent, WebhookEventPath.IsHepsiburadaOrderEvent(path));

    [Fact]
    public void AnOrderEventIsIdentifiedByItsPathAndContent()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("""{"orderNumber":"4736002251","packages":[{"packageNumber":"57b1850c"}]}""");
        var cancel = MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaWebhookIdentity.Create(body, "lineitems/b17553de/cancel");
        var deliver = MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaWebhookIdentity.Create(body, "packages/57b1850c/deliver");
        var preApproval = MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaWebhookIdentity.Create("""{"notice":"x"}"""u8.ToArray(), "claims/awaitingpreapproval");
        Assert.Equal("CLAIM_EVENT", preApproval!.ResourceType);

        Assert.Equal("ORDER_EVENT", cancel!.ResourceType);
        Assert.NotEqual(cancel.ExternalMessageId, deliver!.ExternalMessageId);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("4736002251", MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.FindString(document.RootElement, "OrderNumber"));
        Assert.Equal("57b1850c", MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.FindString(document.RootElement, "packageNumber"));
    }
}

public sealed class HepsiburadaCancellationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CancelledOrdersAreReadForADayEveryQuarterHourAndForAMonthDaily()
    {
        Assert.Equal(new CancellationReadWindow(Now.AddDays(-30), true), HepsiburadaCancellationPolicy.Window(null, null, Now));
        Assert.Equal(new CancellationReadWindow(Now.AddDays(-30), true), HepsiburadaCancellationPolicy.Window(Now.AddMinutes(-1), Now.AddHours(-25), Now));
        Assert.Null(HepsiburadaCancellationPolicy.Window(Now.AddMinutes(-10), Now.AddHours(-2), Now));
        Assert.Equal(new CancellationReadWindow(Now.AddDays(-1), false), HepsiburadaCancellationPolicy.Window(Now.AddMinutes(-16), Now.AddHours(-2), Now));
    }

    [Fact]
    public void CancelledLinesWithoutAnOrderNumberAreSkipped()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"totalCount":2,"items":[{"orderNumber":"4736002251","lineItemId":"l-1","cancelDate":"2026-10-18T10:00:00Z","cancelledBy":"Customer"},{"lineItemId":"l-2"}]}""");
        var (items, total) = MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaJsonMapper.CancelledOrderLines(document.RootElement);

        Assert.Equal(2, total);
        var line = Assert.Single(items);
        Assert.Equal("4736002251", line.OrderNumber);
        Assert.Equal("l-1", line.LineItemId);
    }
}

public sealed class PazaramaOpenOrderPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ManyOpenOrdersAreReadThroughTheirOrderDateWindows()
    {
        var few = Enumerable.Range(0, 25).Select(day => Now.AddDays(-day)).ToArray();
        var many = few.Append(Now.AddDays(-40)).Append(Now.AddMonths(-5)).ToArray();

        Assert.Null(PazaramaOpenOrderPolicy.StartDay(few, 25, Now));
        Assert.Equal(Now.AddDays(-40).ToOffset(TimeSpan.FromHours(3)).Date, PazaramaOpenOrderPolicy.StartDay(many, 25, Now));
    }
}

public sealed class QueueLaneTests
{
    [Theory]
    [InlineData(-2, "USER")]
    [InlineData(-1, "INITIAL")]
    [InlineData(0, "HOT")]
    [InlineData(2, "HOT")]
    [InlineData(3, "BACKGROUND")]
    public void JobsBelongToTheLaneOfTheirPriority(int priority, string lane) =>
        Assert.Equal(lane, MarketplaceHub.Infrastructure.Persistence.QueueHealthService.LaneFor(priority));
}
