using System.Net;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Http;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceTransportTests
{
    public static TheoryData<string> Transports => ["N11", "PAZARAMA", "PTTAVM", "HEPSIBURADA"];

    private static MarketplaceTransport For(string prefix) => prefix switch
    {
        "N11" => N11HttpClient.Transport,
        "PAZARAMA" => PazaramaHttpClient.Transport,
        "PTTAVM" => PttAvmHttpClient.Transport,
        _ => HepsiburadaHttpClient.Transport
    };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task AnAnswerComesBackWithItsWholeBody(string prefix)
    {
        var transport = For(prefix);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/x");
        using var exchange = await transport.SendAsync(new Factory(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"a\":1}") }))), request, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(exchange.Error);
        Assert.Equal(HttpStatusCode.BadRequest, exchange.Response!.StatusCode);
        Assert.Equal("{\"a\":1}", exchange.Body);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ANetworkFailureIsATransientNetworkError(string prefix)
    {
        var transport = For(prefix);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/x");
        using var exchange = await transport.SendAsync(new Factory(new Handler((_, _) => throw new HttpRequestException("reset"))), request, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal($"{prefix}_NETWORK_ERROR", exchange.Error!.Code);
        Assert.Equal(AdapterErrorClass.TransientNetwork, exchange.Error.Class);
        Assert.Equal(TimeSpan.FromSeconds(15), exchange.Error.RetryAfter);
        Assert.NotNull(exchange.Exception);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ATimeoutAfterTheQuotaTokenIsATimeoutAndBeforeItALocalQuotaWait(string prefix)
    {
        var transport = For(prefix);
        // The quota token was taken, then the answer took longer than the request timeout: outcome unknown.
        using (var sent = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/x"))
        {
            using var exchange = await transport.SendAsync(new Factory(new Handler(async (request, token) =>
            {
                request.Options.TryGetValue(transport.QuotaAcquiredKey, out var acquired);
                acquired!();
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })), sent, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Assert.Equal($"{prefix}_TIMEOUT", exchange.Error!.Code);
            Assert.Equal(AdapterErrorClass.TransientNetwork, exchange.Error.Class);
        }
        // The caller's own cancellation escapes as before.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var callerRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/x");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(new Factory(new Handler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })), callerRequest, TimeSpan.FromSeconds(5), cancelled.Token));
        Assert.Equal($"{prefix}_LOCAL_QUOTA_WAIT", transport.LocalQuotaWait().Code);
        Assert.Equal(AdapterErrorClass.RateLimit, transport.LocalQuotaWait().Class);
        Assert.Equal(429, transport.LocalQuotaWait().HttpStatus);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request, cancellationToken);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
