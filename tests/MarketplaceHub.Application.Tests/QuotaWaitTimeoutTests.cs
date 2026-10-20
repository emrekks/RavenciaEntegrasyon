using System.Net;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Time spent waiting for the shared quota token is not counted towards the request timeout (as for
// Trendyol and Pazarama), so a call queued behind the quota is not lost as a timeout before it is sent.
public sealed class QuotaWaitTimeoutTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SlowQuota = TimeSpan.FromMilliseconds(900);

    [Fact]
    public async Task HepsiburadaRequestTimeoutStartsWhenTheQuotaTokenIsTaken()
    {
        using var remote = new Answering("""[{"packageNumber":"P-1","status":"Delivered"}]""", TimeSpan.Zero);
        var client = Hepsiburada(new SlowLimiter(SlowQuota), remote);

        var result = await client.GetPackageTrackingInfoAsync(HepsiburadaAccount(), "P-1", CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(1, remote.Requests);
    }

    [Fact]
    public async Task HepsiburadaSlowAnswerAfterTheQuotaIsStillATimeout()
    {
        using var remote = new Answering("[]", TimeSpan.FromSeconds(5));
        var client = Hepsiburada(new SlowLimiter(TimeSpan.Zero), remote);

        var result = await client.GetPackageTrackingInfoAsync(HepsiburadaAccount(), "P-1", CancellationToken.None);

        Assert.Equal("HEPSIBURADA_TIMEOUT", result.Error!.Code);
    }

    [Fact]
    public async Task N11RequestTimeoutStartsWhenTheQuotaTokenIsTaken()
    {
        using var remote = new Answering("""{"content":[]}""", TimeSpan.Zero);
        var options = Options.Create(new N11Options { Timeout = RequestTimeout });
        var handler = new N11RateLimitHandler(options, TimeProvider.System, new SlowLimiter(SlowQuota)) { InnerHandler = remote };
        var client = new N11HttpClient(new Factory(handler), null!, options, TimeProvider.System, NullLogger<N11HttpClient>.Instance, new ConfigurationBuilder().Build());
        var account = new N11RequestContext(new PlatformConnection { PlatformCode = "N11", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "N11", ExternalStoreId = "Ravencia", ApiVersion = "V1" }, "app-key", "app-secret");

        var result = await client.TestAsync(account, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(1, remote.Requests);
    }

    private static HepsiburadaHttpClient Hepsiburada(ISharedRateLimiter limiter, HttpMessageHandler remote)
    {
        var handler = new HepsiburadaRateLimitHandler(TimeProvider.System, limiter) { InnerHandler = remote };
        return new HepsiburadaHttpClient(new Factory(handler), null!, Options.Create(new HepsiburadaOptions { AuthenticationMode = "BASIC", Timeout = RequestTimeout }), TimeProvider.System, new ConfigurationBuilder().Build(), NullLogger<HepsiburadaHttpClient>.Instance);
    }

    private static HepsiburadaRequestContext HepsiburadaAccount() => new(
        new PlatformConnection { PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "fixture", ExternalStoreId = "merchant-1", Status = "ACTIVE", ApiVersion = "V1.0" },
        new Uri("https://oms-external.example/"), new Uri("https://listing-external.example/"), "integrator", "fixture-key")
    {
        IntegratorName = "ravencia_tests/1.0"
    };

    private sealed class SlowLimiter(TimeSpan wait) : ISharedRateLimiter
    {
        public async ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken)
        {
            await Task.Delay(wait, cancellationToken);
            return TimeSpan.Zero;
        }
    }

    private sealed class Answering(string body, TimeSpan delay) : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
