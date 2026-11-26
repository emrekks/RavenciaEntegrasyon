using MarketplaceHub.Application;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace MarketplaceHub.Infrastructure.Persistence;

// The marketplace's webhook list behind GET /connections/{id}/instant-notifications (InstantNotificationService.StatusAsync).
// The dashboard asks every five minutes per connection from every open tab and phone, and the settings page on each visit
// (live 25-26 Nov 2026: 138 marketplace reads in 16.5 h for two connections, a fifth of them within a minute of the one
// before). Only a successful answer is kept, for one minute: a failed read is asked again next time and still shows
// "Durum okunamadı". The key holds the tenant, the connection, its version and the panel's registration (row and the
// marketplace's id), so a credential change, a new or closed registration, or a change by the Worker reads the
// marketplace again. Every change the panel makes at the marketplace (open, close, pause, resume, restore, recreate)
// clears all entries after the call, and an answer read while such a change ran is not kept.
public sealed class WebhookStatusCache(IMemoryCache cache)
{
    // One minute: the panel's settings page treats its answer as fresh for 30 s, and a state the marketplace changes on
    // its own (Trendyol sets a webhook passive) shows at most a minute later than today.
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private CancellationTokenSource _generation = new();

    // Read before the marketplace is asked: a change made while the read runs cancels it, and Set then keeps nothing.
    public CancellationToken CurrentGeneration => Volatile.Read(ref _generation).Token;

    // The old source is not disposed: a reader may still hold its token and register on it in Set.
    public void InvalidateAll() => Interlocked.Exchange(ref _generation, new CancellationTokenSource()).Cancel();

    public bool TryGet(Key key, out IReadOnlyList<RemoteWebhook> value)
    {
        value = [];
        if (!cache.TryGetValue(key, out IReadOnlyList<RemoteWebhook>? cached) || cached is null) return false;
        value = cached;
        return true;
    }

    public void Set(Key key, CancellationToken generation, IReadOnlyList<RemoteWebhook> value)
    {
        if (generation.IsCancellationRequested) return;
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 };
        options.ExpirationTokens.Add(new CancellationChangeToken(generation));
        cache.Set(key, value, options);
    }

    public sealed record Key(Guid TenantId, Guid ConnectionId, long ConnectionVersion, Guid SubscriptionId, string? ExternalSubscriptionId);
}
