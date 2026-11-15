using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Http;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient
{
    private async Task<AdapterResult<string>> SendAsync(TrendyolRequestContext context, HttpMethod method, string endpoint, HttpContent? content, CancellationToken cancellationToken, bool includeStoreFrontCode = true, Action<HttpRequestMessage>? configure = null, string? storeFrontCode = "TR")
    {
        using var request = TrendyolAuthenticationHandler.Create(context, method, endpoint, content, includeStoreFrontCode, storeFrontCode); configure?.Invoke(request); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The request timeout measures the exchange with Trendyol only. Waiting for the seller quota
        // (order reads are spaced 5 s apart and shared by every job) used to count towards it, so
        // queued reads were cancelled as REMOTE_TIMEOUT before they were ever sent. The timer starts
        // when the resilience handler releases the request; a long cap still bounds the quota wait.
        var instantLimits = TrendyolQuotaLane.Limits;
        var timeout = instantLimits?.RequestTimeout ?? RequestTimeout(trendyolOptions.Value.Timeout);
        linked.CancelAfter(instantLimits is null ? timeout + MaximumQuotaWait : instantLimits.MaximumQuotaWait);
        var quotaAcquired = false;
        request.Options.Set(TrendyolResilienceHandler.QuotaAcquiredKey, () => { quotaAcquired = true; linked.CancelAfter(timeout); });
        CallerCancellation.Mark(request, cancellationToken);
        try
        {
            using var response = await clients.CreateClient("Trendyol").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token); var retryAfter = RetryAfterDelay(response.Headers.RetryAfter, timeProvider.GetUtcNow()); var rate = new RateLimitMetadata(null, response.Headers.RetryAfter?.Date, retryAfter); var remoteRequestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                // The panel's own open circuit refused it: never sent, so never an outcome-unknown write.
                if (LocalCircuitResponse.Refusal(response, "TRENDYOL", "Trendyol") is { } localCircuit)
                    return AdapterResult<string>.Failure(localCircuit, rate);
                var body = await response.Content.ReadBoundedStringAsync(linked.Token);
                if (TrendyolErrorMapper.RepeatRequest(response.StatusCode, body, remoteRequestId) is { } repeat)
                    return AdapterResult<string>.Failure(repeat, rate with { RetryAfter = TrendyolStockRules.RepeatRequestWait });
                if (MarketplaceErrorRules.GatewayBlocked(response.StatusCode, body, "Trendyol", "TRENDYOL", remoteRequestId) is { } gatewayBlock)
                    return AdapterResult<string>.Failure(gatewayBlock, rate with { RetryAfter = MarketplaceErrorRules.GatewayBlockWait });
                var vendorCode = TrendyolErrorMapper.SafeVendorCode(body);
                return AdapterResult<string>.Failure(TrendyolErrorMapper.FromStatus(response.StatusCode, retryAfter, remoteRequestId, vendorCode), rate);
            }
            return AdapterResult<string>.Success(await response.Content.ReadBoundedStringAsync(linked.Token), rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An instant action that never got a quota token was not sent; it is queued instead.
            if (instantLimits is not null && !quotaAcquired)
                return AdapterResult<string>.Failure(new(AdapterErrorClass.RateLimit, "LOCAL_QUOTA_BUSY", "Pazaryeri istek kotası şu an dolu.", 429, TimeSpan.FromSeconds(10), null));
            // Any other request whose quota wait ran out was never sent either (the token is taken right before the
            // send): a local quota wait that keeps the job's attempt, as on n11, not an outcome-unknown timeout.
            // A request that was sent and then timed out stays REMOTE_TIMEOUT, so a write is never taken as unsent.
            if (!quotaAcquired)
                return AdapterResult<string>.Failure(new(AdapterErrorClass.RateLimit, LocalQuotaWaitCode, "Trendyol istek kotası dolu olduğu için istek gönderilmedi; yeniden denenecek.", 429, TimeSpan.FromSeconds(15), null));
            logger.LogWarning("Trendyol {Method} {Path} (storefront {StoreFront}) timed out after the quota wait", method.Method, request.RequestUri?.AbsolutePath, includeStoreFrontCode ? storeFrontCode : "none");
            return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_TIMEOUT", "Platform isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null));
        }
        catch (HttpRequestException) { return AdapterResult<string>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_NETWORK_ERROR", "Platform ağına güvenli bağlantı kurulamadı.", null, TimeSpan.FromSeconds(5), null)); }
    }

    private static readonly TimeSpan MaximumQuotaWait = TimeSpan.FromMinutes(10);
    internal const string LocalQuotaWaitCode = "LOCAL_QUOTA_WAIT";

    // Retry-After as seconds or as an HTTP date; a date in the past means no wait.
    internal static TimeSpan? RetryAfterDelay(System.Net.Http.Headers.RetryConditionHeaderValue? header, DateTimeOffset now) => RetryAfterHeader.Delay(header, now);

    private static TimeSpan RequestTimeout(TimeSpan configured) => configured > TimeSpan.Zero
        ? TimeSpan.FromSeconds(Math.Clamp(configured.TotalSeconds, 5, 300))
        : TimeSpan.FromSeconds(30);
}
