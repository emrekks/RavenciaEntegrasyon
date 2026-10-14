using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Marketplace;

public static class MarketplaceConnectionTestDispatchPolicy
{
    public static (bool IsMarketplace, string JobType) Resolve(string? platformCode)
    {
        var isMarketplace = ActiveIntegrationScope.IsMarketplace(platformCode);
        var jobType = isMarketplace
            ? MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.ConnectionTest)
            : InvoicingJobTypes.ConnectionTest;

        return (isMarketplace, jobType);
    }
}
