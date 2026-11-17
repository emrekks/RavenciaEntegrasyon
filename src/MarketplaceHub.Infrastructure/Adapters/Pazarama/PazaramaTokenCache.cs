namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Pazarama access tokens live one hour. One token per stored credential is kept in memory
// and renewed five minutes before it expires; concurrent callers of the same credential
// wait for a single token request.
public sealed class PazaramaTokenCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);
    private readonly SingleFlightTokenCache<Guid, CachedToken> tokens = new(timeProvider, (cached, now) => cached.ExpiresAt - RenewBefore > now);

    public Task<PazaramaToken> GetAsync(Guid credentialId, Func<CancellationToken, Task<PazaramaToken>> issue, CancellationToken cancellationToken) =>
        tokens.GetAsync(
            credentialId,
            issue,
            (token, now) => token.IsSuccess ? new CachedToken(token, now + token.ExpiresIn) : null,
            cached => cached.Token,
            cancellationToken);

    // Called when Pazarama answers 401 for a cached token (revoked or rotated in the panel).
    public void Forget(Guid credentialId) => tokens.Forget(credentialId);

    private sealed record CachedToken(PazaramaToken Token, DateTimeOffset ExpiresAt);
}

public sealed record PazaramaToken(string? AccessToken, TimeSpan ExpiresIn, MarketplaceHub.Application.AdapterError? Error)
{
    public bool IsSuccess => Error is null && !string.IsNullOrWhiteSpace(AccessToken);
    public override string ToString() => IsSuccess ? "PazaramaToken { issued }" : $"PazaramaToken {{ Error = {Error?.Code} }}";
}
