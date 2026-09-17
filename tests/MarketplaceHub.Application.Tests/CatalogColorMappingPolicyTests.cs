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
    [InlineData("PRODUCT_WRITE")]
    [InlineData("PRICE_STOCK_WRITE")]
    [InlineData("SHIPMENT_WRITE")]
    [InlineData("RETURN_WRITE")]
    public void RequiresExternalWrites_IdentifiesWritePolicies(string resource)
    {
        Assert.True(MarketplaceSyncPolicyRules.RequiresExternalWrites(resource));
    }

    [Theory]
    [InlineData("PRODUCT_WRITE")]
    [InlineData("PRICE_STOCK_WRITE")]
    [InlineData("SHIPMENT_WRITE")]
    [InlineData("RETURN_WRITE")]
    public void ExternalWritePolicies_AreRecognizedAsIndependentControls(string resource)
    {
        Assert.True(MarketplaceExternalWritePolicies.IsPolicy(resource));
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
