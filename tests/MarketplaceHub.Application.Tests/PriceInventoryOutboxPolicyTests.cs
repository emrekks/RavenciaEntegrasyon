using MarketplaceHub.Application;
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

    private static PriceInventoryPushLine Line(Guid variantId, Guid offerId, decimal quantity, long projectionVersion, long priceVersion) =>
        new(variantId, offerId, "barcode", quantity, 100, 90, "TRY", projectionVersion, priceVersion, $"price-{priceVersion}");
}
