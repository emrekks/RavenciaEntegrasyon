using System.Net;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolResilienceHandlerTests
{
    [Fact]
    public void OrderRead_UsesSellerScopedRateLimitBucket()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-42/orders/stream?size=1");

        Assert.Equal("orders:seller-42", TrendyolResilienceHandler.RateBucketFor(request));
    }

    [Fact]
    public void ProductRead_DoesNotUseOrderRateLimitBucket()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/product/sellers/seller-42/products/approved");

        Assert.Null(TrendyolResilienceHandler.RateBucketFor(request));
    }

    [Fact]
    public async Task TooManyRequests_OpensCircuitAfterConfiguredFailures()
    {
        using var downstream = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var resilience = new TrendyolResilienceHandler(
            Options.Create(new TrendyolOptions
            {
                MaxConcurrency = 1,
                RequestsPerInterval = 100,
                RequestInterval = TimeSpan.FromMilliseconds(1),
                CircuitFailureThreshold = 2,
                CircuitBreakDuration = TimeSpan.FromMinutes(1)
            }),
            TimeProvider.System,
            new TrendyolResilienceState(Options.Create(new TrendyolOptions
            {
                MaxConcurrency = 1
            })))
        {
            InnerHandler = downstream
        };
        using var client = new HttpClient(resilience);

        using var first = await client.GetAsync("https://unit.test/first");
        using var second = await client.GetAsync("https://unit.test/second");
        using var third = await client.GetAsync("https://unit.test/third");

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
        Assert.Equal("{\"code\":\"LOCAL_CIRCUIT_OPEN\"}", await third.Content.ReadAsStringAsync());
        Assert.Equal(2, downstream.RequestCount);
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref requestCount);
            var status = statuses[Math.Min(requestNumber - 1, statuses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
