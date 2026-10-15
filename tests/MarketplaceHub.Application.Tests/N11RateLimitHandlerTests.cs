using MarketplaceHub.Infrastructure.Adapters.N11;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11RateLimitHandlerTests
{
    [Theory]
    [InlineData("https://api.n11.com/rest/delivery/v1/shipmentPackages?orderNumber=1", "orders")]
    [InlineData("https://api.n11.com/rest/order/v1/update", "orders")]
    [InlineData("https://api.n11.com/ms/product/tasks/price-stock-update", "tasks")]
    [InlineData("https://api.n11.com/ms/product/task-details/page-query", "tasks")]
    [InlineData("https://api.n11.com/ms/product-query?page=0", "products")]
    [InlineData("https://api.n11.com/cdn/categories", "categories")]
    [InlineData("https://api.n11.com/ws/shipmentCompanyService/", "soap")]
    public void RequestsAreCountedInTheirN11Bucket(string url, string bucket)
    {
        Assert.Equal(bucket, N11RateLimitHandler.BucketFor(new Uri(url)));
    }

    [Fact]
    public void BucketLimitsComeFromOptions()
    {
        var options = new N11Options();

        Assert.Equal(600, N11RateLimitHandler.LimitPerMinute(options, "orders"));
        Assert.Equal(120, N11RateLimitHandler.LimitPerMinute(options, "products"));
        Assert.Equal(30, N11RateLimitHandler.LimitPerMinute(options, "categories"));
        Assert.Equal(60, N11RateLimitHandler.LimitPerMinute(options, "soap"));
    }
}
