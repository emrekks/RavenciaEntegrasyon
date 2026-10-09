using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
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
    public void PriceInventoryPayloadAcceptsValidUniqueLines()
    {
        const string payload = """{"items":[{"barcode":"SKU-1","quantity":4,"salePrice":99.9,"listPrice":129.9}]}""";

        var result = ShopifyHttpClient.ParsePriceInventoryPayload(payload);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ShopifyPriceInventoryLine("SKU-1", 4, 99.9m, 129.9m), Assert.Single(result.Value!));
    }

    [Theory]
    [InlineData("""{"items":[{"barcode":"SKU-1","quantity":4,"salePrice":99.9,"listPrice":129.9},{"barcode":"SKU-1","quantity":2,"salePrice":99.9,"listPrice":129.9}]}""")]
    [InlineData("""{"items":[{"barcode":"SKU-1","quantity":-1,"salePrice":99.9,"listPrice":129.9}]}""")]
    [InlineData("""{"items":[{"barcode":"SKU-1","quantity":4,"salePrice":130,"listPrice":129.9}]}""")]
    public void PriceInventoryPayloadRejectsUnsafeLines(string payload)
    {
        var result = ShopifyHttpClient.ParsePriceInventoryPayload(payload);

        Assert.False(result.IsSuccess);
        Assert.Equal("SHOPIFY_PRICE_INVENTORY_PAYLOAD_INVALID", result.Error!.Code);
    }

    [Fact]
    public void ImmediateWriteOperationReturnsSuccessForEveryWrittenBarcode()
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new[] { "SKU-1", "SKU-2" }))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var result = ShopifyHttpClient.ParseImmediateOperation($"SHOPIFY_IMMEDIATE:{encoded}");

        Assert.True(result.IsSuccess);
        Assert.Equal("COMPLETED", result.Value!.Status);
        Assert.Equal(["SKU-1", "SKU-2"], result.Value.Lines.Select(line => line.ExternalKey));
        Assert.All(result.Value.Lines, line => Assert.True(line.Succeeded));
    }

    [Fact]
    public void TaxonomyCategoryMapsShopifyIdsHierarchyPathAndLifecycle()
    {
        using var json = JsonDocument.Parse("""
        {
          "id": "gid://shopify/TaxonomyCategory/aa-1-2",
          "name": "Tişörtler",
          "fullName": "Giyim ve Aksesuarlar > Giyim > Tişörtler",
          "parentId": "gid://shopify/TaxonomyCategory/aa-1",
          "level": 2,
          "isLeaf": true,
          "isArchived": false
        }
        """);

        var category = ShopifyHttpClient.MapTaxonomyCategory(json.RootElement);

        Assert.Equal("CATEGORIES", category.ResourceType);
        Assert.Equal("aa-1-2", category.ExternalId);
        Assert.Equal("aa-1", category.ParentExternalId);
        Assert.Equal("Tişörtler", category.Name);
        Assert.Equal("Giyim ve Aksesuarlar > Giyim > Tişörtler", category.Path);
        Assert.Equal(2, category.Depth);
        Assert.True(category.IsLeaf);
        Assert.True(category.IsActive);
        Assert.Contains("isArchived", category.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TaxonomyCategoryMarksArchivedEntriesInactiveAndKeepsRootParentEmpty()
    {
        using var json = JsonDocument.Parse("""
        {
          "id": "gid://shopify/TaxonomyCategory/root",
          "name": "Giyim ve Aksesuarlar",
          "fullName": "Giyim ve Aksesuarlar",
          "parentId": null,
          "level": 0,
          "isLeaf": false,
          "isArchived": true
        }
        """);

        var category = ShopifyHttpClient.MapTaxonomyCategory(json.RootElement);

        Assert.Equal("root", category.ExternalId);
        Assert.Null(category.ParentExternalId);
        Assert.Equal(0, category.Depth);
        Assert.False(category.IsLeaf);
        Assert.False(category.IsActive);
    }

    [Fact]
    public void TaxonomyCategoryMapperPreservesFlattenedHierarchyRows()
    {
        using var json = JsonDocument.Parse("""
        [
          { "id":"gid://shopify/TaxonomyCategory/root", "name":"Giyim", "fullName":"Giyim", "parentId":null, "level":0, "isLeaf":false, "isArchived":false },
          { "id":"gid://shopify/TaxonomyCategory/root/1", "name":"Üst Giyim", "fullName":"Giyim > Üst Giyim", "parentId":"gid://shopify/TaxonomyCategory/root", "level":1, "isLeaf":true, "isArchived":false }
        ]
        """);

        var categories = ShopifyHttpClient.MapTaxonomyCategories(json.RootElement);

        Assert.Equal(2, categories.Count);
        Assert.Equal("root", categories[1].ParentExternalId);
        Assert.Equal("Giyim > Üst Giyim", categories[1].Path);
    }
}
