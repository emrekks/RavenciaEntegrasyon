using System.Net;
using System.Net.Http.Headers;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
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

    [Fact]
    public void CircuitKey_SeparatesAccountsOnTheSameApiFamily()
    {
        using var first = WithBasic("https://hb.test/orders/merchantid/m-1", "merchant-1:key-1");
        using var second = WithBasic("https://hb.test/orders/merchantid/m-2", "merchant-2:key-2");
        using var firstAgain = WithBasic("https://hb.test/orders/merchantid/m-1/packages", "merchant-1:key-1");
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "https://hb.test/orders/merchantid/m-1");

        var firstKey = MarketplaceCircuitBreakerHandler.CircuitKeyFor(first);
        Assert.NotEqual(firstKey, MarketplaceCircuitBreakerHandler.CircuitKeyFor(second));
        Assert.Equal(firstKey, MarketplaceCircuitBreakerHandler.CircuitKeyFor(firstAgain));
        Assert.StartsWith("hb.test/orders/merchantid#", firstKey, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Headers.Authorization!.Parameter!, firstKey, StringComparison.Ordinal);
        // No account on the request: the family key as before.
        Assert.Equal("hb.test/orders/merchantid", MarketplaceCircuitBreakerHandler.CircuitKeyFor(anonymous));
    }

    [Fact]
    public void CircuitKey_UsesTheAccountTheRateLimiterUses()
    {
        using var n11First = new HttpRequestMessage(HttpMethod.Get, "https://n11.test/rest/delivery/v1/shipmentPackages");
        n11First.Headers.TryAddWithoutValidation("appkey", "app-key-1");
        using var n11Second = new HttpRequestMessage(HttpMethod.Get, "https://n11.test/rest/delivery/v1/shipmentPackages");
        n11Second.Headers.TryAddWithoutValidation("appkey", "app-key-2");
        using var n11Soap = new HttpRequestMessage(HttpMethod.Post, "https://n11.test/ws/productService/");
        n11Soap.Options.Set(N11RateLimitHandler.AccountKeyOption, "app-key-1");
        using var pazaramaFirst = WithBearer("https://pazarama.test/order/getOrdersForApi", "token-a", PazaramaRateLimitHandler.SellerKey("client-1"));
        using var pazaramaFirstNewToken = WithBearer("https://pazarama.test/order/getOrdersForApi", "token-b", PazaramaRateLimitHandler.SellerKey("client-1"));
        using var pazaramaSecond = WithBearer("https://pazarama.test/order/getOrdersForApi", "token-a", PazaramaRateLimitHandler.SellerKey("client-2"));
        using var bearerOnly = WithBearer("https://pazarama.test/order/getOrdersForApi", "token-c", null);

        Assert.NotEqual(MarketplaceCircuitBreakerHandler.CircuitKeyFor(n11First), MarketplaceCircuitBreakerHandler.CircuitKeyFor(n11Second));
        Assert.StartsWith("n11.test/ws/productService#", MarketplaceCircuitBreakerHandler.CircuitKeyFor(n11Soap), StringComparison.Ordinal);
        Assert.NotEqual(MarketplaceCircuitBreakerHandler.CircuitKeyFor(pazaramaFirst), MarketplaceCircuitBreakerHandler.CircuitKeyFor(pazaramaSecond));
        // A rotated bearer token keeps the seller's circuit, and a bearer token alone never names one.
        Assert.Equal(MarketplaceCircuitBreakerHandler.CircuitKeyFor(pazaramaFirst), MarketplaceCircuitBreakerHandler.CircuitKeyFor(pazaramaFirstNewToken));
        Assert.Equal("pazarama.test/order/getOrdersForApi", MarketplaceCircuitBreakerHandler.CircuitKeyFor(bearerOnly));
    }

    [Fact]
    public async Task OneMerchantsServerErrors_DoNotOpenTheCircuitForAnotherMerchant()
    {
        var downstream = new CountingHandler(HttpStatusCode.InternalServerError);
        using var invoker = new HttpMessageInvoker(new MarketplaceCircuitBreakerHandler(new MarketplaceCircuitBreakerState(), new StepClock()) { InnerHandler = downstream });

        for (var i = 0; i < MarketplaceCircuitBreakerHandler.FailureThreshold; i++)
        {
            using var failing = WithBasic("https://hb.test/orders/merchantid/m-1", "merchant-1:key-1");
            using var failed = await invoker.SendAsync(failing, CancellationToken.None);
        }
        using var blockedRequest = WithBasic("https://hb.test/orders/merchantid/m-1", "merchant-1:key-1");
        using var blocked = await invoker.SendAsync(blockedRequest, CancellationToken.None);
        downstream.Status = HttpStatusCode.OK;
        using var otherRequest = WithBasic("https://hb.test/orders/merchantid/m-2", "merchant-2:key-2");
        using var other = await invoker.SendAsync(otherRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(MarketplaceCircuitBreakerHandler.FailureThreshold + 1, downstream.Calls);
    }

    // A request timeout arrives as a cancelled token, like the caller's own cancellation: the adapter names the
    // caller's token, so only the timeout counts and a hanging platform opens the circuit.
    [Fact]
    public async Task RequestTimeouts_OpenTheCircuit_ButCallerCancellationDoesNot()
    {
        var downstream = new HangingHandler();
        using var invoker = new HttpMessageInvoker(new MarketplaceCircuitBreakerHandler(new MarketplaceCircuitBreakerState(), new StepClock()) { InnerHandler = downstream });

        for (var i = 0; i < MarketplaceCircuitBreakerHandler.FailureThreshold; i++)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SendCancelledAsync(invoker, callerCancelled: true));
        downstream.Answer = true;
        using var stillClosedRequest = new HttpRequestMessage(HttpMethod.Get, "https://hb.test/packages/merchantid/1");
        using var stillClosed = await invoker.SendAsync(stillClosedRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, stillClosed.StatusCode);
        downstream.Answer = false;

        for (var i = 0; i < MarketplaceCircuitBreakerHandler.FailureThreshold; i++)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SendCancelledAsync(invoker, callerCancelled: false));
        using var refusedRequest = new HttpRequestMessage(HttpMethod.Get, "https://hb.test/packages/merchantid/1");
        using var refused = await invoker.SendAsync(refusedRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(2 * MarketplaceCircuitBreakerHandler.FailureThreshold + 1, downstream.Calls);
    }

    // A timeout while the request still waits for its quota token says nothing about the marketplace.
    [Fact]
    public async Task ATimeoutWhileWaitingForTheQuota_IsNotACircuitFailure()
    {
        var limiter = new BlockingLimiter();
        var downstream = new CountingHandler(HttpStatusCode.OK);
        using var quota = new HepsiburadaRateLimitHandler(TimeProvider.System, limiter) { InnerHandler = downstream };
        using var invoker = new HttpMessageInvoker(new MarketplaceCircuitBreakerHandler(new MarketplaceCircuitBreakerState(), new StepClock()) { InnerHandler = quota });

        for (var i = 0; i < MarketplaceCircuitBreakerHandler.FailureThreshold + 1; i++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            using var waiting = QuotaRequest();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(waiting, timeout.Token));
        }
        limiter.Block = false;
        using var request = QuotaRequest();
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, downstream.Calls);
    }

    private static async Task SendCancelledAsync(HttpMessageInvoker invoker, bool callerCancelled)
    {
        using var caller = new CancellationTokenSource();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://hb.test/packages/merchantid/1");
        CallerCancellation.Mark(request, caller.Token);
        if (callerCancelled) caller.Cancel();
        else timeout.Cancel();
        using var response = await invoker.SendAsync(request, timeout.Token);
    }

    private static HttpRequestMessage QuotaRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://oms-external.example/packages/merchantid/m-1");
        request.Options.Set(HepsiburadaRateLimitHandler.QuotaAcquiredKey, () => { });
        CallerCancellation.Mark(request, CancellationToken.None);
        return request;
    }

    private static HttpRequestMessage WithBasic(string url, string credentials)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credentials)));
        return request;
    }

    private static HttpRequestMessage WithBearer(string url, string token, string? sellerKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (sellerKey is not null) request.Options.Set(PazaramaRateLimitHandler.AccountKeyOption, sellerKey);
        return request;
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        private int calls;
        public int Calls => calls;
        public bool Answer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            if (!Answer) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class BlockingLimiter : ISharedRateLimiter
    {
        public bool Block { get; set; } = true;

        public async ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken)
        {
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TimeSpan.Zero;
        }
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
