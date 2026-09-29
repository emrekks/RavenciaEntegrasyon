using MarketplaceHub.Infrastructure;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplacePortRouterTests
{
    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData(" trendyol ")]
    public void SelectPort_ResolvesTrendyolExplicitly(string platform)
    {
        var trendyol = new object();
        var shopify = new object();

        Assert.Same(trendyol, MarketplacePortRouter.SelectPort(platform, trendyol, shopify));
    }

    [Theory]
    [InlineData("SHOPIFY")]
    [InlineData(" shopify ")]
    public void SelectPort_ResolvesShopifyExplicitly(string platform)
    {
        var trendyol = new object();
        var shopify = new object();

        Assert.Same(shopify, MarketplacePortRouter.SelectPort(platform, trendyol, shopify));
    }

    [Theory]
    [InlineData("HEPSIBURADA")]
    [InlineData(" hepsiburada ")]
    public void SelectPort_ResolvesHepsiburadaExplicitly(string platform)
    {
        var hepsiburada = new object();

        Assert.Same(hepsiburada, MarketplacePortRouter.SelectPort(platform, new object(), new object(), hepsiburada));
    }

    [Theory]
    [InlineData("HEPSIBURADA")]
    [InlineData("UNKNOWN")]
    [InlineData(null)]
    public void SelectPort_RejectsUnregisteredPlatforms(string? platform)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => MarketplacePortRouter.SelectPort(platform, new object(), new object()));

        Assert.Contains("adaptör yönlendirmesi tanımlı değil", exception.Message);
    }

    [Fact]
    public void SelectPort_RejectsHepsiburadaWhenAdapterIsNotRegistered()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => MarketplacePortRouter.SelectPort("HEPSIBURADA", new object(), new object()));

        Assert.Contains("adaptör yönlendirmesi tanımlı değil", exception.Message);
    }
}
