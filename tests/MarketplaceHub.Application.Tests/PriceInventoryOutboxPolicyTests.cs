using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PriceInventoryOutboxPolicyTests
{
    [Fact]
    public void ReturningToAnEarlierPayloadAfterANewRevisionGetsANewDedupKey()
    {
        var connectionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var variantId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var offerId = Guid.Parse("30000000-0000-0000-0000-000000000003");

        var first = Line(variantId, offerId, quantity: 10, projectionVersion: 1, priceVersion: 1);
        var changed = Line(variantId, offerId, quantity: 9, projectionVersion: 2, priceVersion: 1);
        var returned = Line(variantId, offerId, quantity: 10, projectionVersion: 3, priceVersion: 1);

        Assert.NotEqual(
            PriceInventoryOutboxPolicy.DedupKey(connectionId, [first]),
            PriceInventoryOutboxPolicy.DedupKey(connectionId, [returned]));
        Assert.NotEqual(
            PriceInventoryOutboxPolicy.DedupKey(connectionId, [changed]),
            PriceInventoryOutboxPolicy.DedupKey(connectionId, [returned]));
    }

    [Fact]
    public void SameTargetRevisionGetsTheSameDedupKeyRegardlessOfLineOrder()
    {
        var connectionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var first = Line(Guid.Parse("20000000-0000-0000-0000-000000000002"), Guid.Parse("30000000-0000-0000-0000-000000000003"), 10, 4, 2);
        var second = Line(Guid.Parse("20000000-0000-0000-0000-000000000004"), Guid.Parse("30000000-0000-0000-0000-000000000005"), 5, 8, 1);

        Assert.Equal(
            PriceInventoryOutboxPolicy.DedupKey(connectionId, [first, second]),
            PriceInventoryOutboxPolicy.DedupKey(connectionId, [second, first]));
    }

    [Fact]
    public void OlderCompletionCannotBeAppliedToANewerTargetRevision()
    {
        var line = Line(
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            10,
            projectionVersion: 2,
            priceVersion: 1);

        Assert.False(PriceInventoryOutboxPolicy.IsCurrent(line, projectionVersion: 3, priceVersion: 1));
        Assert.True(PriceInventoryOutboxPolicy.IsCurrent(line, projectionVersion: 2, priceVersion: 1));
    }

    // Q-1/K-1: only a queued, running or succeeded job keeps the values' key.
    [Theory]
    [InlineData(JobStatus.Pending, true)]
    [InlineData(JobStatus.Leased, true)]
    [InlineData(JobStatus.RetryScheduled, true)]
    [InlineData(JobStatus.Succeeded, true)]
    [InlineData(JobStatus.Blocked, false)]
    [InlineData(JobStatus.Dead, false)]
    [InlineData(JobStatus.Cancelled, false)]
    [InlineData(JobStatus.ManualReview, false)]
    public void OnlyAJobStillStandingForItsValuesHoldsTheDedupKey(JobStatus status, bool holds) =>
        Assert.Equal(holds, PriceInventoryOutboxPolicy.HoldsDedupKey(status));

    [Fact]
    public void TheAutomaticSendRetiresAFailedJobOnItsOwnTerms()
    {
        var now = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
        var justNow = now.AddMinutes(-5);
        var anHourAgo = now - PriceInventoryOutboxPolicy.AutomaticResendAfter;

        Assert.False(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.RetryScheduled, "REMOTE_5XX", null, now));
        Assert.False(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.Succeeded, null, anHourAgo, now));
        // Stopped before the request: the switch the send checks first is on again.
        Assert.True(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.Cancelled, null, justNow, now));
        Assert.True(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.Blocked, "EXTERNAL_WRITE_POLICY_DISABLED", justNow, now));
        Assert.True(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.Blocked, "EXTERNAL_WRITES_DISABLED", justNow, now));
        // Any other failure waits an hour, so one that has not cleared is not sent every scan.
        Assert.False(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.Blocked, "TRENDYOL_UNAUTHORIZED", justNow, now));
        Assert.True(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.Blocked, "TRENDYOL_UNAUTHORIZED", anHourAgo, now));
        Assert.False(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.Dead, "REMOTE_5XX", justNow, now));
        Assert.True(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.Dead, "REMOTE_5XX", anHourAgo, now));
        Assert.True(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.ManualReview, "PRICE_INVENTORY_BATCH_EXPIRED", anHourAgo, now));
        // Values the marketplace refused are not sent again on their own.
        Assert.False(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.Blocked, "PRICE_INVENTORY_REJECTED", now.AddDays(-3), now));
        Assert.False(PriceInventoryOutboxPolicy.AutomaticSendMayRetire(JobStatus.ManualReview, "PRICE_INVENTORY_PARTIAL_FAILURE", now.AddDays(-3), now));
    }

    [Fact]
    public void ARetiredKeyNamesTheJobThatGaveItUp()
    {
        var jobId = Guid.Parse("40000000-0000-0000-0000-000000000004");
        Assert.Equal("price-inventory:x:revision:AB:retired:40000000000000000000000000000004", PriceInventoryOutboxPolicy.RetiredDedupKey("price-inventory:x:revision:AB", jobId));
    }

    private static PriceInventoryPushLine Line(Guid variantId, Guid offerId, decimal quantity, long projectionVersion, long priceVersion) =>
        new(variantId, offerId, "barcode", quantity, 100, 90, "TRY", projectionVersion, priceVersion, $"price-{priceVersion}");
}
