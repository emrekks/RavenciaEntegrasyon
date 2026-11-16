using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 round 10 step 2 (16 Nov 2026): which variant links a catalog scan may judge, moved out of the processor's
// listing verification as it was, so the listing verification and the import's variant link bookkeeping use the same
// rules without the bookkeeping calling back into the processor. Queries only.
internal sealed class ListingVerificationRules(AppDbContext db)
{
    // The links a scan may judge: healthy, suspect or unverified ones, of a variant the panel has not archived and
    // whose publication on the connection is not on its way (it may not be on the marketplace's lists yet).
    public IQueryable<MarketplaceVariantLink> VerifiableVariantLinks(Guid tenantId, Guid connectionId) =>
        JudgeableVariantLinks(tenantId, connectionId).Where(x => x.HealthStatus == null || x.HealthStatus == MarketplaceVariantLinkHealth.Suspect || x.HealthStatus == MarketplaceVariantLinkHealth.Unverified);

    // Every link of the connection whose variant the panel has not archived and whose publication is not on its way.
    public IQueryable<MarketplaceVariantLink> JudgeableVariantLinks(Guid tenantId, Guid connectionId)
    {
        var inFlight = MarketplaceVariantLinkHealth.PublicationInFlightStatuses;
        return db.MarketplaceVariantLinks.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
            && !db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == x.VariantId
                && (variant.Status == ProductStatus.Archived || db.Products.Any(product => product.TenantId == tenantId && product.Id == variant.ProductId && product.Status == ProductStatus.Archived)))
            && !db.MarketplaceListingStates.Any(state => state.TenantId == tenantId && state.ConnectionId == connectionId && state.VariantId == x.VariantId && inFlight.Contains(state.ActualStatus))
            && !db.ChannelListingVariants.Any(listing => listing.TenantId == tenantId && listing.VariantId == x.VariantId
                && db.ChannelListingProfiles.Any(profile => profile.TenantId == tenantId && profile.Id == listing.ProfileId && profile.ConnectionId == connectionId
                    && (inFlight.Contains(listing.ActualStatus) || inFlight.Contains(profile.ActualStatus)))));
    }
}
