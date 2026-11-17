using System.Net.Http.Headers;

namespace MarketplaceHub.Infrastructure.Adapters.Http;

// The one reader of a Retry-After header (seconds or an HTTP date) for every adapter and circuit.
// ADAPTOR-TY-HB-7 (owner decision, 16 Nov 2026): every adapter reads it this way; n11, Pazarama, PTT AVM and Shopify no
// longer read a zero or a past date as a zero or negative wait.
internal static class RetryAfterHeader
{
    // The wait the header asks for; a zero wait or a date in the past means no wait named (null), so the adapter's own
    // default wait for that status applies.
    internal static TimeSpan? Delay(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header?.Delta is { } delta) return delta > TimeSpan.Zero ? delta : null;
        if (header?.Date is { } date && date - now is var wait && wait > TimeSpan.Zero) return wait;
        return null;
    }
}
