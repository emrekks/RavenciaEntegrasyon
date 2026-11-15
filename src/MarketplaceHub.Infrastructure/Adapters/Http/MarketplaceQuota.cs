namespace MarketplaceHub.Infrastructure.Adapters.Http;

// The one request option every marketplace rate-limit handler calls once it has handed out the quota token
// (QuotaAwareTimeout starts the request timeout there; the shared circuit defers its half-open probe to it). Each
// handler's QuotaAcquiredKey is this key, so the circuit no longer needs to know every marketplace by name.
internal static class MarketplaceQuota
{
    internal static readonly HttpRequestOptionsKey<Action> AcquiredKey = new("MarketplaceHub.Quota.Acquired");
}
