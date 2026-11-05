using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Yayınla on a colour card that is already on the marketplace sends only the card's variants not there yet (a size or
// barcode added since), under the card's model code, so the marketplace adds them to the listing it already holds
// (Trendyol: same productMainId; Hepsiburada: same VaryantGroupID; Pazarama: same groupCode). A variant is there when
// it has a link the marketplace still knows (not NOT_FOUND / REJECTED) or a LIVE listing whose link is not broken; a
// variant the marketplace lost goes again. Live 2026-11-05 (MZ021Y-CD on Trendyol): 21 new variants of a matched card
// were never sent because a card with any matched variant was skipped whole.
internal static class PublicationUnsentVariants
{
    internal static async Task<HashSet<Guid>> PresentAsync(AppDbContext db, Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0) return [];
        var links = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(link => link.TenantId == tenantId && link.ConnectionId == connectionId && variantIds.Contains(link.VariantId))
            .Select(link => new { link.VariantId, link.HealthStatus })
            .ToListAsync(cancellationToken);
        var broken = links.Where(link => MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus)).Select(link => link.VariantId).ToHashSet();
        var present = links.Where(link => !MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus)).Select(link => link.VariantId).ToHashSet();
        var liveListings = await (from listing in db.ChannelListingVariants.AsNoTracking()
                                  join profile in db.ChannelListingProfiles.AsNoTracking() on new { listing.TenantId, Id = listing.ProfileId } equals new { profile.TenantId, profile.Id }
                                  where listing.TenantId == tenantId && profile.ConnectionId == connectionId && variantIds.Contains(listing.VariantId) && listing.ActualStatus == "LIVE"
                                  select listing.VariantId).ToListAsync(cancellationToken);
        present.UnionWith(liveListings.Where(variantId => !broken.Contains(variantId)));
        return present;
    }

    // The card's variants still to send; the whole card when none is there. Null when every variant is there already.
    internal static IReadOnlyList<T>? Unsent<T>(IReadOnlyList<T> cardVariants, Func<T, Guid> id, IReadOnlySet<Guid> present)
    {
        var unsent = cardVariants.Where(variant => !present.Contains(id(variant))).ToList();
        return unsent.Count == 0 ? null : unsent;
    }

    internal static string AllPresentMessage(string marketplaceName) =>
        $"Kartın bütün varyantları {marketplaceName}'da zaten var; gönderilecek yeni varyant yok.";
}
