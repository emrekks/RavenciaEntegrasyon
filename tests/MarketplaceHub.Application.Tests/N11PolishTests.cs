using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11PolishTests
{
    [Theory]
    [InlineData("TRENDYOL_PRICE_INVENTORY_SYNC", "N11", "N11")]
    [InlineData("SHIPMENT_ACTION", "N11", "N11")]
    [InlineData("MARKETPLACE_QUESTION_SYNC", "N11", "N11")]
    [InlineData("TRENDYOL_PRICE_INVENTORY_SYNC", "TRENDYOL", "Trendyol")]
    [InlineData("TRENDYOL_PRICE_INVENTORY_SYNC", null, "Trendyol")]
    [InlineData("HEPSIBURADA_ORDER_SYNC", "HEPSIBURADA", "Hepsiburada")]
    [InlineData("N11_ORDER_SYNC", null, "N11")]
    [InlineData("TRENDYOL_EFATURAM_INVOICE", "N11", "Trendyol e-Faturam")]
    public void JobMarketplaceFollowsTheConnectionWhenTheTypeIsShared(string jobType, string? platformCode, string expected) =>
        Assert.Equal(expected, JobOperationsService.Marketplace(jobType, platformCode));

    [Fact]
    public void LoggedRequestContextDoesNotPrintTheKeyPair()
    {
        var context = new N11RequestContext(new PlatformConnection { Id = Guid.Parse("01a13c0d-cf96-7492-9e5f-bf4a6a37fa84"), PlatformCode = "N11", Environment = "PRODUCTION", DisplayName = "N11", ExternalStoreId = "x", Status = "ACTIVE", ApiVersion = "V1" }, "my-app-key", "my-app-secret");

        var text = context.ToString();

        Assert.DoesNotContain("my-app-key", text);
        Assert.DoesNotContain("my-app-secret", text);
        Assert.Contains("01a13c0d-cf96-7492-9e5f-bf4a6a37fa84", text);
    }
}
