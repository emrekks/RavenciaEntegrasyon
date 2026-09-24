using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductPublicationComposerTests
{
    [Theory]
    [InlineData("http://cdn.example.com/product.jpg")]
    [InlineData("https://localhost/product.jpg")]
    [InlineData("https://images.local/product.jpg")]
    [InlineData("https://images.internal/product.jpg")]
    [InlineData("https://192.168.1.20/product.jpg")]
    [InlineData("https://user:password@cdn.example.com/product.jpg")]
    public void PublicationImageUrl_RejectsNonPublicAddresses(string value)
    {
        Assert.False(ProductPublicationComposer.IsPublicHttpsUrl(value));
    }

    [Fact]
    public void PublicationImageUrl_AcceptsPublicHttpsAddress()
    {
        Assert.True(ProductPublicationComposer.IsPublicHttpsUrl("https://cdn.example.com/product.jpg"));
    }

    [Fact]
    public void LocalProductMedia_UsesPublicHttpsContentRoute()
    {
        var assetId = Guid.Parse("0190abcd-1234-7123-8123-123456789abc");
        Assert.Equal($"https://panel.ravencia.com/api/v1/public/product-media/{assetId:D}/content",
            ProductPublicationComposer.BuildPublicProductMediaUrl("https://panel.ravencia.com", assetId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://panel.ravencia.com")]
    [InlineData("https://localhost")]
    [InlineData("https://panel.ravencia.com/subpath")]
    [InlineData("https://panel.ravencia.com?tenant=1")]
    public void LocalProductMedia_RejectsUnsafeOrAmbiguousPublicBaseUrls(string? baseUrl)
    {
        Assert.Null(ProductPublicationComposer.BuildPublicProductMediaUrl(baseUrl, Guid.NewGuid()));
    }

    [Fact]
    public void PublicationImages_AreDeduplicatedAndCappedInDisplayOrder()
    {
        var images = Enumerable.Range(1, 10).Select(number => $"https://cdn.example.com/{number}.jpg").ToList();
        images.Insert(1, images[0]);

        var selected = ProductPublicationComposer.SelectPublicationImageUrls(images);

        Assert.Equal(8, selected.Count);
        Assert.Equal(Enumerable.Range(1, 8).Select(number => $"https://cdn.example.com/{number}.jpg"), selected);
    }
}
