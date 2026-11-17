using System.Net;
using MarketplaceHub.Infrastructure.Adapters.Http;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Jobs;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ADAPTOR-DIGER-5: Shopify sends through the shared send step in its no-quota mode. Its client's own timeout stays the
// request timeout (no five-minute quota wait is added), a timeout is SHOPIFY_TIMEOUT with no wait as before, the request
// carries no quota option (the shared circuit would otherwise defer its probe to a quota token that never comes), and a
// worker shutdown stops only a request that has not started.
public sealed class ShopifyTransportTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheClientTimeoutEndsTheRequestAsATimeoutWithoutAQuotaWait()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://shop.myshopify.com/admin/api/2025-07/graphql.json");
        var started = DateTimeOffset.UtcNow;

        using var exchange = await ShopifyHttpClient.Transport.SendAsync(new Factory(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), TimeSpan.FromMilliseconds(200)), request, Timeout.InfiniteTimeSpan, CancellationToken.None).WaitAsync(Patience);

        Assert.True(DateTimeOffset.UtcNow - started < Patience);
        Assert.Equal("SHOPIFY_TIMEOUT", exchange.Error!.Code);
        Assert.Equal(AdapterErrorClass.TransientNetwork, exchange.Error.Class);
        Assert.Null(exchange.Error.RetryAfter);
    }

    [Fact]
    public async Task ANetworkErrorKeepsItsCodeAndNoWait()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://shop.myshopify.com/x");
        using var exchange = await ShopifyHttpClient.Transport.SendAsync(new Factory(new Handler((_, _) =>
            throw new HttpRequestException("refused"))), request, Timeout.InfiniteTimeSpan, CancellationToken.None);

        Assert.Equal("SHOPIFY_NETWORK_ERROR", exchange.Error!.Code);
        Assert.Null(exchange.Error.RetryAfter);
        Assert.NotNull(exchange.Exception);
    }

    [Fact]
    public async Task TheRequestCarriesNoQuotaOptionAndItsBodyIsRead()
    {
        var quotaOption = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://shop.myshopify.com/x");
        using var exchange = await ShopifyHttpClient.Transport.SendAsync(new Factory(new Handler((sent, _) =>
        {
            quotaOption = sent.Options.TryGetValue(MarketplaceQuota.AcquiredKey, out Action? _);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{}}") });
        })), request, Timeout.InfiniteTimeSpan, CancellationToken.None);

        Assert.False(quotaOption);
        Assert.Null(exchange.Error);
        Assert.Equal("{\"data\":{}}", exchange.Body);
    }

    [Fact]
    public async Task TheCallersOwnCancellationStillEscapes()
    {
        using var caller = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://shop.myshopify.com/x");
        var send = ShopifyHttpClient.Transport.SendAsync(new Factory(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })), request, Timeout.InfiniteTimeSpan, caller.Token);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(Patience));
    }

    [Fact]
    public async Task ARequestAWriteJobStartsAfterTheShutdownIsNotSent()
    {
        var sent = 0;
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        using var scope = JobShutdownSignal.Enter(stopping.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://shop.myshopify.com/x");

        using var exchange = await ShopifyHttpClient.Transport.SendAsync(new Factory(new Handler((_, _) =>
        {
            Interlocked.Increment(ref sent);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        })), request, Timeout.InfiniteTimeSpan, CancellationToken.None);

        Assert.Equal(0, sent);
        Assert.Equal("SHOPIFY_LOCAL_QUOTA_WAIT", exchange.Error!.Code);
        Assert.Equal(1, scope.QuotaWaitsStopped);
    }

    [Fact]
    public async Task ARequestUnderWayFinishesDespiteTheShutdown()
    {
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledInFlight = false;
        using var stopping = new CancellationTokenSource();
        using var scope = JobShutdownSignal.Enter(stopping.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://shop.myshopify.com/x");

        var send = ShopifyHttpClient.Transport.SendAsync(new Factory(new Handler(async (_, token) =>
        {
            inFlight.TrySetResult();
            await answer.Task;
            cancelledInFlight = token.IsCancellationRequested;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        })), request, Timeout.InfiniteTimeSpan, CancellationToken.None);
        await inFlight.Task.WaitAsync(Patience);
        stopping.Cancel();
        answer.SetResult();
        using var exchange = await send.WaitAsync(Patience);

        Assert.False(cancelledInFlight);
        Assert.Null(exchange.Error);
        Assert.Equal(0, scope.QuotaWaitsStopped);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request, cancellationToken);
    }

    // Like the registered "Shopify" client: its own HttpClient.Timeout (45 s there) is the request timeout.
    private sealed class Factory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(45) };
    }
}
