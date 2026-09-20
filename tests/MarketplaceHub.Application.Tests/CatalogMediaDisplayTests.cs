using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CatalogMediaDisplayTests
{
    [Fact]
    public void Url_PreservesRemoteProductMediaUrl()
    {
        var assetId = Guid.NewGuid();

        var result = CatalogMediaDisplay.Url(assetId, "PRODUCT_MEDIA_URL", "https://cdn.example.test/product.jpg");

        Assert.Equal("https://cdn.example.test/product.jpg", result);
    }

    [Fact]
    public void UrlUsesFileEndpointOnlyForStoredMedia()
    {
        var assetId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var result = CatalogMediaDisplay.Url(assetId, "PRODUCT_MEDIA", "products/product.jpg");

        Assert.Equal("/api/v1/files/product-media/11111111-2222-3333-4444-555555555555/content", result);
    }
}
