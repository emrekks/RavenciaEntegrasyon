using System.Collections.Concurrent;

namespace MarketplaceHub.Infrastructure.Adapters;

// One marketplace token per key (a stored credential), kept in memory until it is no longer fresh. Concurrent callers of
// the same key wait for a single token request (single flight); a failed request is never cached, so the next caller
// asks again. Each adapter supplies its own freshness rule (renewal margin, not-before) and its own invalidation
// (Forget after a 401). Used by the Pazarama, E-Faturam and Shopify client-credentials tokens.
internal sealed class SingleFlightTokenCache<TKey, TToken>(TimeProvider time, Func<TToken, DateTimeOffset, bool> isFresh)
    where TKey : notnull
    where TToken : class
{
    private readonly ConcurrentDictionary<TKey, TToken> tokens = new();
    private readonly ConcurrentDictionary<TKey, SemaphoreSlim> locks = new();

    // issue asks the marketplace; toCached turns its answer into the entry to keep (null: keep nothing), given the
    // time the answer arrived; fromCached turns a kept entry back into the caller's answer.
    public async Task<TResult> GetAsync<TResult>(
        TKey key,
        Func<CancellationToken, Task<TResult>> issue,
        Func<TResult, DateTimeOffset, TToken?> toCached,
        Func<TToken, TResult> fromCached,
        CancellationToken cancellationToken)
    {
        if (Fresh(key) is { } cached) return fromCached(cached);
        var gate = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(key) is { } again) return fromCached(again);
            var result = await issue(cancellationToken);
            if (toCached(result, time.GetUtcNow()) is { } entry) tokens[key] = entry;
            return result;
        }
        finally { gate.Release(); }
    }

    public void Forget(TKey key) => tokens.TryRemove(key, out _);

    public void ForgetWhere(Func<TKey, bool> match)
    {
        foreach (var key in tokens.Keys)
            if (match(key)) tokens.TryRemove(key, out _);
    }

    private TToken? Fresh(TKey key) => tokens.TryGetValue(key, out var cached) && isFresh(cached, time.GetUtcNow()) ? cached : null;
}
