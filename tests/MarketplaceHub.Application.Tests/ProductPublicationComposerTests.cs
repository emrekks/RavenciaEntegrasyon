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
}
