using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MarketplaceHub.Infrastructure.Persistence;

// K-3: a channel offer's "sent" marks (LastPriceHash, LastStockProjectionVersion) name the marketplace address the
// values went to. When the variant link of that connection and variant points elsewhere (another marketplace id, a
// changed barcode or stock code, another variant, or no link at all) the marks no longer hold, and the offer is
// unsent again: the next price/stock send (the stock reconcile scan takes stock-lagging offers first) goes to the new
// address. AppDbContext applies this on every save; a save that writes the marks itself (a mapping seeding them from
// what the marketplace shows) keeps its own.
internal static class ChannelOfferSendMarks
{
    // A placeholder that takes the marketplace's own id for the same listing is not a new address; nor is a code
    // recorded for the first time (null before: the send went by the fallback code of the same listing).
    internal static bool Readdressed(string originalExternalId, string externalId, string? originalBarcode, string? barcode, string? originalSku, string? sku) =>
        !string.Equals(originalExternalId, externalId, StringComparison.Ordinal) && !MarketplaceVariantLinkHealth.IsListingPlaceholder(originalExternalId)
        || originalBarcode is not null && !string.Equals(originalBarcode, barcode, StringComparison.Ordinal)
        || originalSku is not null && !string.Equals(originalSku, sku, StringComparison.Ordinal);

    internal static HashSet<(Guid TenantId, Guid ConnectionId, Guid VariantId)> ReaddressedTargets(ChangeTracker changeTracker)
    {
        var targets = new HashSet<(Guid, Guid, Guid)>();
        foreach (var entry in changeTracker.Entries<MarketplaceVariantLink>())
        {
            var link = entry.Entity;
            var originalVariantId = entry.Property(x => x.VariantId).OriginalValue;
            if (entry.State == EntityState.Deleted)
            {
                targets.Add((link.TenantId, link.ConnectionId, originalVariantId));
                continue;
            }
            if (entry.State != EntityState.Modified) continue;
            if (originalVariantId != link.VariantId)
            {
                targets.Add((link.TenantId, link.ConnectionId, originalVariantId));
                targets.Add((link.TenantId, link.ConnectionId, link.VariantId));
                continue;
            }
            if (Readdressed(entry.Property(x => x.ExternalId).OriginalValue, link.ExternalId,
                    entry.Property(x => x.ExternalBarcode).OriginalValue, link.ExternalBarcode,
                    entry.Property(x => x.ExternalSku).OriginalValue, link.ExternalSku))
                targets.Add((link.TenantId, link.ConnectionId, link.VariantId));
        }
        return targets;
    }

    internal static IQueryable<ChannelOffer> Candidates(AppDbContext db, IReadOnlyCollection<(Guid TenantId, Guid ConnectionId, Guid VariantId)> targets)
    {
        var tenantIds = targets.Select(x => x.TenantId).Distinct().ToArray();
        var connectionIds = targets.Select(x => x.ConnectionId).Distinct().ToArray();
        var variantIds = targets.Select(x => x.VariantId).Distinct().ToArray();
        return db.ChannelOffers.Where(x => tenantIds.Contains(x.TenantId) && connectionIds.Contains(x.ConnectionId) && variantIds.Contains(x.VariantId));
    }

    internal static void ResetLoaded(AppDbContext db, IEnumerable<ChannelOffer> offers, IReadOnlySet<(Guid TenantId, Guid ConnectionId, Guid VariantId)> targets)
    {
        foreach (var offer in offers)
        {
            if (!targets.Contains((offer.TenantId, offer.ConnectionId, offer.VariantId))) continue;
            var entry = db.Entry(offer);
            if (entry.State is not (EntityState.Unchanged or EntityState.Modified)) continue;
            if (entry.Property(x => x.LastPriceHash).IsModified || entry.Property(x => x.LastStockProjectionVersion).IsModified) continue;
            Reset(offer);
        }
    }

    internal static bool Reset(ChannelOffer offer)
    {
        if (offer.LastPriceHash is null && offer.LastStockProjectionVersion is null && offer.LastSentCode is null) return false;
        offer.LastPriceHash = null;
        offer.LastStockProjectionVersion = null;
        // The values confirmed at the old address say nothing of the new one.
        PriceInventorySendPolicy.ClearSent(offer);
        offer.Version++;
        return true;
    }
}
