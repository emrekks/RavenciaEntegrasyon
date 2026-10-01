using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSyncContentionTests
{
    [Theory]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderSync)]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(MarketplaceJobTypes.ShopifyOrderSync)]
    public void TargetedReadRetriesRatherThanReportingSuccessWithoutProcessing(string jobType)
    {
        var result = MarketplaceSyncExecutionLock.ContentionResult(jobType,
            "{\"externalOrderId\":\"4736002251\",\"packageNumber\":\"5467917398\"}");
        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.False(result.Succeeded);
        Assert.Equal("TARGETED_ORDER_SYNC_BUSY", result.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"externalOrderId\":null}")]
    [InlineData("{\"externalOrderId\":\" \"}")]
    public void GeneralScheduledReadsStillCoalesce(string payload)
    {
        Assert.True(MarketplaceSyncExecutionLock.ContentionResult(MarketplaceJobTypes.HepsiburadaOrderSync, payload).Succeeded);
    }
}
