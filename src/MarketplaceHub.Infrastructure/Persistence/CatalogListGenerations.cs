using System.Collections.Concurrent;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-10: a per-tenant counter that moves whenever this process saves a card or variant of the tenant, or writes its
// model links; the product list's cached rule check (FamilyIdMatchesModelCodeRuleCachedAsync) is keyed by it.
internal static class CatalogListGenerations
{
    private static readonly ConcurrentDictionary<Guid, long> Generations = new();

    public static long Of(Guid tenantId) => Generations.GetValueOrDefault(tenantId);

    public static void Bump(Guid tenantId) => Generations.AddOrUpdate(tenantId, 1, (_, value) => value + 1);

    // Before a save: the tenants whose cards or variants it adds, changes or deletes, bumped now and (by the caller) again
    // after the save, so a check that read the rows before the commit is not kept under the new counter.
    public static IReadOnlyList<Guid> Bump(ChangeTracker changeTracker)
    {
        var tenants = changeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity switch { Product product => (Guid?)product.TenantId, ProductVariant variant => variant.TenantId, _ => null })
            .OfType<Guid>().Distinct().ToList();
        foreach (var tenantId in tenants) Bump(tenantId);
        return tenants;
    }

    public static void Bump(IEnumerable<Guid> tenantIds)
    {
        foreach (var tenantId in tenantIds) Bump(tenantId);
    }
}
