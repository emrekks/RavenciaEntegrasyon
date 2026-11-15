using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private async Task UpsertMarketplaceProductLink(Guid tenantId, Guid connectionId, Guid productId, string externalProductId, CancellationToken cancellationToken)
    {
        var productLink = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == productId, cancellationToken);
        if (productLink is null) db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ProductId = productId, ExternalId = externalProductId, Version = 1 });
    }

    // A Trendyol placeholder link (the unapproved list's model code) takes the approved content's id, or is dropped when
    // that content belongs to a sibling card of the model.
    private async Task ReplacePlaceholderProductLink(Guid linkId, string? contentId, CancellationToken cancellationToken)
    {
        var link = await db.MarketplaceProductLinks.SingleAsync(x => x.Id == linkId, cancellationToken);
        if (contentId is null)
        {
            db.MarketplaceProductLinks.Remove(link);
            return;
        }
        link.ExternalId = contentId;
        link.Version++;
    }

    private async Task MarkProductLinkPublished(Guid tenantId, Guid connectionId, Guid productId, CancellationToken cancellationToken)
    {
        var link = db.MarketplaceProductLinks.Local.SingleOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == productId)
            ?? await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == productId, cancellationToken);
        if (link is null) return;
        var productVersion = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == productId).Select(x => x.Version).SingleAsync(cancellationToken);
        link.LastPublishedProductVersion = productVersion;
        link.LastPublishedAt = timeProvider.GetUtcNow();
        link.SyncStatus = "SYNCED";
        link.DirtyFieldsJson = null;
        link.LastError = null;
        link.Version++;
    }

    // The marketplace answered the variant live: its link exists and reads healthy again, whatever a scan or a refused
    // price/stock line said before (the panel trusts a broken link over a live listing).
    // A Trendyol placeholder link (the barcode, from the unapproved list) takes the variantId the approval answered.
    private async Task UpsertMarketplaceVariantLink(Guid tenantId, Guid connectionId, Guid variantId, string externalVariantId, CancellationToken cancellationToken, bool replacePlaceholder = false)
    {
        var now = timeProvider.GetUtcNow();
        var variantLink = db.MarketplaceVariantLinks.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId)
            ?? await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId, cancellationToken);
        if (variantLink is null)
        {
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variantId, ExternalId = externalVariantId, LastSeenAt = now, Version = 1 });
            return;
        }
        if (replacePlaceholder && !string.Equals(variantLink.ExternalId, externalVariantId, StringComparison.Ordinal) && ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId(variantLink.ExternalId))
        {
            variantLink.ExternalId = externalVariantId;
            MarketplaceVariantLinkHealth.MarkSeen(variantLink, now);
            variantLink.Version++;
            return;
        }
        HealLiveLink(variantLink, now);
    }

    private void HealLiveLink(MarketplaceVariantLink link, DateTimeOffset now)
    {
        var wasBroken = link.HealthStatus is not null;
        MarketplaceVariantLinkHealth.MarkSeen(link, now);
        if (wasBroken && db.Entry(link).State != EntityState.Added) link.Version++;
    }

    // A listing variant the marketplace answered live. Its existing link is healed; a Shopify publication without a link
    // gets one (Shopify keeps no import link for panel publications) under the variant id Shopify answered, or, until
    // Shopify names it, under a placeholder that carries the listing's codes and is never sent as a variant id.
    private async Task EnsureLiveListingLink(Guid tenantId, Guid connectionId, ChannelListingVariant listing, string? remoteVariantId, bool createIfMissing, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var remoteId = string.IsNullOrWhiteSpace(remoteVariantId) ? null : Short(remoteVariantId, 256);
        var link = db.MarketplaceVariantLinks.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == listing.VariantId)
            ?? await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == listing.VariantId, cancellationToken);
        if (link is null)
        {
            if (!createIfMissing) return;
            var own = string.IsNullOrWhiteSpace(listing.ExternalSku) ? listing.ExternalBarcode : listing.ExternalSku;
            var code = remoteId ?? (string.IsNullOrWhiteSpace(own) ? null : MarketplaceVariantLinkHealth.ListingPlaceholderPrefix + Short(own, 240));
            if (code is null || await ExternalIdTaken(tenantId, connectionId, code, null, cancellationToken)) return;
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                VariantId = listing.VariantId,
                ExternalId = code,
                ExternalSku = string.IsNullOrWhiteSpace(listing.ExternalSku) ? null : Short(listing.ExternalSku, 160),
                ExternalBarcode = string.IsNullOrWhiteSpace(listing.ExternalBarcode) ? null : Short(listing.ExternalBarcode, 160),
                LastSeenAt = now,
                HealthChangedAt = now,
                Version = 1
            });
            return;
        }
        if (remoteId is not null && MarketplaceVariantLinkHealth.IsListingPlaceholder(link.ExternalId) && !await ExternalIdTaken(tenantId, connectionId, remoteId, link.Id, cancellationToken))
        {
            link.ExternalId = remoteId;
            link.Version++;
        }
        HealLiveLink(link, now);
    }

    private async Task<bool> ExternalIdTaken(Guid tenantId, Guid connectionId, string externalId, Guid? exceptLinkId, CancellationToken cancellationToken) =>
        db.MarketplaceVariantLinks.Local.Any(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalId && x.Id != exceptLinkId)
        || await db.MarketplaceVariantLinks.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalId && x.Id != exceptLinkId, cancellationToken);

    // Shopify answers a variant as a gid ("gid://shopify/ProductVariant/44791234567890"); the links keep the numeric id an
    // import stores. Anything else is not a Shopify variant id.
    internal static string? ShopifyVariantIdOrNull(string? value)
    {
        var id = value?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        const string prefix = "gid://shopify/ProductVariant/";
        if (id.StartsWith(prefix, StringComparison.Ordinal)) id = id[prefix.Length..];
        return id.Length > 0 && id.All(char.IsAsciiDigit) ? id : null;
    }

    // Hepsiburada addresses a variant by its hbSku (HBCV…). Links an earlier import keyed by the merchant SKU (the
    // listing's hepsiburadaSku was not read then) move to the hbSku, with the merchant SKU and barcode kept beside it
    // for order lines. Saved at once: the import then finds each link by its hbSku instead of adding a second one.
    private async Task RekeyHepsiburadaVariantLinks(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken)
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
    private async Task MarkVariantLinksSeen(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> page, CancellationToken cancellationToken, bool heal = true)
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
    private async Task MarkMissingVariantLinks(Guid tenantId, Guid connectionId, string platformCode, DateTimeOffset scanStartedAt, CancellationToken cancellationToken)
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

    // A price/stock answer for one line: a code the marketplace does not know breaks the link, and an explicit
    // acceptance heals one a refusal broke. A code saved by hand after the push was sent is left alone.
    private async Task RecordPushedVariantLinkHealth(Guid tenantId, Guid connectionId, IReadOnlyDictionary<Guid, string> rejected, IReadOnlyCollection<Guid> accepted, DateTimeOffset? sentAt, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var since = sentAt ?? now;
        var broken = 0;
        foreach (var (variantId, reason) in rejected)
        {
            var safeReason = Short(reason, 200);
            var target = db.MarketplaceVariantLinks
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId && (x.HealthChangedAt == null || x.HealthChangedAt < since));
            // A second refusal for an unknown code breaks the link; the first only makes it suspect (still linked).
            broken += await target.Where(x => x.HealthStatus == MarketplaceVariantLinkHealth.Suspect)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.Rejected)
                    .SetProperty(x => x.HealthReason, safeReason)
                    .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
            await target.Where(x => x.HealthStatus == null || x.HealthStatus == MarketplaceVariantLinkHealth.Unverified)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.Suspect)
                    .SetProperty(x => x.HealthReason, safeReason)
                    .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
        }
        if (accepted.Count > 0)
            await db.MarketplaceVariantLinks
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && accepted.Contains(x.VariantId)
                    && (x.HealthStatus == MarketplaceVariantLinkHealth.Rejected || x.HealthStatus == MarketplaceVariantLinkHealth.Suspect))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.HealthStatus, (string?)null)
                    .SetProperty(x => x.HealthReason, (string?)null)
                    .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
        if (broken > 0)
        {
            var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
            await RecordIssue(tenantId, $"variant-link-rejected:{connectionId}", "VARIANT_LINK_REJECTED",
                $"{MarketplacePlatformName(platformCode)}: {broken:N0} varyantın kodu fiyat-stok gönderiminde art arda iki kez pazaryerinde bulunamadı; bu varyantlara toplu fiyat ve stok gönderilmez. Kodu ürünün Varyant Eşleştirme penceresinden düzeltin.", cancellationToken);
        }
    }

    private static string MarketplacePlatformName(string? platformCode) => platformCode switch
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
    private async Task RecordVariantLinkCodes(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken, bool onlyAdded = false)
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

    // onlyPayloadHash: a create job writes only the variants it sent (those carrying its payload hash); a variant already
    // live from an earlier publication of the card keeps its state when only its refused siblings are sent again.
    private async Task<JobExecutionResult> MarkPublicationResult(Guid tenantId, Guid connectionId, ChannelListingProfile profile, string status, string? rejectionCode, JobExecutionResult result, CancellationToken cancellationToken, string? onlyPayloadHash = null, bool keepLiveStates = false)
    {
        profile.ActualStatus = status;
        profile.LastRejectionCode = SafeCode(rejectionCode);
        profile.Version++;
        await SetListingStatus(tenantId, connectionId, profile.Id, status, rejectionCode, cancellationToken, onlyPayloadHash, keepLiveStates);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    // A product update's outcome on the card and its listing rows; a variant on sale keeps its LIVE state, so its price
    // and stock keep going out (ListingSaleStatePolicy.AfterUpdate).
    private Task<JobExecutionResult> MarkUpdateResult(Guid tenantId, Guid connectionId, ChannelListingProfile profile, string status, string? rejectionCode, JobExecutionResult result, CancellationToken cancellationToken) =>
        MarkPublicationResult(tenantId, connectionId, profile, status, rejectionCode, result, cancellationToken, keepLiveStates: true);

    private async Task SetListingStatus(Guid tenantId, Guid connectionId, Guid profileId, string status, string? rejectionCode, CancellationToken cancellationToken, string? onlyPayloadHash = null, bool keepLiveStates = false)
    {
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profileId).ToListAsync(cancellationToken);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (onlyPayloadHash is not null)
            listings = listings.Where(listing => !states.TryGetValue(listing.VariantId, out var sent) || string.Equals(sent.PayloadHash, onlyPayloadHash, StringComparison.Ordinal)).ToList();
        foreach (var listing in listings)
        {
            listing.ActualStatus = status;
            listing.RejectionCode = SafeCode(rejectionCode);
            if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = keepLiveStates ? ListingSaleStatePolicy.AfterUpdate(state.ActualStatus, status) : status; state.LastRejectionCode = SafeCode(rejectionCode); state.Version++; }
        }
    }

    private static string? SafeCode(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(256, value.Trim().Length)];

    // A refused line's reasons as the marketplace wrote them: every failure reason (Trendyol), the line's status text
    // (n11, Pazarama) and its error code.
    private static string? LineReason(RemoteOperationLine line) =>
        line.Succeeded ? null : JobVariantResultPolicy.Reason([.. line.Reasons ?? [], line.Status, line.ErrorCode]);

    // Replaces the job's per-variant answer with this run's listing outcome; the caller's SaveChanges stores it. With a
    // publication hash, a listing another publication left (a live variant not sent again) is not this job's.
    private async Task RecordJobVariantResults(Guid tenantId, Guid jobId, string jobType, IEnumerable<ChannelListingVariant> listings, IReadOnlyDictionary<Guid, string?>? reasons, CancellationToken cancellationToken, string? payloadHash = null, IReadOnlyDictionary<Guid, MarketplaceListingState>? states = null, IReadOnlyDictionary<Guid, string>? notSent = null)
    {
        await db.JobVariantResults.Where(x => x.TenantId == tenantId && x.JobId == jobId).ExecuteDeleteAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listing in listings)
        {
            if (payloadHash is not null && states is not null && states.TryGetValue(listing.VariantId, out var state)
                && !string.Equals(state.PayloadHash, payloadHash, StringComparison.Ordinal)) continue;
            var barcode = (listing.ExternalBarcode ?? listing.ExternalSku)?.Trim();
            if (string.IsNullOrWhiteSpace(barcode) || !seen.Add(barcode)) continue;
            var notSentReason = notSent?.GetValueOrDefault(listing.VariantId);
            var status = notSentReason is not null ? JobVariantResultPolicy.NotSent : JobVariantResultPolicy.StatusFor(jobType, listing.ActualStatus);
            var reason = notSentReason is not null ? notSentReason
                : status is JobVariantResultPolicy.Rejected or JobVariantResultPolicy.Pending
                ? JobVariantResultPolicy.Reason([reasons?.GetValueOrDefault(listing.VariantId) ?? listing.RejectionCode])
                : null;
            db.JobVariantResults.Add(new JobVariantResult
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                JobId = jobId,
                VariantId = listing.VariantId,
                Barcode = barcode[..Math.Min(256, barcode.Length)],
                StockCode = SafeCode(listing.ExternalSku),
                Status = status,
                Reason = reason,
                RecordedAt = now
            });
        }
    }
}
