using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 1 (16 Nov 2026): the variant link bookkeeping a catalog read does (the links it saw, the ones a
// complete scan missed, the marketplace codes and sale states it read, Hepsiburada's re-keying to the hbSku), moved out of
// the job processor as it was; the import and the listing verification use it alike. The links a scan may judge come from
// the listing verification rules (ListingVerificationRules). Reads only: nothing is sent to a marketplace.
internal sealed class VariantLinkBookkeeping(
    AppDbContext db,
    TimeProvider timeProvider,
    ListingVerificationRules listingRules)
{
    private IQueryable<MarketplaceVariantLink> VerifiableVariantLinks(Guid tenantId, Guid connectionId) => listingRules.VerifiableVariantLinks(tenantId, connectionId);

    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        new IssueRecorder(db).RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    private static string Short(string? value, int maximum) => CatalogAttributeService.Short(value, maximum);

    internal async Task<bool> ExternalIdTaken(Guid tenantId, Guid connectionId, string externalId, Guid? exceptLinkId, CancellationToken cancellationToken) =>
        db.MarketplaceVariantLinks.Local.Any(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalId && x.Id != exceptLinkId)
        || await db.MarketplaceVariantLinks.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalId && x.Id != exceptLinkId, cancellationToken);

    // Hepsiburada addresses a variant by its hbSku (HBCV…). Links an earlier import keyed by the merchant SKU (the
    // listing's hepsiburadaSku was not read then) move to the hbSku, with the merchant SKU and barcode kept beside it
    // for order lines. Saved at once: the import then finds each link by its hbSku instead of adding a second one.
    internal async Task RekeyHepsiburadaVariantLinks(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken)
    {
        var byMerchantSku = new Dictionary<string, RemoteCatalogVariant>(StringComparer.Ordinal);
        foreach (var remote in snapshots.SelectMany(snapshot => snapshot.Variants))
            if (HepsiburadaStageCertificationPolicy.LooksLikeHepsiburadaSku(remote.ExternalVariantId) && remote.Sku?.Trim() is { Length: > 0 } merchantSku
                && !string.Equals(merchantSku, remote.ExternalVariantId.Trim(), StringComparison.OrdinalIgnoreCase))
                byMerchantSku.TryAdd(merchantSku, remote);
        if (byMerchantSku.Count == 0) return;
        var merchantSkus = byMerchantSku.Keys.ToArray();
        var hbSkus = byMerchantSku.Values.Select(remote => Short(remote.ExternalVariantId.Trim(), 256)).ToArray();
        var links = await db.MarketplaceVariantLinks
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (merchantSkus.Contains(x.ExternalId) || hbSkus.Contains(x.ExternalId)))
            .ToListAsync(cancellationToken);
        var taken = links.Select(link => link.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = 0;
        foreach (var link in links)
        {
            if (!byMerchantSku.TryGetValue(link.ExternalId, out var remote)) continue;
            var hbSku = Short(remote.ExternalVariantId.Trim(), 256);
            // An hbSku already linked (to this or another variant) is left as it is.
            if (!taken.Add(hbSku)) continue;
            link.ExternalId = hbSku;
            link.ExternalSku = Short(remote.Sku!.Trim(), 160);
            link.ExternalBarcode = string.IsNullOrWhiteSpace(remote.Barcode) ? null : Short(remote.Barcode.Trim(), 160);
            link.Version++;
            moved++;
        }
        if (moved > 0) await db.SaveChangesAsync(cancellationToken);
    }

    // One update per catalog page: a link whose code (its id, or the marketplace stock code or barcode beside it) is on
    // the page was seen now, and a broken one is healthy again. Matching more codes than the link's own only errs
    // towards "seen", never towards a false break.
    // heal: false (an observing verification run) records only when the code was seen and leaves its health alone.
    internal async Task MarkVariantLinksSeen(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> page, CancellationToken cancellationToken, bool heal = true)
    {
        var codes = page.SelectMany(product => product.Variants.SelectMany(variant => new[] { variant.ExternalVariantId, variant.Sku, variant.Barcode }))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => Short(code!.Trim(), 256))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (codes.Length == 0) return;
        var now = timeProvider.GetUtcNow();
        var seen = db.MarketplaceVariantLinks
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && (codes.Contains(x.ExternalId) || x.ExternalSku != null && codes.Contains(x.ExternalSku) || x.ExternalBarcode != null && codes.Contains(x.ExternalBarcode)));
        // The sale state is only read, so an observing run records it too.
        await RecordVariantSaleStates(seen, page, now, cancellationToken);
        if (!heal)
        {
            await seen.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastSeenAt, now), cancellationToken);
            return;
        }
        await seen
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastSeenAt, now)
                .SetProperty(x => x.HealthChangedAt, x => x.HealthStatus != null ? now : x.HealthChangedAt)
                .SetProperty(x => x.HealthReason, x => x.HealthStatus != null ? null : x.HealthReason)
                .SetProperty(x => x.HealthStatus, (string?)null), cancellationToken);
    }

    // The sale state the page gives each link it names (ListingSaleStateReads): what the marketplace says now, unknown
    // when its answer does not say. One update per distinct state on the page; the publication state and everything
    // price/stock sends check are left alone.
    private static async Task RecordVariantSaleStates(IQueryable<MarketplaceVariantLink> seen, IReadOnlyList<RemoteCatalogProduct> page, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A product on the pending-approval list says nothing about the sale of an approved listing of the same code
        // (Trendyol lists a product under review on both lists).
        var variants = page.Where(product => !product.IsPendingApproval).SelectMany(product => product.Variants).ToList();
        if (variants.Count == 0) return;
        var links = await seen.AsNoTracking()
            .Select(x => new { x.Id, x.ExternalId, x.ExternalBarcode, x.ExternalSku })
            .ToListAsync(cancellationToken);
        var groups = links
            .Select(link => (link.Id, Read: ListingSaleStateReads.For(link.ExternalId, link.ExternalBarcode, link.ExternalSku, variants)))
            .Where(item => item.Read is not null)
            .GroupBy(item => item.Read!, item => item.Id);
        foreach (var group in groups)
        {
            var ids = group.ToArray();
            var (state, reason) = (group.Key.State, group.Key.Reason);
            DateTimeOffset? readAt = state is null ? null : now;
            await seen.Where(x => ids.Contains(x.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.SaleState, state)
                    .SetProperty(x => x.SaleStateReason, reason)
                    .SetProperty(x => x.SaleStateReadAt, readAt), cancellationToken);
        }
    }

    // After a complete catalog scan: a healthy link the scan never saw is gone from the marketplace. A scan that saw
    // too few links is taken for a short listing and marks nothing (MarketplaceVariantLinkHealth.FullScanCanMarkMissing).
    // A code saved by hand after the scan began is left alone until the next scan.
    internal async Task MarkMissingVariantLinks(Guid tenantId, Guid connectionId, string platformCode, DateTimeOffset scanStartedAt, CancellationToken cancellationToken)
    {
        // Archived panel variants and publications still on their way are never judged (VerifiableVariantLinks).
        var links = VerifiableVariantLinks(tenantId, connectionId);
        var healthy = await links.CountAsync(cancellationToken);
        if (healthy == 0) return;
        var seen = await links.CountAsync(x => x.LastSeenAt >= scanStartedAt, cancellationToken);
        var platform = MarketplacePlatformName(platformCode);
        if (!MarketplaceVariantLinkHealth.FullScanCanMarkMissing(seen, healthy))
        {
            await RecordIssue(tenantId, $"variant-link-scan-short:{connectionId}", "VARIANT_LINK_SCAN_SHORT",
                $"{platform} tam ürün taramasında bağlı {healthy:N0} varyant kodunun yalnız {seen:N0} tanesi görüldü; tarama eksik sayıldı, hiçbir kod kopuk işaretlenmedi.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var now = timeProvider.GetUtcNow();
        var unseen = links.Where(x => (x.LastSeenAt == null || x.LastSeenAt < scanStartedAt) && (x.HealthChangedAt == null || x.HealthChangedAt < scanStartedAt));
        // A link the previous full scan already missed breaks now; one missed for the first time only becomes
        // suspect and keeps reading as linked. The order matters: a link made suspect here is not broken in the same scan.
        var missing = await unseen.Where(x => x.HealthStatus == MarketplaceVariantLinkHealth.Suspect)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.NotFound)
                .SetProperty(x => x.HealthReason, MarketplaceVariantLinkHealth.NotFoundReason)
                .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
        await unseen.Where(x => x.HealthStatus == null || x.HealthStatus == MarketplaceVariantLinkHealth.Unverified)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.Suspect)
                .SetProperty(x => x.HealthReason, "Son tam ürün taramasında görülmedi.")
                .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
        if (missing > 0)
            await RecordIssue(tenantId, $"variant-link-not-found:{connectionId}", "VARIANT_LINK_NOT_FOUND",
                $"{platform}: {missing:N0} varyantın kodu art arda iki tam ürün taramasında pazaryerinde bulunamadı. Fiyat ve stok gönderimi sürüyor; kodu ürünün Varyant Eşleştirme penceresinden kontrol edin.", cancellationToken);
        if (missing > 0) await db.SaveChangesAsync(cancellationToken);
    }

    internal static string MarketplacePlatformName(string? platformCode) => platformCode switch
    {
        "HEPSIBURADA" => "Hepsiburada",
        "SHOPIFY" => "Shopify",
        "TRENDYOL" => "Trendyol",
        "PTTAVM" => "PttAVM",
        "N11" => "N11",
        "PAZARAMA" => "Pazarama",
        _ => platformCode ?? "Pazaryeri"
    };

    // Every link the import read keeps the marketplace's own barcode and stock code, whichever code paired it with
    // the panel variant: price, stock and order reads address the marketplace by them, not by the panel's codes.
    // onlyAdded: only the links this batch made (a new-only or focused price/stock run leaves the panel's links as they were).
    internal async Task RecordVariantLinkCodes(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken, bool onlyAdded = false)
    {
        var remotes = new Dictionary<string, RemoteCatalogVariant>(StringComparer.Ordinal);
        foreach (var remote in snapshots.SelectMany(snapshot => snapshot.Variants))
            if (Short(remote.ExternalVariantId, 256) is { Length: > 0 } externalId) remotes.TryAdd(externalId, remote);
        if (remotes.Count == 0) return;
        var externalIds = remotes.Keys.ToArray();
        var links = onlyAdded
            ? []
            : await db.MarketplaceVariantLinks.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalId)).ToListAsync(cancellationToken);
        links.AddRange(db.MarketplaceVariantLinks.Local.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && remotes.ContainsKey(x.ExternalId) && !links.Contains(x)
            && (!onlyAdded || db.Entry(x).State == EntityState.Added)));
        var handedBack = new HashSet<Guid>();
        foreach (var link in links)
        {
            // A link the mapping of this batch re-keyed in memory (an old id replaced by the variant's code) is loaded
            // by its stored id but carries the new one; it is not a remote read here.
            if (!remotes.TryGetValue(link.ExternalId, out var remote)) continue;
            // The import read this code from the marketplace, so it holds there: a link made in this batch starts seen.
            var wasBroken = link.HealthStatus is not null;
            MarketplaceVariantLinkHealth.MarkSeen(link, timeProvider.GetUtcNow());
            var barcode = string.IsNullOrWhiteSpace(remote.Barcode) ? null : Short(remote.Barcode.Trim(), 160);
            var sku = string.IsNullOrWhiteSpace(remote.Sku) ? null : Short(remote.Sku.Trim(), 160);
            // Who keeps the stock (n11depom, Hepsiburada fulfilment) is taken only from an answer that says it.
            var fulfilled = remote.FulfilledByMarketplace ?? link.FulfilledByMarketplace;
            if (link.ExternalBarcode == barcode && link.ExternalSku == sku && link.FulfilledByMarketplace == fulfilled && !wasBroken) continue;
            // A variant the marketplace stops keeping stock for goes back to the seller's stock: the stock is sent again,
            // since the composer moved its mark on without sending while the marketplace kept it.
            if (link.FulfilledByMarketplace == true && fulfilled == false) handedBack.Add(link.VariantId);
            link.ExternalBarcode = barcode;
            link.ExternalSku = sku;
            link.FulfilledByMarketplace = fulfilled;
            if (db.Entry(link).State != EntityState.Added) link.Version++;
        }
        if (handedBack.Count == 0) return;
        var offers = db.ChannelOffers.Local.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && handedBack.Contains(x.VariantId)).ToList();
        var loaded = offers.Select(x => x.VariantId).ToHashSet();
        var missing = handedBack.Where(variantId => !loaded.Contains(variantId)).ToArray();
        if (missing.Length > 0)
            offers.AddRange(await db.ChannelOffers.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && missing.Contains(x.VariantId)).ToListAsync(cancellationToken));
        foreach (var offer in offers.Where(x => x.LastStockProjectionVersion is not null))
        {
            offer.LastStockProjectionVersion = null;
            if (db.Entry(offer).State != EntityState.Added) offer.Version++;
        }
    }

}
