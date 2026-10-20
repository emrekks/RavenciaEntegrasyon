using System.Collections.Concurrent;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

// E-Faturam sign-in returns a JWT with an exp claim. One token per stored credential is kept in memory
// and renewed shortly before it expires, so every invoice operation no longer signs in with the account
// password; concurrent callers of the same credential wait for a single sign-in. A token without exp is
// not cached.
public sealed class TrendyolEFaturamTokenCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, TrendyolEFaturamAccessContext> tokens = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    public async Task<AdapterResult<TrendyolEFaturamAccessContext>> GetAsync(
        Guid credentialId,
        Func<CancellationToken, Task<AdapterResult<TrendyolEFaturamAccessContext>>> signIn,
        CancellationToken cancellationToken)
    {
        if (Fresh(credentialId) is { } cached) return AdapterResult<TrendyolEFaturamAccessContext>.Success(cached);
        var gate = locks.GetOrAdd(credentialId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(credentialId) is { } again) return AdapterResult<TrendyolEFaturamAccessContext>.Success(again);
            var result = await signIn(cancellationToken);
            if (result.IsSuccess && Usable(result.Value!)) tokens[credentialId] = result.Value!;
            return result;
        }
        finally { gate.Release(); }
    }

    // Called when the invoice API answers 401 for a token (revoked, rotated or expired early).
    public void Forget(Guid credentialId) => tokens.TryRemove(credentialId, out _);

    private TrendyolEFaturamAccessContext? Fresh(Guid credentialId) =>
        tokens.TryGetValue(credentialId, out var cached) && Usable(cached) ? cached : null;

    private bool Usable(TrendyolEFaturamAccessContext access) =>
        access.ExpiresAt is { } expiresAt && expiresAt - RenewBefore > timeProvider.GetUtcNow()
        && (access.NotBefore is not { } notBefore || notBefore <= timeProvider.GetUtcNow());
}
