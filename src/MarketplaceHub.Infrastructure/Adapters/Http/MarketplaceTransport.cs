using System.Net.Http;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Http;

// The send step the quota-paced adapters (Hepsiburada, n11 REST and SOAP, Pazarama, PTT AVM) share: the request goes
// out on the marketplace's bounded client (16 MB buffer) under a QuotaAwareTimeout (the request timeout starts once the
// rate-limit handler hands out the quota token; the wait for it is capped at five minutes), the body is read whole,
// and a transport failure is classed the same way for each of them:
//   cancelled before the quota token          -> {PREFIX}_LOCAL_QUOTA_WAIT (RateLimit, 15 s): never sent (also when a
//                                                worker shutdown ended a write job's wait, KUYRUK-1);
//   cancelled after it (the request timed out) -> {PREFIX}_TIMEOUT (TransientNetwork, 15 s): outcome unknown;
//   HttpRequestException                       -> {PREFIX}_NETWORK_ERROR (TransientNetwork, 15 s).
// The caller's own cancellation still escapes as OperationCanceledException. What an answer means (status, body,
// local circuit, gateway page) stays with each adapter's classifier.
//
// ADAPTOR-DIGER-5: Shopify sends through it too, in its "no quota" mode (WaitsForQuota false; it has no rate-limit
// handler): the token counts as taken at once (QuotaAwareTimeout.WithoutQuota), so no five-minute quota wait is added and
// a cancellation is a timeout as before; its client's own 45 s HttpClient.Timeout stays the request timeout (requestTimeout
// Timeout.InfiniteTimeSpan) and its timeout and network errors keep carrying no wait (TransportErrorWait null).
// Trendyol (its own resilience handler, a ten-minute quota cap, ResponseHeadersRead) and E-Faturam (the invoice provider:
// a linked timeout, ResponseHeadersRead, its own error texts and 5 s waits, and no shutdown stop in front of an invoice
// submit) keep their own send step.
internal sealed record MarketplaceTransport(string ClientName, string CodePrefix, string DisplayName, HttpRequestOptionsKey<Action> QuotaAcquiredKey)
{
    internal static readonly TimeSpan TransportRetryAfter = TimeSpan.FromSeconds(15);

    // False for a client without a rate-limit handler: see QuotaAwareTimeout.WithoutQuota.
    internal bool WaitsForQuota { get; init; } = true;

    // The wait a timeout or network error carries.
    internal TimeSpan? TransportErrorWait { get; init; } = TransportRetryAfter;

    internal AdapterError LocalQuotaWait() =>
        new(AdapterErrorClass.RateLimit, $"{CodePrefix}_LOCAL_QUOTA_WAIT", $"{DisplayName} istek kotası dolu olduğu için istek gönderilmedi; yeniden denenecek.", 429, TransportRetryAfter, null);

    internal AdapterError Timeout() =>
        new(AdapterErrorClass.TransientNetwork, $"{CodePrefix}_TIMEOUT", $"{DisplayName} isteği zaman aşımına uğradı.", null, TransportErrorWait, null);

    internal AdapterError NetworkError() =>
        new(AdapterErrorClass.TransientNetwork, $"{CodePrefix}_NETWORK_ERROR", $"{DisplayName} bağlantısı geçici olarak kurulamadı.", null, TransportErrorWait, null);

    internal async Task<MarketplaceExchange> SendAsync(IHttpClientFactory clients, HttpRequestMessage request, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        using var timeout = WaitsForQuota
            ? new QuotaAwareTimeout(request, QuotaAcquiredKey, requestTimeout, cancellationToken)
            : QuotaAwareTimeout.WithoutQuota(request, requestTimeout, cancellationToken);
        // KUYRUK-1: the worker is shutting down and this write job's request has not started: it is not sent.
        if (timeout.StoppedBeforeStart) return new(null, "", LocalQuotaWait(), null);
        HttpResponseMessage? response = null;
        try
        {
            response = await clients.CreateBoundedClient(ClientName).SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return new(response, body, null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            // Never sent when the quota stayed full: not an "outcome unknown" call.
            return new(null, "", timeout.QuotaAcquired ? Timeout() : LocalQuotaWait(), null);
        }
        catch (HttpRequestException exception)
        {
            response?.Dispose();
            return new(null, "", NetworkError(), exception);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }
}

// One exchange: the answer and its whole body, or the transport error (with the network exception for the log).
internal sealed class MarketplaceExchange(HttpResponseMessage? response, string body, AdapterError? error, HttpRequestException? exception) : IDisposable
{
    public HttpResponseMessage? Response { get; } = response;
    public string Body { get; } = body;
    public AdapterError? Error { get; } = error;
    public HttpRequestException? Exception { get; } = exception;

    public void Dispose() => Response?.Dispose();
}
