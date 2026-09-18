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

        Assert.Equal("orders:unit.test:seller-42", TrendyolResilienceHandler.RateBucketFor(request));
    }

    [Fact]
    public void ProductRead_DoesNotUseOrderRateLimitBucket()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/product/sellers/seller-42/products/approved");

        Assert.Null(TrendyolResilienceHandler.RateBucketFor(request));
    }

    [Fact]
    public void GlobalRateLimit_IsolatedByMarketplaceClientIdentity()
    {
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/product/sellers/seller-1/products/approved");
        using var sameClient = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/product/sellers/seller-2/products/approved");
        using var second = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/product/sellers/seller-2/products/approved");
        first.Headers.TryAddWithoutValidation("User-Agent", "seller-1 - Integrator");
        sameClient.Headers.TryAddWithoutValidation("User-Agent", "seller-1 - Integrator");
        second.Headers.TryAddWithoutValidation("User-Agent", "seller-2 - Integrator");

        Assert.Equal(TrendyolResilienceHandler.GlobalRateBucketFor(first), TrendyolResilienceHandler.GlobalRateBucketFor(sameClient));
        Assert.NotEqual(TrendyolResilienceHandler.GlobalRateBucketFor(first), TrendyolResilienceHandler.GlobalRateBucketFor(second));
    }

    [Fact]
    public async Task OrderStream_RespectsMinimumSpacing()
    {
        var options = Options.Create(new TrendyolOptions
        {
            MaxConcurrency = 2,
            RequestsPerInterval = 100,
            RequestInterval = TimeSpan.FromMilliseconds(1),
            OrderRequestsPerInterval = 100,
            OrderRequestInterval = TimeSpan.FromMinutes(1),
            OrderRequestMinimumInterval = TimeSpan.FromMilliseconds(80)
        });
        var state = new TrendyolResilienceState(options);
        using var downstream = new SequenceHandler(HttpStatusCode.OK, HttpStatusCode.OK);
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, state) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using var first = await client.GetAsync("https://unit.test/integration/order/sellers/seller-42/orders/stream");
        var started = DateTimeOffset.UtcNow;
        using var second = await client.GetAsync("https://unit.test/integration/order/sellers/seller-42/orders/stream");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(55));
    }

    [Fact]
    public async Task TooManyRequests_WithRetryAfter_OpensCircuitForRemoteDelay()
    {
        var options = Options.Create(new TrendyolOptions { MaxConcurrency = 1, RequestsPerInterval = 100, RequestInterval = TimeSpan.FromMilliseconds(1), CircuitFailureThreshold = 5 });
        var state = new TrendyolResilienceState(options);
        using var downstream = new RetryAfterHandler(TimeSpan.FromSeconds(30));
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, state) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using var first = await client.GetAsync("https://unit.test/first");
        using var second = await client.GetAsync("https://unit.test/second");

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.True(second.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(29));
        Assert.Equal(1, downstream.RequestCount);
    }

    [Fact]
    public void Circuit_IsolatedBySeller()
    {
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-1/orders/stream");
        using var second = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-2/orders/stream");

        Assert.NotEqual(TrendyolResilienceHandler.CircuitKeyFor(first), TrendyolResilienceHandler.CircuitKeyFor(second));
    }

    [Fact]
    public async Task IdleRateAndCircuitState_IsBounded_WhenManySellerKeysWereSeen()
    {
        var options = Options.Create(new TrendyolOptions
        {
            MaxConcurrency = 4,
            RequestsPerInterval = 100,
            RequestInterval = TimeSpan.FromMilliseconds(1),
            OrderRequestsPerInterval = 100,
            OrderRequestInterval = TimeSpan.FromMilliseconds(1)
        });
        var state = new TrendyolResilienceState(options);
        var old = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1));
        lock (state.SyncRoot)
        {
            for (var index = 0; index < TrendyolResilienceState.MaxTrackedStateEntries + 100; index++)
            {
                var rateKey = $"orders:stale-{index}";
                state.RequestStarts[rateKey] = new Queue<DateTimeOffset>();
                state.RateBucketLastTouched[rateKey] = old;
                state.Circuits[$"unit.test:seller:stale-{index}"] = new TrendyolCircuitState { LastTouchedAt = old };
            }
        }

        using var downstream = new SequenceHandler(HttpStatusCode.OK);
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, state) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);
        using var response = await client.GetAsync("https://unit.test/integration/order/sellers/fresh/orders/stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(state.RequestStarts.Count, 0, TrendyolResilienceState.MaxTrackedStateEntries + 1);
        Assert.InRange(state.Circuits.Count, 0, TrendyolResilienceState.MaxTrackedStateEntries + 1);
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

    [Fact]
    public async Task CallerCancellationDuringHalfOpenAttempt_ReleasesHalfOpenOwnership()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-09T10:00:00Z"));
        var options = Options.Create(new TrendyolOptions
        {
            MaxConcurrency = 1,
            RequestsPerInterval = 100,
            RequestInterval = TimeSpan.FromMilliseconds(1),
            CircuitFailureThreshold = 1,
            CircuitBreakDuration = TimeSpan.FromMinutes(1)
        });
        var state = new TrendyolResilienceState(options);
        using var downstream = new BlockingHandler();
        using var resilience = new TrendyolResilienceHandler(options, clock, state) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using var first = await client.GetAsync("https://unit.test/first");
        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);

        clock.Advance(TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var halfOpen = client.GetAsync("https://unit.test/half-open", cancellation.Token);
        await downstream.WaitUntilEntered;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => halfOpen);

        using var recovered = await client.GetAsync("https://unit.test/recovered");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(3, downstream.RequestCount);
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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requestCount;

        public int RequestCount => requestCount;
        public Task WaitUntilEntered => entered.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref requestCount);
            if (requestNumber == 1) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (requestNumber == 2)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RetryAfterHandler(TimeSpan retryAfter) : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            return Task.FromResult(response);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset utcNow = initial;

        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
    }
}
