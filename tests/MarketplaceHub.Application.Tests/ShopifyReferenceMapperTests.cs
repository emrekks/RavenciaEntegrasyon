using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyReferenceMapperTests
{
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
