using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSyncHealthPolicyTests
{
    private static readonly TimeSpan DelayedAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DegradedAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(15);

    [Fact]
    public void RecentSuccessfulRunWithPartialRecordFailuresIsDegraded()
    {
        var now = DateTimeOffset.UtcNow;

        var health = MarketplaceSyncHealthPolicy.Classify(
            now.AddSeconds(-20), now, DelayedAfter, DegradedAfter, OfflineAfter,
            consecutiveFailureCount: 0, lastFailedCount: 3);

        Assert.Equal(MarketplaceSyncHealth.Degraded, health);
    }

    [Fact]
    public void RecentSuccessfulRunAfterFailedAttemptsRemainsDegradedUntilCleanRun()
    {
        var now = DateTimeOffset.UtcNow;

        var health = MarketplaceSyncHealthPolicy.Classify(
            now.AddSeconds(-20), now, DelayedAfter, DegradedAfter, OfflineAfter,
            consecutiveFailureCount: 2, lastFailedCount: 0);

        Assert.Equal(MarketplaceSyncHealth.Degraded, health);
    }

    [Fact]
    public void CleanRecentSuccessfulRunIsHealthy()
    {
        var now = DateTimeOffset.UtcNow;

        var health = MarketplaceSyncHealthPolicy.Classify(
            now.AddSeconds(-20), now, DelayedAfter, DegradedAfter, OfflineAfter);

        Assert.Equal(MarketplaceSyncHealth.Healthy, health);
    }

    [Fact]
    public void OfflineFreshnessTakesPrecedenceOverFailureCounts()
    {
        var now = DateTimeOffset.UtcNow;

        var health = MarketplaceSyncHealthPolicy.Classify(
            now.AddMinutes(-20), now, DelayedAfter, DegradedAfter, OfflineAfter,
            consecutiveFailureCount: 1, lastFailedCount: 1);

        Assert.Equal(MarketplaceSyncHealth.Offline, health);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(72)]
    public void ScheduledDailyAndMultiDayFlowsRemainHealthyUntilTheirNextExpectedRun(int cadenceHours)
    {
        var now = DateTimeOffset.UtcNow;
        var cadence = TimeSpan.FromHours(cadenceHours) + TimeSpan.FromMinutes(10);
        var lastSuccessAt = now - cadence - TimeSpan.FromMinutes(1);

        var health = MarketplaceSyncHealthPolicy.Classify(
            lastSuccessAt, now, DelayedAfter, DegradedAfter, OfflineAfter,
            expectedCadence: cadence);

        Assert.Equal(MarketplaceSyncHealth.Healthy, health);
    }

    [Fact]
    public void LongScheduleBecomesDelayedDegradedAndOfflineOnlyAfterItsExpectedCadence()
    {
        var now = DateTimeOffset.UtcNow;
        var cadence = TimeSpan.FromDays(1);

        Assert.Equal(MarketplaceSyncHealth.Delayed, ClassifyAfter(now, cadence + DelayedAfter, cadence));
        Assert.Equal(MarketplaceSyncHealth.Degraded, ClassifyAfter(now, cadence + DegradedAfter, cadence));
        Assert.Equal(MarketplaceSyncHealth.Offline, ClassifyAfter(now, cadence + OfflineAfter, cadence));
    }

    private static MarketplaceSyncHealth ClassifyAfter(DateTimeOffset now, TimeSpan age, TimeSpan cadence) =>
        MarketplaceSyncHealthPolicy.Classify(
            now - age, now, DelayedAfter, DegradedAfter, OfflineAfter,
            expectedCadence: cadence);
}
