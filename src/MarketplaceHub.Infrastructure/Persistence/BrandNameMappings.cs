using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Hepsiburada, n11 and Shopify have no brand list: they take the brand name as free text. A name the user
// typed for the connection (BrandNameMapping) is sent instead of the panel brand name; without one the
// panel name goes, as before.
internal static class BrandNameMappings
{
    public static readonly string[] Platforms = ["HEPSIBURADA", "N11", "SHOPIFY"];

    public static async Task<string> ResolveAsync(AppDbContext db, Guid tenantId, Guid connectionId, Guid brandId, string panelName, CancellationToken cancellationToken)
    {
        var name = await db.BrandNameMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == brandId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(cancellationToken);
        return Pick(name, panelName);
    }

    internal static string Pick(string? mappedName, string panelName) =>
        string.IsNullOrWhiteSpace(mappedName) ? panelName.Trim() : mappedName.Trim();
}
