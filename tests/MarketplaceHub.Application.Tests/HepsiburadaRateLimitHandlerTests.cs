using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaRateLimitHandlerTests
{
    [Theory]
    [InlineData("https://oms-external.hepsiburada.com/orders/merchantid/1", "oms", 600)]
    [InlineData("https://mpop.hepsiburada.com/product/api/products", "catalog", 180)]
    [InlineData("https://listing-external.hepsiburada.com/listings/merchantid/1", "listing", 100)]
    public void EachServiceHasItsOwnSharedBucket(string url, string bucket, int limit) =>
        Assert.Equal((bucket, limit), HepsiburadaRateLimitHandler.BucketFor(new Uri(url)));

    [Fact]
    public void QuestionServiceIsNotThrottledHere() =>
        Assert.Null(HepsiburadaRateLimitHandler.BucketFor(new Uri("https://api-asktoseller-merchant.hepsiburada.com/api/v1.0/issues")));
}
