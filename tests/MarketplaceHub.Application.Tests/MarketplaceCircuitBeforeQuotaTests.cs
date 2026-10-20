using System.Net;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A request refused by an open circuit must not spend (or wait for) a marketplace quota token.
public sealed class MarketplaceCircuitBeforeQuotaTests
{
    [Fact]
    public async Task TrendyolOpenCircuitAnswersBeforeTheQuotaIsTaken()
    {
        var options = Options.Create(new TrendyolOptions { MaxConcurrency = 1, RequestsPerInterval = 100, RequestInterval = TimeSpan.FromMilliseconds(1), CircuitFailureThreshold = 5 });
        var limiter = new CountingLimiter();
        using var downstream = new QuotaRefusal(TimeSpan.FromSeconds(30));
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, new TrendyolResilienceState(options), limiter) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using var first = await client.GetAsync("https://unit.test/integration/product/sellers/seller-1/products/approved");
        var tokensAfterFirst = limiter.Taken;
        using var second = await client.GetAsync("https://unit.test/integration/product/sellers/seller-1/products/approved");

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(tokensAfterFirst, limiter.Taken);
        Assert.Equal(1, downstream.Requests);
    }

    [Fact]
    public async Task TrendyolCircuitOpenedByFailuresStillAnswers503()
    {
        var options = Options.Create(new TrendyolOptions { MaxConcurrency = 1, RequestsPerInterval = 100, RequestInterval = TimeSpan.FromMilliseconds(1), CircuitFailureThreshold = 2 });
        using var downstream = new Failing();
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, new TrendyolResilienceState(options)) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);

        using var first = await client.GetAsync("https://unit.test/a");
        using var second = await client.GetAsync("https://unit.test/a");
        using var third = await client.GetAsync("https://unit.test/a");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
        Assert.Equal(2, downstream.Requests);
    }

    [Theory]
    [InlineData("Hepsiburada", typeof(HepsiburadaRateLimitHandler))]
    [InlineData("N11", typeof(N11RateLimitHandler))]
    [InlineData(PazaramaHttpClient.ClientName, typeof(PazaramaRateLimitHandler))]
    public void TheSharedCircuitBreakerSitsOutsideTheRateLimitHandler(string clientName, Type rateLimitHandler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AppDb"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["Security:CredentialKey"] = Convert.ToBase64String(new byte[32]),
            ["MARKETPLACEHUB_ENVIRONMENT"] = "PILOT_LOCAL",
            ["DataProtection:KeysRoot"] = Path.Combine(Path.GetTempPath(), "marketplacehub-handler-order-keys")
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMarketplaceInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        var chain = new List<Type>();
        HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (handler is DelegatingHandler delegating)
        {
            chain.Add(delegating.GetType());
            handler = delegating.InnerHandler;
        }

        var circuit = chain.IndexOf(typeof(MarketplaceCircuitBreakerHandler));
        var quota = chain.IndexOf(rateLimitHandler);
        Assert.True(circuit >= 0 && quota >= 0, string.Join(" > ", chain.Select(type => type.Name)));
        Assert.True(circuit < quota, string.Join(" > ", chain.Select(type => type.Name)));
    }

    [Fact]
    public async Task AnOpenSharedCircuitDoesNotTakeAHepsiburadaQuotaToken()
    {
        var limiter = new CountingLimiter();
        using var downstream = new QuotaRefusal(TimeSpan.FromSeconds(30));
        using var quota = new HepsiburadaRateLimitHandler(TimeProvider.System, limiter) { InnerHandler = downstream };
        using var circuit = new MarketplaceCircuitBreakerHandler(new MarketplaceCircuitBreakerState(), TimeProvider.System) { InnerHandler = quota };
        using var client = new HttpClient(circuit);

        using var first = await client.GetAsync("https://oms-external.example/packages/merchantid/m-1");
        using var second = await client.GetAsync("https://oms-external.example/packages/merchantid/m-1");

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(1, limiter.Taken);
        Assert.Equal(1, downstream.Requests);
    }

    // G11: the half-open probe is claimed only once the quota token is held, so a probe stuck in the quota queue
    // does not answer every other request of the family with 503.
    [Fact]
    public async Task ATrendyolProbeWaitingForItsQuotaDoesNotBlockTheOthers()
    {
        var options = Options.Create(new TrendyolOptions { MaxConcurrency = 4, RequestsPerInterval = 100, RequestInterval = TimeSpan.FromMilliseconds(1), CircuitFailureThreshold = 1, CircuitBreakDuration = TimeSpan.FromMilliseconds(1) });
        var limiter = new HoldingLimiter();
        using var downstream = new FailFirst(1);
        using var resilience = new TrendyolResilienceHandler(options, TimeProvider.System, new TrendyolResilienceState(options), limiter) { InnerHandler = downstream };
        using var client = new HttpClient(resilience);
        using var opened = await client.GetAsync("https://unit.test/a");
        Assert.Equal(HttpStatusCode.InternalServerError, opened.StatusCode);
        await Task.Delay(50);

        var hold = limiter.HoldNext();
        var waiting = client.GetAsync("https://unit.test/a");
        await limiter.Held.Task;
        using var other = await client.GetAsync("https://unit.test/a");
        hold.SetResult();
        using var released = await waiting;

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(3, downstream.Requests);
    }

    [Fact]
    public async Task ASharedCircuitProbeWaitingForItsQuotaDoesNotBlockTheOthers()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var limiter = new HoldingLimiter();
        using var downstream = new FailFirst(MarketplaceCircuitBreakerHandler.FailureThreshold);
        using var quota = new HepsiburadaRateLimitHandler(clock, limiter) { InnerHandler = downstream };
        using var circuit = new MarketplaceCircuitBreakerHandler(new MarketplaceCircuitBreakerState(), clock) { InnerHandler = quota };
        using var client = new HttpClient(circuit);
        for (var i = 0; i < MarketplaceCircuitBreakerHandler.FailureThreshold; i++)
            (await client.SendAsync(QuotaRequest())).Dispose();
        using var refused = await client.SendAsync(QuotaRequest());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        clock.Advance(MarketplaceCircuitBreakerHandler.BreakDuration + TimeSpan.FromSeconds(1));

        var hold = limiter.HoldNext();
        var waiting = client.SendAsync(QuotaRequest());
        await limiter.Held.Task;
        using var other = await client.SendAsync(QuotaRequest());
        hold.SetResult();
        using var released = await waiting;

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(MarketplaceCircuitBreakerHandler.FailureThreshold + 2, downstream.Requests);
    }

    [Fact]
    public async Task ASharedCircuitProbeClaimedByAnotherRequestIsRefusedAtTheGateWithoutBeingSent()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var limiter = new HoldingLimiter();
        using var downstream = new FailFirst(MarketplaceCircuitBreakerHandler.FailureThreshold + 1);
        using var quota = new HepsiburadaRateLimitHandler(clock, limiter) { InnerHandler = downstream };
        using var circuit = new MarketplaceCircuitBreakerHandler(new MarketplaceCircuitBreakerState(), clock) { InnerHandler = quota };
        using var client = new HttpClient(circuit);
        for (var i = 0; i < MarketplaceCircuitBreakerHandler.FailureThreshold; i++)
            (await client.SendAsync(QuotaRequest())).Dispose();
        clock.Advance(MarketplaceCircuitBreakerHandler.BreakDuration + TimeSpan.FromSeconds(1));

        var hold = limiter.HoldNext();
        var waiting = client.SendAsync(QuotaRequest());
        await limiter.Held.Task;
        using var failedProbe = await client.SendAsync(QuotaRequest());
        hold.SetResult();
        using var refusedAtGate = await waiting;

        Assert.Equal(HttpStatusCode.InternalServerError, failedProbe.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refusedAtGate.StatusCode);
        Assert.Equal(MarketplaceCircuitBreakerHandler.FailureThreshold + 1, downstream.Requests);
    }

    private static HttpRequestMessage QuotaRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://oms-external.example/packages/merchantid/m-1");
        request.Options.Set(HepsiburadaRateLimitHandler.QuotaAcquiredKey, () => { });
        return request;
    }

    private sealed class HoldingLimiter : ISharedRateLimiter
    {
        private TaskCompletionSource? hold;
        public TaskCompletionSource Held { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HoldNext()
        {
            Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hold = next;
            return next;
        }

        public async ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref hold, null) is { } waitFor)
            {
                Held.TrySetResult();
                await waitFor.Task.WaitAsync(cancellationToken);
            }
            return TimeSpan.Zero;
        }
    }

    private sealed class FailFirst(int failures) : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Interlocked.Increment(ref requests) <= failures ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class CountingLimiter : ISharedRateLimiter
    {
        private int taken;
        public int Taken => taken;

        public ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref taken);
            return ValueTask.FromResult(TimeSpan.Zero);
        }
    }

    private sealed class QuotaRefusal(TimeSpan retryAfter) : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            return Task.FromResult(response);
        }
    }

    private sealed class Failing : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
