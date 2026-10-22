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

    // Price and stock address a Shopify variant by the id an import stored (numeric, or a full gid); anything
    // else falls back to the barcode search.
    [Theory]
    [InlineData("44791234567890", "gid://shopify/ProductVariant/44791234567890")]
    [InlineData(" 44791234567890 ", "gid://shopify/ProductVariant/44791234567890")]
    [InlineData("gid://shopify/ProductVariant/12", "gid://shopify/ProductVariant/12")]
    [InlineData("8690000000011-A", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void VariantGid_IsTheStoredVariantIdOrNothing(string? value, string? expected) =>
        Assert.Equal(expected, ShopifyHttpClient.VariantGid(value));
}
