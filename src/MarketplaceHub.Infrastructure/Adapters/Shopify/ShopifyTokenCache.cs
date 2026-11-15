namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

// Client-credentials access tokens of the older Shopify connections. Shared by every request and job of the process (a
// singleton; the authentication handler itself is scoped) and keyed by the credential, so a rotated credential never
// reuses the old token. A token is fresh until a minute before it expires; concurrent callers of one credential wait for
// a single token request (ADAPTOR-DIGER-2); a failed request is not kept. The clock is injected (ADAPTOR-DIGER-2 step 4):
// both the expiry of a new token and the freshness check run on it.
public sealed class ShopifyTokenCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    private readonly SingleFlightTokenCache<(Guid ConnectionId, Guid CredentialId), CachedToken> tokens =
        new(timeProvider, (cached, now) => cached.ExpiresAt - RenewBefore > now);

    // issue answers Shopify's token and the lifetime it gave (expires_in); the expiry is counted from the moment the
    // answer arrived.
    internal Task<CachedToken?> GetAsync((Guid ConnectionId, Guid CredentialId) key, Func<CancellationToken, Task<IssuedToken?>> issue, CancellationToken cancellationToken) =>
        tokens.GetAsync(
            key,
            async token => await issue(token) is { } issued ? new CachedToken(issued.Token, timeProvider.GetUtcNow() + issued.Lifetime) : null,
            (cached, _) => cached,
            cached => (CachedToken?)cached,
            cancellationToken);

    // Drops the cached tokens of a connection after Shopify refused one (401).
    internal void Forget(Guid connectionId) => tokens.ForgetWhere(key => key.ConnectionId == connectionId);

    internal sealed record IssuedToken(string Token, TimeSpan Lifetime)
    {
        public override string ToString() => $"IssuedToken {{ Lifetime = {Lifetime} }}";
    }

    internal sealed record CachedToken(string Token, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => $"CachedToken {{ ExpiresAt = {ExpiresAt:O} }}";
    }
}
