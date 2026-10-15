using System.Collections.Concurrent;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Pazarama access tokens live one hour. One token per stored credential is kept in memory
// and renewed five minutes before it expires; concurrent callers of the same credential
// wait for a single token request.
public sealed class PazaramaTokenCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, CachedToken> tokens = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    public async Task<PazaramaToken> GetAsync(Guid credentialId, Func<CancellationToken, Task<PazaramaToken>> issue, CancellationToken cancellationToken)
    {
        if (Fresh(credentialId) is { } cached) return cached;
        var gate = locks.GetOrAdd(credentialId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(credentialId) is { } again) return again;
            var token = await issue(cancellationToken);
            if (token.IsSuccess) tokens[credentialId] = new(token, timeProvider.GetUtcNow() + token.ExpiresIn);
            return token;
        }
        finally { gate.Release(); }
    }

    // Called when Pazarama answers 401 for a cached token (revoked or rotated in the panel).
    public void Forget(Guid credentialId) => tokens.TryRemove(credentialId, out _);

    private PazaramaToken? Fresh(Guid credentialId) =>
        tokens.TryGetValue(credentialId, out var cached) && cached.ExpiresAt - RenewBefore > timeProvider.GetUtcNow() ? cached.Token : null;

    private sealed record CachedToken(PazaramaToken Token, DateTimeOffset ExpiresAt);
}

public sealed record PazaramaToken(string? AccessToken, TimeSpan ExpiresIn, MarketplaceHub.Application.AdapterError? Error)
{
    public bool IsSuccess => Error is null && !string.IsNullOrWhiteSpace(AccessToken);
    public override string ToString() => IsSuccess ? "PazaramaToken { issued }" : $"PazaramaToken {{ Error = {Error?.Code} }}";
}
