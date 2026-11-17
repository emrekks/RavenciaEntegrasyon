using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Pazarama access tokens live one hour. One token per stored credential is kept in memory
// and renewed five minutes before it expires; concurrent callers of the same credential
// wait for a single token request.
// A passing failure of the token service (an empty 200, a 5xx, a timeout, a quota or gateway refusal) is kept for
// FailureHold (17 Nov 2026: during a Pazarama outage every job and every open order of the lifecycle scan asked the
// token service again), so the callers of that half minute get the same answer instead of each asking again. A refused
// credential or an unreadable answer is never kept: the next caller asks again.
public sealed class PazaramaTokenCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan FailureHold = TimeSpan.FromSeconds(30);
    private readonly SingleFlightTokenCache<Guid, CachedToken> tokens = new(timeProvider, (cached, now) => cached.Token.IsSuccess
        ? cached.ExpiresAt - RenewBefore > now
        : now >= cached.StoredAt && now < cached.StoredAt + FailureHold);

    public Task<PazaramaToken> GetAsync(Guid credentialId, Func<CancellationToken, Task<PazaramaToken>> issue, CancellationToken cancellationToken) =>
        tokens.GetAsync(
            credentialId,
            issue,
            (token, now) => token.IsSuccess ? new CachedToken(token, now, now + token.ExpiresIn)
                : IsPassing(token.Error) ? new CachedToken(token, now, now + FailureHold)
                : null,
            cached => cached.Token,
            cancellationToken);

    // Called when Pazarama answers 401 for a cached token (revoked or rotated in the panel).
    public void Forget(Guid credentialId) => tokens.Forget(credentialId);

    // A failure that says nothing about the credentials: the token service did not answer usefully this time.
    internal static bool IsPassing(AdapterError? error) =>
        error?.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.RateLimit;

    // StoredAt bounds a held failure on both sides: a clock set back (NTP step) must not keep it for longer than FailureHold.
    private sealed record CachedToken(PazaramaToken Token, DateTimeOffset StoredAt, DateTimeOffset ExpiresAt);
}

public sealed record PazaramaToken(string? AccessToken, TimeSpan ExpiresIn, MarketplaceHub.Application.AdapterError? Error)
{
    public bool IsSuccess => Error is null && !string.IsNullOrWhiteSpace(AccessToken);
    public override string ToString() => IsSuccess ? "PazaramaToken { issued }" : $"PazaramaToken {{ Error = {Error?.Code} }}";
}
