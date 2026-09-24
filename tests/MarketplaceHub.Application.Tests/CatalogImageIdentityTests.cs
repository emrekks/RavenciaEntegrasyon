using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CatalogImageIdentityTests
{
    [Fact]
    public void DistinctUrls_RemovesRefetchQueryAndFragmentDifferences()
    {
        var urls = CatalogImageIdentity.DistinctUrls([
            "https://cdn.example.test/products/item.jpg?width=800&cache=one#gallery",
            "https://cdn.example.test/products/item.jpg?width=1200&cache=two",
            "https://cdn.example.test/products/item-back.jpg?width=800"
        ]);

        Assert.Equal([
            "https://cdn.example.test/products/item.jpg",
            "https://cdn.example.test/products/item-back.jpg"
        ], urls);
    }

    [Fact]
    public void DistinctUrls_KeepsDifferentImagePaths()
    {
        var urls = CatalogImageIdentity.DistinctUrls([
            "https://cdn.example.test/products/item-front.jpg",
            "https://cdn.example.test/products/item-back.jpg"
        ]);

        Assert.Equal(2, urls.Count);
    }

    [Fact]
    public void DistinctDisplayUrls_PreservesTheSharedOrderAcrossMediaScopes()
    {
        var urls = CatalogImageIdentity.DistinctDisplayUrls([
            "/api/v1/files/product-media/root-primary/content",
            "/api/v1/files/product-media/variant-gray/content",
            "/api/v1/files/product-media/root-primary/content",
            "/api/v1/files/product-media/variant-green/content"
        ]);

        Assert.Equal([
            "/api/v1/files/product-media/root-primary/content",
            "/api/v1/files/product-media/variant-gray/content",
            "/api/v1/files/product-media/variant-green/content"
        ], urls);
    }
}
