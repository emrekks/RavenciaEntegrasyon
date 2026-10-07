using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceCursorProgressPolicyTests
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumStallAfter = TimeSpan.FromMinutes(15);

    [Fact]
    public void NoSuccessfulRunIsUnknown()
    {
        var now = DateTimeOffset.UtcNow;

        var status = MarketplaceCursorProgressPolicy.Classify(
            lastSuccessAt: null, lastCursorAdvancedAt: null, cursorStagnantSince: null, lastReceivedCount: 0,
            now, Cadence, MinimumStallAfter);

        Assert.Equal(MarketplaceCursorProgressStatus.Unknown, status);
    }

    [Fact]
    public void SuccessfulEmptyPollDoesNotRaiseStalledStatus()
    {
        var now = DateTimeOffset.UtcNow;

        var status = MarketplaceCursorProgressPolicy.Classify(
            lastSuccessAt: now, lastCursorAdvancedAt: now.AddDays(-30), cursorStagnantSince: now.AddDays(-30), lastReceivedCount: 0,
            now, Cadence, MinimumStallAfter);

        Assert.Equal(MarketplaceCursorProgressStatus.NoData, status);
    }

    [Fact]
    public void DataWithoutAnyRecordedCursorProgressIsUnknownRatherThanAFalseAlarm()
    {
        var now = DateTimeOffset.UtcNow;

        var status = MarketplaceCursorProgressPolicy.Classify(
            lastSuccessAt: now, lastCursorAdvancedAt: null, cursorStagnantSince: null, lastReceivedCount: 4,
            now, Cadence, MinimumStallAfter);

        Assert.Equal(MarketplaceCursorProgressStatus.Unknown, status);
    }

    [Fact]
    public void RecentProgressWithReceivedRecordsIsCurrent()
    {
        var now = DateTimeOffset.UtcNow;

        var status = MarketplaceCursorProgressPolicy.Classify(
            lastSuccessAt: now, lastCursorAdvancedAt: now.AddMinutes(-14), cursorStagnantSince: now.AddMinutes(-14), lastReceivedCount: 10,
            now, Cadence, MinimumStallAfter);

        Assert.Equal(MarketplaceCursorProgressStatus.Current, status);
    }

    [Fact]
    public void ReceivedRecordsWithObservedStagnationPastThreeCyclesIsStalled()
    {
        var now = DateTimeOffset.UtcNow;

        var status = MarketplaceCursorProgressPolicy.Classify(
            lastSuccessAt: now, lastCursorAdvancedAt: now.AddDays(-4), cursorStagnantSince: now.AddMinutes(-16), lastReceivedCount: 10,
            now, Cadence, MinimumStallAfter);

        Assert.Equal(MarketplaceCursorProgressStatus.Stalled, status);
    }

    [Fact]
    public void MultiDayScheduleUsesThreeExpectedCyclesBeforeStalling()
    {
        var now = DateTimeOffset.UtcNow;

        var status = MarketplaceCursorProgressPolicy.Classify(
            lastSuccessAt: now, lastCursorAdvancedAt: now.AddDays(-4), cursorStagnantSince: now.AddDays(-2), lastReceivedCount: 2,
            now, TimeSpan.FromDays(1), MinimumStallAfter);

        Assert.Equal(MarketplaceCursorProgressStatus.Current, status);
    }

    [Fact]
    public void OldCursorWithoutObservedStagnationIsNotFlagged()
    {
        var now = DateTimeOffset.UtcNow;

        var status = MarketplaceCursorProgressPolicy.Classify(
            lastSuccessAt: now, lastCursorAdvancedAt: now.AddDays(-30), cursorStagnantSince: null, lastReceivedCount: 5,
            now, Cadence, MinimumStallAfter);

        Assert.Equal(MarketplaceCursorProgressStatus.Current, status);
    }
}
