using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

// E-Faturam sign-in returns a JWT with an exp claim. One token per stored credential is kept in memory
// and renewed shortly before it expires, so every invoice operation no longer signs in with the account
// password; concurrent callers of the same credential wait for a single sign-in. A token without exp is
// not cached.
public sealed class TrendyolEFaturamTokenCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(2);
    private readonly SingleFlightTokenCache<Guid, TrendyolEFaturamAccessContext> tokens = new(timeProvider, Usable);

    public Task<AdapterResult<TrendyolEFaturamAccessContext>> GetAsync(
        Guid credentialId,
        Func<CancellationToken, Task<AdapterResult<TrendyolEFaturamAccessContext>>> signIn,
        CancellationToken cancellationToken) =>
        tokens.GetAsync(
            credentialId,
            signIn,
            (result, now) => result.IsSuccess && Usable(result.Value!, now) ? result.Value! : null,
            cached => AdapterResult<TrendyolEFaturamAccessContext>.Success(cached),
            cancellationToken);

    // Called when the invoice API answers 401 for a token (revoked, rotated or expired early).
    public void Forget(Guid credentialId) => tokens.Forget(credentialId);

    private static bool Usable(TrendyolEFaturamAccessContext access, DateTimeOffset now) =>
        access.ExpiresAt is { } expiresAt && expiresAt - RenewBefore > now
        && (access.NotBefore is not { } notBefore || notBefore <= now);
}
