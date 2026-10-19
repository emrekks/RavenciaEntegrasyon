using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyInventoryQuantityTests
{
    [Theory]
    [InlineData("""{"quantities":[{"name":"available","quantity":7}]}""", 7)]
    [InlineData("""{"quantities":[{"name":"on_hand","quantity":9},{"name":"available","quantity":4}]}""", 4)]
    [InlineData("""{"quantities":[{"quantity":0}]}""", 0)]
    public void AvailableQuantity_ReadsTheAvailableEntryOfTheList(string level, int expected)
    {
        using var document = JsonDocument.Parse(level);

        Assert.Equal(expected, ShopifyHttpClient.AvailableQuantity(document.RootElement));
    }

    [Theory]
    [InlineData("""{"quantities":[]}""")]
    [InlineData("""{"quantities":[{"name":"available","quantity":null}]}""")]
    [InlineData("""{"quantities":{"name":"available","quantity":3}}""")]
    [InlineData("""{}""")]
    public void AvailableQuantity_IsMissingWhenShopifyGaveNoNumber(string level)
    {
        using var document = JsonDocument.Parse(level);

        Assert.Null(ShopifyHttpClient.AvailableQuantity(document.RootElement));
    }
}
