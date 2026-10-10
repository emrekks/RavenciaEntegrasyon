using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductCatalogSyncLaneTests
{
    [Theory]
    [InlineData(MarketplaceJobTypes.ProductSync)]
    [InlineData(MarketplaceJobTypes.ShopifyProductSync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaProductSync)]
    public void ProductCatalogSyncJobsHaveAnIndependentWorkerLane(string jobType)
    {
        Assert.True(MarketplaceHub.Worker.Worker.IsProductCatalogSyncJob(jobType));
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.OrderSync)]
    [InlineData(MarketplaceJobTypes.ReferenceSync)]
    [InlineData(MarketplaceJobTypes.ProductCreate)]
    public void NonCatalogJobsDoNotUseTheProductCatalogLane(string jobType)
    {
        Assert.False(MarketplaceHub.Worker.Worker.IsProductCatalogSyncJob(jobType));
    }
}
