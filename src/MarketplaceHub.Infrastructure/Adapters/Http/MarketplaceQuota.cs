namespace MarketplaceHub.Infrastructure.Adapters.Http;

// The request options every marketplace rate-limit handler shares, so the circuit no longer needs to know every
// marketplace by name (ADAPTOR-TY-HB-7 step 4).
internal static class MarketplaceQuota
{
    // Called once the handler has handed out the quota token (QuotaAwareTimeout starts the request timeout there; the
    // shared circuit defers its half-open probe to it). Each handler's QuotaAcquiredKey is this key.
    internal static readonly HttpRequestOptionsKey<Action> AcquiredKey = new("MarketplaceHub.Quota.Acquired");

    // The account a request is sent for, as its rate-limit handler buckets it (Pazarama's seller key, PTT AVM's store
    // key, n11's SOAP app key). Each handler's AccountKeyOption is this key; the shared circuit keys its account by it.
    internal static readonly HttpRequestOptionsKey<string> AccountKey = new("MarketplaceHub.Quota.Account");
}
