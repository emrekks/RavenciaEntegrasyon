using System.Net.Http.Headers;

namespace MarketplaceHub.Infrastructure.Adapters.Http;

// The one reader of a Retry-After header (seconds or an HTTP date) for every adapter and circuit.
internal static class RetryAfterHeader
{
    // The wait the header asks for; a zero wait or a date in the past means no wait (null). Trendyol, both circuits and
    // Hepsiburada read it this way.
    internal static TimeSpan? Delay(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header?.Delta is { } delta) return delta > TimeSpan.Zero ? delta : null;
        if (header?.Date is { } date && date - now is var wait && wait > TimeSpan.Zero) return wait;
        return null;
    }

    // The header as n11, Pazarama, PTT AVM and Shopify have always read it: the seconds as given (Retry-After: 0 is a
    // zero wait), a date as its distance from now even when it lies in the past (a negative wait, which
    // JobExecutionResult bounds later). Kept apart from Delay until the owner decides to unify them (ADAPTOR-TY-HB-7).
    internal static TimeSpan? Unchecked(RetryConditionHeaderValue? header, DateTimeOffset now) =>
        header?.Delta ?? (header?.Date is { } date ? date - now : null);
}
