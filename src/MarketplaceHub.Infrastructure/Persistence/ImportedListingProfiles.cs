using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A product the panel matched to a marketplace listing (its variants carry marketplace_variant_links, written by a
// product sync that read the listing) gets the listing profile the panel's own updates and status checks need: one per
// product and connection, Enabled, Source MARKETPLACE_IMPORT, and one listing variant per linked variant with the codes
// the marketplace holds (the link's barcode and stock code).
//
// Owner decision 6 Nov 2026: the panel card wins. The profile carries no marketplace value: no title, description,
// delivery days, origin, warranty or VAT override, and no category or brand id (a marketplace category different from the
// panel's verified mapping would stop every update as LISTING_MAPPING_CONFLICT). Updates keep sending the card and its
// verified mappings, as for a profile the panel made. Nothing is assumed about the listing either: profile and listing
// variants and their marketplace_listing_states start UNKNOWN (the update's approval check or a status refresh reads the
// real state). PriceInventoryComposer sends only a LIVE state by the listing and leaves these listings out of its
// "listed" set, so an imported variant's price and stock keep going by its link until a read says LIVE.
//
// Only the database is written; nothing is sent to a marketplace. Shopify is left out: its update writes the whole
// product (productSet), and its products are published from the panel. A profile the panel made is never touched.
internal static class ImportedListingProfiles
{
    public const string UnknownState = "UNKNOWN";

    // Said when a card has no profile on the connection. The update never makes one (owner decision 6 Nov 2026): a
    // product sync that reads the products' information makes it for a product already on the marketplace.
    public static string MissingPublicationMessage(string platformName) =>
        $"Bu ürünün {platformName} yayın kaydı yok. Ürün bu mağazada zaten satışta ise Ürünler sayfasında İçe / Dışa Aktar > Platformdan Ürün Aktar ile bu mağaza için ürün bilgilerini okuyan bir aktarım çalıştırın (Mevcut Ürünleri Güncelle ya da Tam Katalog Taraması); aktarım yayın kaydını oluşturur, sonra güncellemeyi tekrar deneyin. Ürün orada yoksa önce Yayınla ile gönderin.";

    public static bool Supported(string? platformCode) => platformCode?.Trim().ToUpperInvariant() is "TRENDYOL" or "HEPSIBURADA" or "N11" or "PAZARAMA" or "PTTAVM";

    // The products whose variants are linked under these marketplace codes (a link's id, or the barcode it holds).
    public static async Task<IReadOnlyList<Guid>> ProductIdsForCodesAsync(AppDbContext db, Guid tenantId, Guid connectionId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0) return [];
        var values = codes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal).ToArray();
        return await (from link in db.MarketplaceVariantLinks.AsNoTracking()
                      join variant in db.ProductVariants.AsNoTracking() on new { link.TenantId, link.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                      where link.TenantId == tenantId && link.ConnectionId == connectionId && (values.Contains(link.ExternalId) || (link.ExternalBarcode != null && values.Contains(link.ExternalBarcode)))
                      select variant.ProductId).Distinct().ToListAsync(cancellationToken);
    }

    // Creates the missing profiles of these products on the connection, and the missing listing variants of the profiles
    // made here (a variant linked later joins its profile on the next run). Idempotent. Returns the profiles created.
    // Saves its own changes; a concurrent run that made the same row first leaves it as that run made it.
    public static async Task<int> EnsureAsync(AppDbContext db, Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return 0;
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (!Supported(platformCode)) return 0;
        var ids = productIds.Distinct().ToArray();
        var linked = await (from link in db.MarketplaceVariantLinks.AsNoTracking()
                            join variant in db.ProductVariants.AsNoTracking() on new { link.TenantId, link.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                            where link.TenantId == tenantId && link.ConnectionId == connectionId && ids.Contains(variant.ProductId) && variant.Status != ProductStatus.Archived
                            select new { variant.ProductId, variant.Id, link.ExternalBarcode, link.ExternalSku }).ToListAsync(cancellationToken);
        if (linked.Count == 0) return 0;
        var linkedProductIds = linked.Select(x => x.ProductId).Distinct().ToArray();
        var profiles = await db.ChannelListingProfiles.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && linkedProductIds.Contains(x.ProductId)).ToDictionaryAsync(x => x.ProductId, cancellationToken);
        var importedProfileIds = profiles.Values.Where(x => x.Source == ChannelListingProfileSource.MarketplaceImport).Select(x => x.Id).ToArray();
        var listed = (await db.ChannelListingVariants.AsNoTracking().Where(x => x.TenantId == tenantId && importedProfileIds.Contains(x.ProfileId)).Select(x => new { x.ProfileId, x.VariantId }).ToListAsync(cancellationToken))
            .Select(x => (x.ProfileId, x.VariantId)).ToHashSet();
        var created = 0;
        var added = 0;
        foreach (var group in linked.GroupBy(x => x.ProductId))
        {
            if (!profiles.TryGetValue(group.Key, out var profile))
            {
                profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ProductId = group.Key, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "UNKNOWN", Source = ChannelListingProfileSource.MarketplaceImport, Version = 1 };
                db.ChannelListingProfiles.Add(profile);
                created++;
            }
            else if (profile.Source != ChannelListingProfileSource.MarketplaceImport) continue;
            foreach (var variant in group.GroupBy(x => x.Id).Select(x => x.First()))
            {
                if (!listed.Add((profile.Id, variant.Id))) continue;
                added++;
                db.ChannelListingVariants.Add(new ChannelListingVariant
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ProfileId = profile.Id,
                    VariantId = variant.Id,
                    ExternalBarcode = string.IsNullOrWhiteSpace(variant.ExternalBarcode) ? null : variant.ExternalBarcode.Trim(),
                    ExternalSku = string.IsNullOrWhiteSpace(variant.ExternalSku) ? null : variant.ExternalSku.Trim(),
                    DesiredStatus = "LIVE",
                    ActualStatus = "UNKNOWN"
                });
            }
        }
        // Every listing of an imported profile has its listing state: the update and approval jobs require one per listing
        // (PRODUCT_UPDATE_STATE_INCOMPLETE otherwise; live 7 Nov 2026, RYP003T). It starts UNKNOWN, which the price/stock
        // composer never reads as live, so the variant's price and stock keep going by its link until a marketplace read
        // (the update's approval check, a status refresh) records the real state. An existing state is left as it is.
        // listed holds only imported profiles' listings: the ones read above and the ones added here.
        var importedVariantIds = listed.Select(x => x.VariantId).Distinct().ToArray();
        var stated = new HashSet<Guid>();
        if (importedVariantIds.Length > 0)
            stated.UnionWith(await db.MarketplaceListingStates.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && importedVariantIds.Contains(x.VariantId)).Select(x => x.VariantId).ToListAsync(cancellationToken));
        foreach (var variantId in importedVariantIds.Where(id => !stated.Contains(id)))
        {
            db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variantId, DesiredStatus = "LIVE", ActualStatus = UnknownState, Version = 1 });
            added++;
        }
        if (created == 0 && added == 0) return 0;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another sync run made the same row first: keep its rows.
            db.ChangeTracker.Clear();
            return 0;
        }
        return created;
    }
}
