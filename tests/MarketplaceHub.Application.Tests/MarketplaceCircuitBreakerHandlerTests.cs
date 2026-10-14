using System.Net;
using MarketplaceHub.Infrastructure.Adapters;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceCircuitBreakerHandlerTests
{
    [Fact]
    public async Task FiveConsecutiveServerErrors_OpenTheCircuitForThatApiFamilyOnly()
    {
        var clock = new StepClock();
        var downstream = new CountingHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(new MarketplaceCircuitBreakerHandler(new MarketplaceCircuitBreakerState(), clock) { InnerHandler = downstream });

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("https://hb.test/packages/merchantid/1")).StatusCode);
        var blocked = await client.GetAsync("https://hb.test/packages/merchantid/2");
        var otherFamily = await client.GetAsync("https://hb.test/orders/merchantid/3");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal(6, downstream.Calls);
        Assert.Equal(HttpStatusCode.InternalServerError, otherFamily.StatusCode);

        // After the break a single probe goes through; success closes the circuit.
        clock.Advance(TimeSpan.FromSeconds(31));
        downstream.Status = HttpStatusCode.OK;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://hb.test/packages/merchantid/4")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://hb.test/packages/merchantid/5")).StatusCode);
    }

    [Fact]
    public async Task QuotaRejection_IsAnsweredLocallyAs429UntilRetryAfter()
    {
        var clock = new StepClock();
        var downstream = new CountingHandler(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(20));
        using var client = new HttpClient(new MarketplaceCircuitBreakerHandler(new MarketplaceCircuitBreakerState(), clock) { InnerHandler = downstream });

        await client.GetAsync("https://shop.test/admin/api/graphql.json");
        var local = await client.GetAsync("https://shop.test/admin/api/graphql.json");

        Assert.Equal(1, downstream.Calls);
        Assert.Equal(HttpStatusCode.TooManyRequests, local.StatusCode);
        Assert.True(local.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(19));
    }

    [Fact]
    public void CircuitKey_UsesHostAndFirstTwoPathSegments()
    {
        Assert.Equal("hb.test/packages/merchantid", MarketplaceCircuitBreakerHandler.CircuitKeyFor(new Uri("https://hb.test/packages/merchantid/123/invoice")));
        Assert.Equal("hb.test", MarketplaceCircuitBreakerHandler.CircuitKeyFor(new Uri("https://hb.test/")));
    }

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
        public void Advance(TimeSpan delta) => now = now.Add(delta);
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingHandler(HttpStatusCode status, TimeSpan? retryAfter = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; set; } = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(Status);
            if (retryAfter is { } wait) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            return Task.FromResult(response);
        }
    }
}
