using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceSalesImageUrlTests
{
    [Theory]
    [InlineData("https://productimages.hepsiburada.net/s/777/{size}/110001632331020.jpg", "https://productimages.hepsiburada.net/s/777/500/110001632331020.jpg")]
    [InlineData("https://productimages.hepsiburada.net/s/777/{SIZE}/110001632331020.jpg", "https://productimages.hepsiburada.net/s/777/500/110001632331020.jpg")]
    [InlineData("https://cdn.example.test/image.jpg", "https://cdn.example.test/image.jpg")]
    public void NormalizeImageUrlResolvesHepsiburadaSizeToken(string source, string expected)
    {
        Assert.Equal(expected, MarketplaceSalesService.NormalizeImageUrl(source));
    }

    [Fact]
    public void NormalizeImageUrlRejectsNonHttpsImages()
    {
        Assert.Null(MarketplaceSalesService.NormalizeImageUrl("http://productimages.hepsiburada.net/s/777/{size}/image.jpg"));
    }
}
