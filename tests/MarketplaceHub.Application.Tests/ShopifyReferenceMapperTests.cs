using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyReferenceMapperTests
{
    [Fact]
    public void Delivered_fulfillment_uses_delivery_time_instead_of_its_creation_time()
    {
        using var json = JsonDocument.Parse("""
        {
          "createdAt": "2026-05-19T18:12:07Z",
          "updatedAt": "2026-05-20T18:06:00Z",
          "deliveredAt": "2026-05-20T18:05:00Z",
          "events": { "nodes": [{ "status": "DELIVERED", "happenedAt": "2026-05-20T18:05:00Z" }] }
        }
        """);

        var occurredAt = ShopifyHttpClient.FulfillmentStatusOccurredAt(json.RootElement, "DELIVERED");

        Assert.Equal(new DateTimeOffset(2026, 5, 20, 18, 5, 0, TimeSpan.Zero), occurredAt);
    }

    [Fact]
    public void Delivered_fulfillment_uses_delivery_event_when_delivered_at_is_missing()
    {
        using var json = JsonDocument.Parse("""
        {
          "createdAt": "2026-05-19T18:12:07Z",
          "updatedAt": "2026-05-20T18:06:00Z",
          "deliveredAt": null,
          "events": { "nodes": [{ "status": "DELIVERED", "happenedAt": "2026-05-20T18:05:00Z" }] }
        }
        """);

        var occurredAt = ShopifyHttpClient.FulfillmentStatusOccurredAt(json.RootElement, "DELIVERED");

        Assert.Equal(new DateTimeOffset(2026, 5, 20, 18, 5, 0, TimeSpan.Zero), occurredAt);
    }

    [Fact]
    public void ProductTypesMapToAFlatCategoryListKeyedByTheExactTypeText()
    {
        using var json = JsonDocument.Parse("""
        ["Bluz", "Büyük Beden Bluz", "Bluz", "", "  ", "Alt - Üst Takım "]
        """);

        var categories = ShopifyHttpClient.MapProductTypes(json.RootElement);

        Assert.Equal(["Bluz", "Büyük Beden Bluz", "Alt - Üst Takım "], categories.Select(category => category.ExternalId));
        Assert.All(categories, category =>
        {
            Assert.Equal("CATEGORIES", category.ResourceType);
            Assert.Null(category.ParentExternalId);
            Assert.Equal(0, category.Depth);
            Assert.True(category.IsLeaf);
            Assert.True(category.IsActive);
            Assert.Equal(category.Name, category.Path);
        });
        Assert.Equal("Alt - Üst Takım", categories[2].Name);
    }

    [Fact]
    public void ProductTypesRejectANonTextEntry()
    {
        using var json = JsonDocument.Parse("""["Bluz", 3]""");

        Assert.Throws<JsonException>(() => ShopifyHttpClient.MapProductTypes(json.RootElement));
    }

    [Theory]
    [InlineData("Bluz", "Anne Bluz", "Bluz")]
    [InlineData("Alt - Üst Takım ", "Takım", "Alt - Üst Takım ")]
    [InlineData(null, " Anne Bluz ", "Anne Bluz")]
    [InlineData(null, null, null)]
    public void PublicationSendsTheMappedProductTypeBeforeThePanelCategoryName(string? mapped, string? panel, string? expected)
    {
        Assert.Equal(expected, ShopifyProductPublicationComposer.ShopifyProductType(mapped, panel));
    }
}
