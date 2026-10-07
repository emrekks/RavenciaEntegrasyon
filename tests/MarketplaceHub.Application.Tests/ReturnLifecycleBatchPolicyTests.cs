using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReturnLifecycleBatchPolicyTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(25, 12, 13)]
    [InlineData(101, 50, 50)]
    public void ReservesCapacityForBothCargoStates(int requestedSize, int expectedIncomplete, int expectedComplete)
    {
        Assert.Equal((expectedIncomplete, expectedComplete), ReturnLifecycleBatchPolicy.SplitBatch(requestedSize));
    }
}
