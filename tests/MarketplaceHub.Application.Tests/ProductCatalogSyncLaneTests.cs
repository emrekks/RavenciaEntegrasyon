using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;
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
    [InlineData(null, 3)]
    [InlineData("5", 5)]
    [InlineData("0", 1)]
    [InlineData("50", 8)]
    public void LaneSlotsAreConfigurableAndBounded(string? configured, int expected)
    {
        var values = new Dictionary<string, string?>();
        if (configured is not null) values["Worker:LaneSlots:hot"] = configured;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(expected, MarketplaceHub.Worker.Worker.LaneSlots(configuration, "hot", 3));
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
