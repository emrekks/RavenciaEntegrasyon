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
    public void SilenceCountsOnlyWhenThereWereOrdersToDeliver()
    {
        // A store without orders for seven hours has a quiet webhook, not a broken one.
        Assert.True(WebhookPollingPolicy.IsHealthy(Now.AddHours(-7), Now.AddDays(-3), [], Now, ordersArrivedSinceLastSign: false));
        Assert.False(WebhookPollingPolicy.IsHealthy(Now.AddHours(-7), Now.AddDays(-3), [], Now, ordersArrivedSinceLastSign: true));
        // Two missed orders break it whatever the silence says.
        Assert.False(WebhookPollingPolicy.IsHealthy(Now.AddMinutes(-5), Now.AddDays(-3), [Now.AddHours(-2), Now.AddHours(-1)], Now, ordersArrivedSinceLastSign: false));
    }

    [Fact]
    public void OnlyOrderPollsAreStretchedAndRightAfterRegistrationNothingCountsAsMissed()
    {
        // A missed notification is still read within three minutes on every platform.
        Assert.Equal(180, WebhookPollingPolicy.StretchedIntervalSeconds("TRENDYOL", "ORDERS"));
        Assert.Equal(180, WebhookPollingPolicy.StretchedIntervalSeconds("HEPSIBURADA", "ORDERS"));
        Assert.Equal(3600, WebhookPollingPolicy.StretchedIntervalSeconds("TRENDYOL", "ORDER_RECOVERY"));
        // Trendyol and Shopify order reads bring cancellations; Hepsiburada's come from its open-order scan.
        Assert.Equal(3600, WebhookPollingPolicy.StretchedIntervalSeconds("TRENDYOL", "ORDER_LIFECYCLE"));
        Assert.Equal(3600, WebhookPollingPolicy.StretchedIntervalSeconds("SHOPIFY", "ORDER_LIFECYCLE"));
        Assert.Null(WebhookPollingPolicy.StretchedIntervalSeconds("HEPSIBURADA", "ORDER_LIFECYCLE"));
        Assert.Null(WebhookPollingPolicy.StretchedIntervalSeconds("TRENDYOL", "RETURNS"));
        Assert.False(WebhookPollingPolicy.CountsAsGap(Now.AddMinutes(-5), Now));
        Assert.True(WebhookPollingPolicy.CountsAsGap(Now.AddMinutes(-30), Now));
        Assert.False(WebhookPollingPolicy.CountsAsGap(null, Now));
    }

    [Fact]
    public void TheOrderTheLastNotificationBroughtDoesNotMakeTheSilenceCount()
    {
        // Its ingest job writes it seconds after the notification; only a later order shows orders without notifications.
        var after = WebhookPollingPolicy.UnnotifiedOrdersAfter(Now);
        Assert.False(Now.AddSeconds(40) > after);
        Assert.True(Now.AddMinutes(11) > after);
    }

    [Fact]
    public void AnOrderThePollReadsBeforeItsNotificationIsNotAMiss()
    {
        // Trendyol order times are stored three hours ahead (Turkey wall time); the cut-off moves with them.
        var cutoff = WebhookPollingPolicy.MissedOrderCutoff(Now, TimeSpan.FromHours(3));
        Assert.Equal(Now.AddHours(3).AddMinutes(-10), cutoff);
        Assert.False(Now.AddHours(3).AddSeconds(-20) <= cutoff);
        Assert.True(Now.AddHours(3).AddMinutes(-30) <= cutoff);
    }

    [Fact]
    public void OnlyMissesKeptInTheCurrentFormatCount()
    {
        var missed = new[] { Now.AddHours(-1) };
        var cursor = MarketplaceHub.Infrastructure.Persistence.ScheduledJobProducer.WebhookGapsCursor(missed);

        Assert.Equal(missed, MarketplaceHub.Infrastructure.Persistence.ScheduledJobProducer.WebhookGaps(cursor));
        // The earlier bare list also held orders read moments before their notification; it no longer counts.
        Assert.Empty(MarketplaceHub.Infrastructure.Persistence.ScheduledJobProducer.WebhookGaps("""["2026-10-18T11:00:00+00:00","2026-10-18T11:30:00+00:00"]"""));
        Assert.Empty(MarketplaceHub.Infrastructure.Persistence.ScheduledJobProducer.WebhookGaps("{bozuk"));
    }
}

public sealed class ConnectionRetestPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 19, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("PAZARAMA_GATEWAY_BLOCKED", true)]
    [InlineData("TRENDYOL_RATE_LIMITED", true)]
    [InlineData("N11_REMOTE_ERROR", true)]
    [InlineData("HEPSIBURADA_TIMEOUT", true)]
    [InlineData("REMOTE_TIMEOUT", true)]
    [InlineData("PAZARAMA_CREDENTIALS_REJECTED", false)]
    [InlineData("PAZARAMA_ACCESS_DENIED", false)]
    [InlineData("SHOPIFY_REQUIRED_READ_SCOPE", false)]
    [InlineData(null, false)]
    public void OnlyFaultsThatComeAndGoAreRetried(string? code, bool transient) =>
        Assert.Equal(transient, ConnectionRetestPolicy.IsTransient(code));

    [Fact]
    public void ADraftWhoseTestFailedOnAPassingFaultIsRetestedAfterAQuarterHour()
    {
        Assert.True(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddMinutes(-16), "PAZARAMA_GATEWAY_BLOCKED", Now));
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddMinutes(-5), "PAZARAMA_GATEWAY_BLOCKED", Now));
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", null, "PAZARAMA_GATEWAY_BLOCKED", Now));
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddHours(-1), "PAZARAMA_CREDENTIALS_REJECTED", Now));
        Assert.False(ConnectionRetestPolicy.ShouldRetest("VERIFIED", Now.AddHours(-1), "PAZARAMA_GATEWAY_BLOCKED", Now));
    }

    [Theory]
    [InlineData("PAZARAMA_CREDENTIALS_REJECTED", true)]
    [InlineData("N11_CREDENTIALS_REJECTED", true)]
    [InlineData("PAZARAMA_ACCESS_DENIED", true)]
    [InlineData("ACCESS_DENIED", true)]
    [InlineData("SHOPIFY_REQUIRED_READ_SCOPE", false)]
    [InlineData("PAZARAMA_GATEWAY_BLOCKED", false)]
    [InlineData(null, false)]
    public void CredentialAndAccessRefusalsAreKnown(string? code, bool refusal) =>
        Assert.Equal(refusal, ConnectionRetestPolicy.IsAuthRefusal(code));

    [Fact]
    public void ARefusedButUnchangedCredentialIsRetestedWithADoublingPauseForADay()
    {
        // The identity service refused the credentials saved two hours ago, first right after the save: an hour later.
        Assert.True(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddMinutes(-61), "PAZARAMA_CREDENTIALS_REJECTED", Now, Now.AddMinutes(-122)));
        Assert.True(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddMinutes(-61), "PAZARAMA_ACCESS_DENIED", Now, Now.AddMinutes(-122)));
        // At least an hour, not every quarter hour.
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddMinutes(-30), "PAZARAMA_CREDENTIALS_REJECTED", Now, Now.AddMinutes(-31)));
        // Refused for two hours at the last test: the next retest waits two hours, not one.
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddMinutes(-61), "PAZARAMA_CREDENTIALS_REJECTED", Now, Now.AddHours(-3)));
        Assert.True(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddMinutes(-121), "PAZARAMA_CREDENTIALS_REJECTED", Now, Now.AddHours(-4)));
        // Refused for eight hours: eight hours of quiet.
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddHours(-7), "PAZARAMA_CREDENTIALS_REJECTED", Now, Now.AddHours(-15)));
        Assert.True(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddHours(-8), "PAZARAMA_CREDENTIALS_REJECTED", Now, Now.AddHours(-16)));
        // A day after the credential was saved the retries stop.
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddHours(-2), "PAZARAMA_CREDENTIALS_REJECTED", Now, Now.AddHours(-25)));
        // Credentials changed after the refused test: the new ones get their own test, not a retry.
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddHours(-2), "PAZARAMA_CREDENTIALS_REJECTED", Now, Now.AddMinutes(-30)));
        // Without a known credential nothing is retried; a scope the seller must grant is never retried.
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddHours(-2), "PAZARAMA_CREDENTIALS_REJECTED", Now));
        Assert.False(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddHours(-2), "SHOPIFY_REQUIRED_READ_SCOPE", Now, Now.AddHours(-3)));
        // Passing faults keep the quarter-hour cadence whatever the credential's age.
        Assert.True(ConnectionRetestPolicy.ShouldRetest("DRAFT", Now.AddMinutes(-16), "PAZARAMA_GATEWAY_BLOCKED", Now, Now.AddDays(-10)));
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

    [Fact]
    public void APackageEventFindsItsPackageInThePathAndUnpackListsItsOrders()
    {
        var deliver = MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaWebhookIdentity.Create("""{"deliveredDate":"2026-10-19T10:00:00"}"""u8.ToArray(), "packages/57b1850c/deliver");
        var messageId = $"{Guid.NewGuid():N}:{deliver!.ExternalMessageId}";

        var path = MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.OrderEventPath(messageId);
        Assert.Equal("packages/57b1850c/deliver", path);
        Assert.Equal(("packages", "57b1850c"), MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.HepsiburadaEventReference(path));
        Assert.Equal(("lineitems", "b17553de"), MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.HepsiburadaEventReference("lineitems/b17553de/cancel"));
        Assert.Null(MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.HepsiburadaEventReference("orders"));
        Assert.Null(MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.OrderEventPath("returns:claim-1:ABC"));

        using var unpack = System.Text.Json.JsonDocument.Parse("""{"packageNumber":"57b1850c","orderNumbers":["4736002251","4736002252"]}""");
        Assert.Equal(["4736002251", "4736002252"], MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.FindStrings(unpack.RootElement, "orderNumbers"));
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
    // The metrics use the worker's real lanes (audit 2026-10-20 Q21); the four earlier codes stay.
    [Theory]
    [InlineData(-2, MarketplaceJobTypes.ShipmentAction, "USER")]
    [InlineData(-1, MarketplaceJobTypes.OrderSync, "INITIAL")]
    [InlineData(0, MarketplaceJobTypes.WebhookIngest, "HOT")]
    [InlineData(2, MarketplaceJobTypes.ProductCreate, "HOT")]
    [InlineData(3, MarketplaceJobTypes.OrderReconciliation, "BACKGROUND")]
    [InlineData(0, MarketplaceJobTypes.OrderStatusSync, "ORDER_STATUS")]
    [InlineData(2, MarketplaceJobTypes.ReturnSync, "RETURNS_READ")]
    public void JobsBelongToTheLaneThatLeasesThem(int priority, string jobType, string lane) =>
        Assert.Equal(lane, MarketplaceHub.Infrastructure.Persistence.QueueHealthService.LaneCode(MarketplaceHub.Infrastructure.Jobs.WorkerLanes.LaneFor(jobType, priority)));

    [Fact]
    public void TheLaneExpressionFollowsTheWorkerLanesInOrder()
    {
        var sql = MarketplaceHub.Infrastructure.Persistence.QueueHealthService.LaneCaseSql(MarketplaceHub.Infrastructure.Jobs.WorkerLanes.All(), out var parameters);

        Assert.StartsWith("CASE WHEN job.\"Priority\" <= -2 AND", sql);
        Assert.EndsWith("ELSE 'unassigned' END", sql);
        Assert.Equal(MarketplaceHub.Infrastructure.Jobs.WorkerLanes.All().Count * 3, parameters.Count);
    }
}

public sealed class HepsiburadaStatusListCadenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheShippedDeliveredAndUndeliveredListsRideAlongEveryFiveMinutesOrOnAFullRead()
    {
        Assert.True(HepsiburadaPackagePolicy.ShouldReadStatusLists(null, Now, fullRead: false));
        Assert.False(HepsiburadaPackagePolicy.ShouldReadStatusLists(Now.AddMinutes(-1), Now, fullRead: false));
        Assert.True(HepsiburadaPackagePolicy.ShouldReadStatusLists(Now.AddMinutes(-5), Now, fullRead: false));
        Assert.True(HepsiburadaPackagePolicy.ShouldReadStatusLists(Now.AddMinutes(-1), Now, fullRead: true));
    }
}

public sealed class HepsiburadaOpenPackageRowTests
{
    [Fact]
    public void AnOpenPackageRowNamesItsOrderThroughItsItems()
    {
        // The documented open-package list row: no top-level order number, items carry it.
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"id":"6ad4ab08-2a36-562c-3232-45d406060606","packageNumber":"5528399494","orderDate":"2026-10-18T14:18:00","status":"Packaged",
             "items":[{"lineItemId":"l-1","orderNumber":"4588521195","hbSku":"HBCV000084MLD6","merchantSku":"MZ005S24","productName":"Anne Penye Bluz","quantity":1,"price":{"amount":525,"currency":"TRY"},"totalPrice":{"amount":525,"currency":"TRY"},"vatRate":10}]}
            """);
        var package = MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaJsonMapper.OrderPackage(document.RootElement);

        Assert.Equal("4588521195", package.ExternalOrderId);
        Assert.Equal("5528399494", package.Package.ExternalPackageId);
    }

    [Fact]
    public void ARowWithoutAPackageNumberStillNamesItsOrder()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"id":"6ad4ab08","items":[{"orderNumber":"4091991027"}]}""");

        Assert.Throws<System.Text.Json.JsonException>(() => MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaJsonMapper.OrderPackage(document.RootElement));
        Assert.Equal("4091991027", MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaJsonMapper.PackageOrderNumber(document.RootElement));
    }
}
