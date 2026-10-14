using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CatalogPricePolicyTests
{
    [Fact]
    public void EffectiveSalePrice_PrefersChannelOffer()
    {
        Assert.Equal(549m, CatalogPricePolicy.EffectiveSalePrice(549m, 599m, 699m));
    }

    [Fact]
    public void EffectiveSalePrice_FallsBackToVariantDefaultWithoutChannelOffer()
    {
        Assert.Equal(599m, CatalogPricePolicy.EffectiveSalePrice(null, 599m, 699m));
    }

    [Fact]
    public void EffectiveSalePrice_FallsBackToProductDefault()
    {
        Assert.Equal(599m, CatalogPricePolicy.EffectiveSalePrice(null, null, 599m));
    }

    [Fact]
    public void EffectiveSalePrice_IgnoresNonPositiveValues()
    {
        Assert.Null(CatalogPricePolicy.EffectiveSalePrice(0m, 0m, null));
    }

    [Fact]
    public void MinimumPositivePrice_IgnoresMissingAndNonPositivePrices()
    {
        Assert.Equal(599m, CatalogPricePolicy.MinimumPositivePrice([null, 0m, 699m, 599m]));
        Assert.Null(CatalogPricePolicy.MinimumPositivePrice([null, 0m, -1m]));
    }
}
