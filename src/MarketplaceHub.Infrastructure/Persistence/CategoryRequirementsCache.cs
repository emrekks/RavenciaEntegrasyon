using MarketplaceHub.Application;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace MarketplaceHub.Infrastructure.Persistence;

public static class CatalogRequirementNotifications
{
    // Raised by statement triggers on every table the category attribute requirements are read from
    // (migration NotifyCatalogRequirementChanges), by the API and the Worker alike.
    public const string Channel = "catalog_requirements_changed";
}

// The editor's category attribute requirements (GET /catalog/categories/{id}/attribute-requirements) per tenant and
// category. Entries are only served while CategoryRequirementsChangeListener holds its LISTEN connection: any write to
// those tables, from this process or the Worker's syncs, clears every entry, and so does a lost or new connection. A
// process without the listener (the Worker, tests that do not start it) always reads the database.
public sealed class CategoryRequirementsCache(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private CancellationTokenSource _generation = new();
    private int _active;

    public bool IsActive => Volatile.Read(ref _active) == 1;

    public void SetActive(bool active)
    {
        Volatile.Write(ref _active, active ? 1 : 0);
        InvalidateAll();
    }

    // Read before the database is queried: a change committed while the query runs cancels it, and Set then
    // keeps nothing, so an entry never outlives a change it may have missed.
    public CancellationToken CurrentGeneration => Volatile.Read(ref _generation).Token;

    // The old source is not disposed: a reader may still hold its token and register on it in Set.
    public void InvalidateAll() => Interlocked.Exchange(ref _generation, new CancellationTokenSource()).Cancel();

    public bool TryGet(Guid tenantId, Guid categoryId, out IReadOnlyList<CategoryAttributeRequirementView> value)
    {
        value = [];
        if (!IsActive || !cache.TryGetValue(new Key(tenantId, categoryId), out IReadOnlyList<CategoryAttributeRequirementView>? cached) || cached is null) return false;
        value = cached;
        return true;
    }

    public void Set(Guid tenantId, Guid categoryId, CancellationToken generation, IReadOnlyList<CategoryAttributeRequirementView> value)
    {
        if (!IsActive || generation.IsCancellationRequested) return;
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 };
        options.ExpirationTokens.Add(new CancellationChangeToken(generation));
        cache.Set(new Key(tenantId, categoryId), value, options);
    }

    private sealed record Key(Guid TenantId, Guid CategoryId);
}
