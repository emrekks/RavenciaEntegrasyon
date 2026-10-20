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
