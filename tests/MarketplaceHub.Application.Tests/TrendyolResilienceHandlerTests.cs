using System.Net;
using MarketplaceHub.Infrastructure.Adapters;
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
    public void GlobalRateLimit_IsCountedPerEndpoint()
    {
        using var stock = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/integration/inventory/sellers/seller-1/products/price-and-inventory");
        using var orders = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-1/orders/stream");
        using var claims = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-1/claims");
        using var categories = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/product/categories/411/attributes");

        var buckets = new[] { stock, orders, claims, categories }.Select(TrendyolResilienceHandler.GlobalRateBucketFor).ToArray();
        Assert.Equal(4, buckets.Distinct().Count());
        Assert.EndsWith(":integration/product/categories", buckets[3]);
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

        using var first = await client.GetAsync("https://unit.test/integration/order/sellers/seller-42/orders/stream?nextCursor=a");
        var started = DateTimeOffset.UtcNow;
        using var second = await client.GetAsync("https://unit.test/integration/order/sellers/seller-42/orders/stream?nextCursor=b");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(55));
    }

    [Fact]
    public async Task QuotaAcquiredCallback_RunsAfterTheQuotaWaitAndBeforeSending()
    {
        var options = Options.Create(new TrendyolOptions
        {
            MaxConcurrency = 2,
            RequestsPerInterval = 100,
            RequestInterval = TimeSpan.FromMilliseconds(1),
            OrderRequestsPerInterval = 100,
            OrderRequestInterval = TimeSpan.FromMinutes(1),
            OrderRequestMinimumInterval = TimeSpan.FromMilliseconds(120)
        });
        using var downstream = new SequenceHandler(HttpStatusCode.OK, HttpStatusCode.OK);
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, new TrendyolResilienceState(options)) { InnerHandler = downstream };
        using var invoker = new HttpMessageInvoker(resilience);
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-42/orders/stream?nextCursor=a");
        using var second = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-42/orders/stream?nextCursor=b");
        var released = DateTimeOffset.MinValue;
        second.Options.Set(TrendyolResilienceHandler.QuotaAcquiredKey, () => released = DateTimeOffset.UtcNow);

        using var firstResponse = await invoker.SendAsync(first, CancellationToken.None);
        var queuedAt = DateTimeOffset.UtcNow;
        using var secondResponse = await invoker.SendAsync(second, CancellationToken.None);

        // The second read waited for the 120 ms spacing; the timeout clock only starts after that.
        Assert.True(released - queuedAt >= TimeSpan.FromMilliseconds(90));
    }

    [Fact]
    public void SharedWindowBucket_NeverExceedsTheSlidingWindowLimit()
    {
        var (capacity, perSecond) = SharedRateLimits.ForWindow(30, TimeSpan.FromMinutes(1));

        Assert.Equal(6, capacity);
        Assert.True(capacity + perSecond * 60 <= 30.000001);
        Assert.Equal((1d, 0.2d), SharedRateLimits.ForSpacing(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SharedLimiter_CountsGlobalOrderAndSpacingBucketsAcrossProcesses()
    {
        var options = Options.Create(new TrendyolOptions { MaxConcurrency = 1, OrderRequestMinimumInterval = TimeSpan.FromSeconds(5) });
        var shared = new RecordingSharedLimiter();
        using var downstream = new SequenceHandler(HttpStatusCode.OK);
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, new TrendyolResilienceState(options), shared) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using var response = await client.GetAsync("https://unit.test/integration/order/sellers/seller-42/orders/stream?nextCursor=a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["trendyol:global:unit.test:anonymous:order-reads", "trendyol:orders:unit.test:seller-42:fg", "trendyol:orders:unit.test:seller-42:spacing"], shared.Keys);
    }

    [Fact]
    public void OrderQuota_KeepsAForegroundShareAndSpacesOnlyThePagesOfOneStreamQuery()
    {
        var options = new TrendyolOptions { OrderRequestsPerInterval = 30, OrderForegroundShare = 0.4, OrderRequestMinimumInterval = TimeSpan.FromSeconds(5), OrderStreamStartMinimumInterval = TimeSpan.FromSeconds(1) };
        using var start = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-42/orders/stream?size=200");
        using var page = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-42/orders/stream?size=200&nextCursor=abc");
        using var package = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/integration/order/sellers/seller-42/v2/orders?shipmentPackageIds=1");

        var foregroundStart = TrendyolResilienceHandler.OrderQuotaFor(start, options, background: false)!;
        var backgroundPage = TrendyolResilienceHandler.OrderQuotaFor(page, options, background: true)!;
        var backgroundPackage = TrendyolResilienceHandler.OrderQuotaFor(package, options, background: true)!;

        Assert.Equal(("orders:unit.test:seller-42:fg", 12, TimeSpan.FromSeconds(1)), (foregroundStart.Bucket, foregroundStart.Limit, foregroundStart.Spacing));
        Assert.Equal(("orders:unit.test:seller-42:bg", 18, TimeSpan.FromSeconds(5)), (backgroundPage.Bucket, backgroundPage.Limit, backgroundPage.Spacing));
        Assert.Equal(TimeSpan.Zero, backgroundPackage.Spacing);
        Assert.NotEqual(foregroundStart.SpacingBucket, backgroundPage.SpacingBucket);
    }

    [Fact]
    public async Task BackgroundLane_DrawsFromTheBackgroundShare()
    {
        var options = Options.Create(new TrendyolOptions { MaxConcurrency = 1 });
        var shared = new RecordingSharedLimiter();
        using var downstream = new SequenceHandler(HttpStatusCode.OK);
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, new TrendyolResilienceState(options), shared) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using (TrendyolQuotaLane.Enter(isBackground: true))
        {
            using var response = await client.GetAsync("https://unit.test/integration/order/sellers/seller-42/v2/orders?shipmentPackageIds=1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(["trendyol:global:unit.test:anonymous:order-reads", "trendyol:orders:unit.test:seller-42:bg"], shared.Keys);
        Assert.False(TrendyolQuotaLane.IsBackground);
    }

    [Fact]
    public async Task PackageRead_SharesTheOrderQuotaButNotTheStreamSpacing()
    {
        var options = Options.Create(new TrendyolOptions { MaxConcurrency = 1, OrderRequestMinimumInterval = TimeSpan.FromSeconds(5) });
        var shared = new RecordingSharedLimiter();
        using var downstream = new SequenceHandler(HttpStatusCode.OK);
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, new TrendyolResilienceState(options), shared) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using var response = await client.GetAsync("https://unit.test/integration/order/sellers/seller-42/v2/orders?shipmentPackageIds=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["trendyol:global:unit.test:anonymous:order-reads", "trendyol:orders:unit.test:seller-42:fg"], shared.Keys);
    }

    [Fact]
    public async Task SharedLimiterFailure_FallsBackToTheInProcessLimiter()
    {
        var options = Options.Create(new TrendyolOptions { MaxConcurrency = 1, RequestsPerInterval = 100, RequestInterval = TimeSpan.FromMilliseconds(1) });
        using var downstream = new SequenceHandler(HttpStatusCode.OK);
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, new TrendyolResilienceState(options), new ThrowingSharedLimiter()) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using var response = await client.GetAsync("https://unit.test/integration/product/sellers/seller-42/products/approved");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class RecordingSharedLimiter : MarketplaceHub.Infrastructure.Adapters.ISharedRateLimiter
    {
        public List<string> Keys { get; } = [];
        public ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken)
        {
            Keys.Add(key);
            return ValueTask.FromResult(TimeSpan.Zero);
        }
    }

    private sealed class ThrowingSharedLimiter : MarketplaceHub.Infrastructure.Adapters.ISharedRateLimiter
    {
        public ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database unavailable");
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
    public async Task OrderQuota429_DoesNotStopTheSellersStockAndInvoiceRequests()
    {
        var options = Options.Create(new TrendyolOptions { MaxConcurrency = 1, RequestsPerInterval = 100, RequestInterval = TimeSpan.FromMilliseconds(1), OrderRequestsPerInterval = 100, OrderRequestInterval = TimeSpan.FromMilliseconds(1), OrderRequestMinimumInterval = TimeSpan.Zero, OrderStreamStartMinimumInterval = TimeSpan.Zero, CircuitFailureThreshold = 5 });
        var state = new TrendyolResilienceState(options);
        using var downstream = new RetryAfterHandler(TimeSpan.FromSeconds(30));
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, state) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using var orders = await client.GetAsync("https://unit.test/integration/order/sellers/seller-1/orders/stream");
        using var ordersAgain = await client.GetAsync("https://unit.test/integration/order/sellers/seller-1/v2/orders");
        using var stock = await client.PostAsync("https://unit.test/integration/inventory/sellers/seller-1/products/price-and-inventory", new StringContent("{}"));
        using var invoice = await client.PostAsync("https://unit.test/integration/sellers/seller-1/seller-invoice-links", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, orders.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ordersAgain.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, stock.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, invoice.StatusCode);
        Assert.Equal(3, downstream.RequestCount);
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
