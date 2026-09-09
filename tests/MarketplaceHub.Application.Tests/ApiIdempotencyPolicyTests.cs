using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ApiIdempotencyPolicyTests
{
    [Fact]
    public void CompletedSamePayload_IsReplayable()
    {
        Assert.Equal(ApiIdempotencyDecisionKind.Replay, ApiIdempotencyPolicy.Resolve("COMPLETED", "hash-a", "hash-a"));
    }

    [Fact]
    public void SameKeyWithDifferentPayload_IsRejected()
    {
        Assert.Equal(ApiIdempotencyDecisionKind.KeyReused, ApiIdempotencyPolicy.Resolve("COMPLETED", "hash-a", "hash-b"));
        Assert.Equal(ApiIdempotencyDecisionKind.KeyReused, ApiIdempotencyPolicy.Resolve("IN_PROGRESS", "hash-a", "hash-b"));
    }

    [Fact]
    public void UnfinishedSamePayload_IsNotReplayable()
    {
        Assert.Equal(ApiIdempotencyDecisionKind.InProgress, ApiIdempotencyPolicy.Resolve("IN_PROGRESS", "hash-a", "hash-a"));
        Assert.Equal(ApiIdempotencyDecisionKind.Unknown, ApiIdempotencyPolicy.Resolve("UNKNOWN", "hash-a", "hash-a"));
    }
}
