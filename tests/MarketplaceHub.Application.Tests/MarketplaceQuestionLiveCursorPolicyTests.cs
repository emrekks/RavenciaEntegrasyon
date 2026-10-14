using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceQuestionLiveCursorPolicyTests
{
    [Fact]
    public void AdvancesThroughEveryPageBeforeNextStatusAndKind()
    {
        var cursor = MarketplaceQuestionLiveCursorPolicy.FirstPage(0);
        var fullPage = new MarketplaceQuestionPage(Enumerable.Repeat(RemoteQuestion(), 50).ToArray(), 0, 3, 120);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor, fullPage, 50, 0, 2, 2);
        Assert.Equal(new MarketplaceQuestionLiveCursor(0, 0, 1), cursor);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor!, fullPage with { Page = 1 }, 50, 0, 2, 2);
        Assert.Equal(new MarketplaceQuestionLiveCursor(0, 0, 2), cursor);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor!, fullPage with { Page = 2, Items = Enumerable.Repeat(RemoteQuestion(), 20).ToArray() }, 50, 0, 2, 2);
        Assert.Equal(new MarketplaceQuestionLiveCursor(1, 0, 0), cursor);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor!, fullPage with { Items = [] }, 50, 0, 2, 2);
        Assert.Equal(new MarketplaceQuestionLiveCursor(0, 1, 0), cursor);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor!, fullPage with { Items = [] }, 50, 0, 2, 2);
        Assert.Equal(new MarketplaceQuestionLiveCursor(1, 1, 0), cursor);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor!, fullPage with { Items = [] }, 50, 0, 2, 2);
        Assert.Null(cursor);
    }

    [Fact]
    public void UsesOneBasedPageForHepsiburada()
    {
        var cursor = MarketplaceQuestionLiveCursorPolicy.FirstPage(1);
        var fullPage = new MarketplaceQuestionPage(Enumerable.Repeat(RemoteQuestion(), 25).ToArray(), 1, 2, 30);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor, fullPage, 25, 1, 1, 2);

        Assert.Equal(new MarketplaceQuestionLiveCursor(0, 0, 2), cursor);
    }

    [Fact]
    public void DoesNotRequestPagePastLastFullZeroBasedTrendyolPage()
    {
        var cursor = MarketplaceQuestionLiveCursorPolicy.FirstPage(0);
        var fullPage = new MarketplaceQuestionPage(Enumerable.Repeat(RemoteQuestion(), 50).ToArray(), 0, 3, 150);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor, fullPage, 50, 0, 1, 1);
        Assert.Equal(new MarketplaceQuestionLiveCursor(0, 0, 1), cursor);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor!, fullPage with { Page = 1 }, 50, 0, 1, 1);
        Assert.Equal(new MarketplaceQuestionLiveCursor(0, 0, 2), cursor);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor!, fullPage with { Page = 2 }, 50, 0, 1, 1);
        Assert.Null(cursor);
    }

    [Fact]
    public void ContinuesWhenFullPageDoesNotReportTotalPages()
    {
        var cursor = MarketplaceQuestionLiveCursorPolicy.FirstPage(0);
        var fullPageWithoutPageCount = new MarketplaceQuestionPage(Enumerable.Repeat(RemoteQuestion(), 50).ToArray(), 0, 0, 50);

        cursor = MarketplaceQuestionLiveCursorPolicy.Advance(cursor, fullPageWithoutPageCount, 50, 0, 1, 1);

        Assert.Equal(new MarketplaceQuestionLiveCursor(0, 0, 1), cursor);
    }

    private static RemoteMarketplaceQuestion RemoteQuestion() => new(
        "question-id", "PRODUCT", "WAITING_FOR_ANSWER", "question", null, null, null, null, null,
        null, null, DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch, []);
}
