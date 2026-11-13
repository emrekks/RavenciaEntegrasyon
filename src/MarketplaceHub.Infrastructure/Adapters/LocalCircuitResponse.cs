using System.Net;
using System.Net.Http.Headers;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters;

// The answer the panel's circuit breakers (TrendyolResilienceHandler, MarketplaceCircuitBreakerHandler) give while a
// circuit is open after repeated faults. It carries a header no marketplace sends, so the adapter tells it from a real
// 503 and answers LocalCircuitOpen (RateLimit) instead of an uncertain Remote5xx: the request never left the server.
internal static class LocalCircuitResponse
{
    internal const string HeaderName = "X-MarketplaceHub-Local-Circuit";

    internal static HttpResponseMessage Create(HttpStatusCode status, TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"code\":\"LOCAL_CIRCUIT_OPEN\"}")
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter);
        // A circuit opened for a quota answers 429 and keeps its quota meaning; only the fault circuit is marked.
        if (status != HttpStatusCode.TooManyRequests) response.Headers.TryAddWithoutValidation(HeaderName, "open");
        return response;
    }

    internal static bool IsLocal(HttpResponseMessage response) => response.Headers.Contains(HeaderName);

    // The adapter error of a locally refused request, or null for any other answer.
    internal static AdapterError? Refusal(HttpResponseMessage response, string codePrefix, string marketplaceName)
    {
        if (!IsLocal(response)) return null;
        var wait = response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero ? delta : TimeSpan.FromSeconds(30);
        return new(AdapterErrorClass.RateLimit, LocalCircuitOpen.Code(codePrefix),
            $"{marketplaceName} isteği gönderilmedi: art arda gelen hatalardan sonra bu servis kısa süre durduruldu. Süre dolunca yeniden denenecek.",
            429, wait, null);
    }
}
