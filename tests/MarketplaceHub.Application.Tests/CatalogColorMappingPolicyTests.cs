using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CatalogColorMappingPolicyTests
{
    [Theory]
    [InlineData("Saks", "MAVI")]
    [InlineData("Kiremit", "TURUNCU")]
    [InlineData("Mürdüm", "MOR")]
    public void FallbackKeys_MapsSimilarWebColorsToPanelColors(string webColor, string expected)
    {
        Assert.Contains(expected, CatalogColorMappingPolicy.FallbackKeys(webColor));
    }

    [Fact]
    public void FallbackKeys_ReturnsNoGuessForUnknownColor()
    {
        Assert.Empty(CatalogColorMappingPolicy.FallbackKeys("Tamamen farklı bir renk"));
    }

    [Theory]
    [InlineData("STOCK_RECONCILE_SHORT")]
    [InlineData("STOCK_RECONCILE_MEDIUM")]
    [InlineData("STOCK_RECONCILE_DAILY")]
    public void RequiresExternalWrites_IdentifiesStockReconciliationPolicies(string resource)
    {
        Assert.True(MarketplaceSyncPolicyRules.RequiresExternalWrites(resource));
    }

    [Theory]
    [InlineData("ORDER_SYNC")]
    [InlineData("PRODUCT_SYNC")]
    [InlineData("RETURN_SYNC")]
    public void RequiresExternalWrites_DoesNotBlockReadOnlyPolicies(string resource)
    {
        Assert.False(MarketplaceSyncPolicyRules.RequiresExternalWrites(resource));
    }
}
