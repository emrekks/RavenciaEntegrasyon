namespace MarketplaceHub.Infrastructure.Adapters.Http;

// OWNER DECISIONS (ADAPTOR-TY-HB-7), kept as they are: the two circuit breakers treat a 429 that names no wait
// differently. Each difference is one switch here, so a later decision is a one-line change.
internal static class MarketplaceCircuitRules
{
    // Trendyol's circuit (TrendyolResilienceHandler) counts a 429 without Retry-After as a failure: five in a row open
    // the circuit for its break. False would make it learn nothing from such an answer, as the shared circuit does.
    internal const bool TrendyolCountsQuotaStopWithoutRetryAfter = true;

    // The shared circuit (MarketplaceCircuitBreakerHandler: Hepsiburada, n11, Pazarama, PTT AVM, Shopify) learns nothing
    // from a 429 without Retry-After (no failure counted, no break). True would count it as a failure, as Trendyol does.
    internal const bool SharedCircuitCountsQuotaStopWithoutRetryAfter = false;
}
