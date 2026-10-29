using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Shopify publications made from the panel had no variant link (live 2026-10-29: 72 LIVE Shopify listing variants, none
// linked), and link health lives on links, so the verification could never judge them. Every LIVE Shopify listing
// variant without a link gets one: a placeholder id carrying the listing's SKU and barcode (never sent as a Shopify
// variant id), UNVERIFIED, so nothing reads missing until a scan says so. The status check that next answers the
// variant replaces the placeholder with Shopify's own id. Idempotent; run with the hourly policy pass.
internal static class ShopifyListingLinkBackfill
{
    private const int BatchSize = 500;

    public static async Task<int> EnsureAsync(AppDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var missing = await (from listing in db.ChannelListingVariants.AsNoTracking()
                             join profile in db.ChannelListingProfiles.AsNoTracking() on new { listing.TenantId, listing.ProfileId } equals new { profile.TenantId, ProfileId = profile.Id }
                             join connection in db.PlatformConnections.AsNoTracking() on new { profile.TenantId, Id = profile.ConnectionId } equals new { connection.TenantId, connection.Id }
                             where connection.PlatformCode == "SHOPIFY" && listing.ActualStatus == "LIVE"
                                 && (listing.ExternalSku != null || listing.ExternalBarcode != null)
                                 && !db.MarketplaceVariantLinks.Any(link => link.TenantId == listing.TenantId && link.ConnectionId == profile.ConnectionId && link.VariantId == listing.VariantId)
                             orderby listing.Id
                             select new { listing.TenantId, profile.ConnectionId, listing.VariantId, listing.ExternalSku, listing.ExternalBarcode })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (missing.Count == 0) return 0;
        var now = timeProvider.GetUtcNow();
        var rows = missing
            .Select(row => new
            {
                row.TenantId,
                row.ConnectionId,
                row.VariantId,
                row.ExternalSku,
                row.ExternalBarcode,
                ExternalId = MarketplaceVariantLinkHealth.ListingPlaceholderPrefix + Trim(string.IsNullOrWhiteSpace(row.ExternalSku) ? row.ExternalBarcode! : row.ExternalSku, 240)
            })
            .GroupBy(row => (row.TenantId, row.ConnectionId, row.VariantId)).Select(group => group.First())
            .GroupBy(row => (row.TenantId, row.ConnectionId, row.ExternalId)).Where(group => group.Count() == 1).Select(group => group.First())
            .ToList();
        var externalIds = rows.Select(row => row.ExternalId).Distinct().ToArray();
        var taken = (await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(link => externalIds.Contains(link.ExternalId))
                .Select(link => new { link.TenantId, link.ConnectionId, link.ExternalId })
                .ToListAsync(cancellationToken))
            .Select(link => (link.TenantId, link.ConnectionId, link.ExternalId))
            .ToHashSet();
        var added = 0;
        foreach (var row in rows.Where(row => !taken.Contains((row.TenantId, row.ConnectionId, row.ExternalId))))
        {
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = row.TenantId,
                ConnectionId = row.ConnectionId,
                VariantId = row.VariantId,
                ExternalId = row.ExternalId,
                ExternalSku = string.IsNullOrWhiteSpace(row.ExternalSku) ? null : Trim(row.ExternalSku, 160),
                ExternalBarcode = string.IsNullOrWhiteSpace(row.ExternalBarcode) ? null : Trim(row.ExternalBarcode, 160),
                HealthStatus = MarketplaceVariantLinkHealth.Unverified,
                HealthChangedAt = now,
                Version = 1
            });
            added++;
        }
        if (added == 0) return 0;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A publication check linked one of them meanwhile; the next pass sees the committed state.
            db.ChangeTracker.Clear();
            return 0;
        }
        return added;
    }

    private static string Trim(string value, int maximum)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }
}
