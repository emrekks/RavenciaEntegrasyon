using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The issue an automatic price/stock send leaves for an offer it cannot send (price 0, another currency, a code the
// marketplace cannot take), one per connection and variant; the next send that carries the offer closes it.
internal static class PriceInventoryOfferIssues
{
    public const string KeyPrefix = "stock-projection-offer-invalid:";

    public static string Key(Guid connectionId, Guid variantId) => $"{KeyPrefix}{connectionId:N}:{variantId:N}";

    public static string ConnectionPrefix(Guid connectionId) => $"{KeyPrefix}{connectionId:N}:";

    public static bool IsOfferProblem(string? code) => PriceStockOfferIssueCodes.IsOfferProblem(code);

    // The open offer issues of one connection: the monitor's InvalidOffers and the invalid-offers list count the same rows.
    public static IQueryable<OperationalIssue> Open(AppDbContext db, Guid tenantId, string prefix) =>
        db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(prefix));

    // The variant of a key written by Key(); null for a key that does not end in one.
    public static Guid? VariantOf(string dedupeKey) =>
        Guid.TryParseExact(dedupeKey[(dedupeKey.LastIndexOf(':') + 1)..], "N", out var variantId) ? variantId : null;
}

// Owner, 24 Nov 2026 ("bunların bir süre sonra temizlenmesi lazım, sorun yok işaretleme gibi bir şey"): an offer issue the
// owner marks "Sorun Yok" in İşlem Takibi is resolved, and the summary keeps the values the send judged when it was
// marked (the reason code, the offer's currency, list and sale price) as a short fingerprint. The automatic send does not
// open it again while those values are the same; once any of them changes and the offer is still refused, it opens again
// with the send's own summary (IssueRecorder writes the summary anew, the mark goes with it). The mark changes panel
// records only: nothing is sent, no price or offer is changed.
internal static class OfferIssueNoProblem
{
    public const string AuditAction = "OFFER_ISSUE_MARKED_NO_PROBLEM";
    public const string MarkStart = " · Sorun Yok olarak işaretlendi [değerler: ";

    // codes: for a reason other than the price check, the marketplace codes that check looked at (CheckedCodesAsync).
    public static string Fingerprint(string code, string? currency, decimal? listPrice, decimal? salePrice, string? codes = null) =>
        TextValues.Sha256Hex($"{code}|{currency?.Trim().ToUpperInvariant() ?? "-"}|{Price(listPrice)}|{Price(salePrice)}{(codes is null ? "" : "|" + codes)}")[..16];

    // The fingerprint of the values the send judges now for this offer and reason; offerFound is false without an offer row.
    public static async Task<(string Fingerprint, bool OfferFound)> CurrentAsync(AppDbContext db, Guid tenantId, Guid connectionId, Guid variantId, string code, CancellationToken cancellationToken)
    {
        var offer = await db.ChannelOffers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId)
            .Select(x => new { x.Currency, x.ListPrice, x.SalePrice })
            .FirstOrDefaultAsync(cancellationToken);
        var codes = await CheckedCodesAsync(db, tenantId, connectionId, variantId, code, cancellationToken);
        return (Fingerprint(code, offer?.Currency, offer?.ListPrice, offer?.SalePrice, codes), offer is not null);
    }

    // Review of 24 Nov 2026: the link and barcode checks judge codes, not prices. HEPSIBURADA_LISTING_LINK_REQUIRED looks at
    // the merchant SKU and hbSku (the variant link's codes, the panel SKU, the listing row's codes); PAZARAMA_BARCODE_INVALID
    // at the barcode the link holds. Their mark keeps those, so a changed code that is still refused opens it again.
    // Null for the price check (its fingerprint stays the prices alone).
    private static async Task<string?> CheckedCodesAsync(AppDbContext db, Guid tenantId, Guid connectionId, Guid variantId, string code, CancellationToken cancellationToken)
    {
        if (code == PriceStockOfferIssueCodes.ChannelOfferInvalid) return null;
        var link = await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.ExternalId, x.ExternalSku, x.ExternalBarcode })
            .FirstOrDefaultAsync(cancellationToken);
        var values = new List<string?> { link?.ExternalId, link?.ExternalSku, link?.ExternalBarcode };
        if (code == PriceStockOfferIssueCodes.HepsiburadaListingLinkRequired)
        {
            values.Add(await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == variantId).Select(x => x.Sku).FirstOrDefaultAsync(cancellationToken));
            var listing = await (from profile in db.ChannelListingProfiles.AsNoTracking()
                                 join listingVariant in db.ChannelListingVariants.AsNoTracking() on new { profile.TenantId, ProfileId = profile.Id } equals new { listingVariant.TenantId, listingVariant.ProfileId }
                                 where profile.TenantId == tenantId && profile.ConnectionId == connectionId && listingVariant.VariantId == variantId
                                 orderby listingVariant.Id
                                 select new { listingVariant.ExternalSku, listingVariant.ExternalBarcode }).FirstOrDefaultAsync(cancellationToken);
            values.Add(listing?.ExternalSku);
            values.Add(listing?.ExternalBarcode);
        }
        return string.Join("|", values.Select(value => value?.Trim() ?? "-"));
    }

    private static string Price(decimal? value) => value?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    public static string Mark(string fingerprint) => $"{MarkStart}{fingerprint}]";

    // The fingerprint a "Sorun Yok" mark left in the summary; null when the summary carries none.
    public static string? FingerprintOf(string summary)
    {
        var start = summary.LastIndexOf(MarkStart, StringComparison.Ordinal);
        if (start < 0) return null;
        start += MarkStart.Length;
        var end = summary.IndexOf(']', start);
        return end > start ? summary[start..end] : null;
    }

    // Whether the owner marked this resolved issue "Sorun Yok" on the very values the send refuses now.
    public static async Task<bool> StillMarkedAsync(AppDbContext db, OperationalIssue issue, Guid connectionId, Guid variantId, string code, CancellationToken cancellationToken)
    {
        if (issue.Status != IssueStatus.Resolved || issue.TenantId is not { } tenantId || FingerprintOf(issue.Summary) is not { } marked) return false;
        var (current, offerFound) = await CurrentAsync(db, tenantId, connectionId, variantId, code, cancellationToken);
        return offerFound && marked == current;
    }
}

// Owner decisions A and B: the gate of the automatic price/stock writes and the baseline taken when it opens.
internal static class AutomaticPriceStockWrites
{
    // The connections among the given ones whose automatic price/stock writes are open: the stored master switch on
    // and both write policies switched on (a policy without a row is off). The global switch is the worker's own
    // check; the maintenance pass clears the stored switch when it is off.
    public static async Task<HashSet<Guid>> OpenConnectionIdsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken)
    {
        if (connectionIds.Count == 0) return [];
        var ids = connectionIds.Distinct().ToArray();
        var settings = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.Id))
            .Select(x => new { x.Id, x.SettingsJson })
            .ToListAsync(cancellationToken);
        var policies = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId)
                && (x.ResourceType == MarketplaceExternalWritePolicies.Price || x.ResourceType == MarketplaceExternalWritePolicies.Stock))
            .Select(x => new { x.ConnectionId, x.ResourceType, x.Enabled })
            .ToListAsync(cancellationToken);
        return settings
            .Where(connection => AutomaticPriceStockWriteGate.IsOpen(
                StockProjectionOutbox.StoredExternalWritesEnabled(connection.SettingsJson),
                policies.FirstOrDefault(x => x.ConnectionId == connection.Id && x.ResourceType == MarketplaceExternalWritePolicies.Price)?.Enabled,
                policies.FirstOrDefault(x => x.ConnectionId == connection.Id && x.ResourceType == MarketplaceExternalWritePolicies.Stock)?.Enabled))
            .Select(connection => connection.Id)
            .ToHashSet();
    }

    public static async Task<bool> IsOpenAsync(AppDbContext db, Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        (await OpenConnectionIdsAsync(db, tenantId, [connectionId], cancellationToken)).Contains(connectionId);

    public static bool IsAutomaticWritePolicy(string resourceType) =>
        resourceType is MarketplaceExternalWritePolicies.Price or MarketplaceExternalWritePolicies.Stock;

    // Saves the caller's changes, with the baseline of B taken over the connection's offers when markBaseline is set.
    // The offers carry a concurrency token, and anything else saving them at the same moment (the other switch of the
    // pair from an older panel, a send, an import) made the whole save fail with CONCURRENCY_CONFLICT (18 Nov). An offer
    // conflict is not the user's: the offers are read again and the baseline taken again, a few times; a conflict on
    // anything else (the policy itself) still fails, so a stale If-Match never slips through.
    // Owner rule of 18 Nov: switching the writes on also opens the connection's missing offers at the panel price
    // (PanelPriceOffers), marked as sent like every other offer, in the same save: nothing goes out when it opens. An
    // offer another request opened meanwhile (the unique index refuses the second) is read as there on the next attempt.
    public static async Task<PanelPriceOffers.Counts?> SaveWithBaselineAsync(AppDbContext db, Guid tenantId, Guid connectionId, string platformCode, bool markBaseline, CancellationToken cancellationToken, Guid? actorUserId = null, DateTimeOffset? now = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            PanelPriceOffers.Counts? opened = null;
            if (markBaseline)
            {
                opened = await PanelPriceOffers.OpenAsync(db, tenantId, connectionId, platformCode, actorUserId, now ?? DateTimeOffset.UtcNow, cancellationToken);
                await MarkCurrentValuesSentAsync(db, tenantId, connectionId, platformCode, cancellationToken);
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return opened;
            }
            catch (DbUpdateConcurrencyException conflict) when (markBaseline && attempt < 3 && conflict.Entries.Count > 0 && conflict.Entries.All(entry => entry.Entity is ChannelOffer))
            {
                DetachOfferWork(db);
            }
            catch (DbUpdateException conflict) when (markBaseline && attempt < 3 && PanelPriceOffers.IsOfferUniqueViolation(conflict))
            {
                DetachOfferWork(db);
            }
        }
    }

    // The offers (and what an opening added beside them) are read and opened again on the next attempt.
    private static void DetachOfferWork(AppDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<ChannelOffer>().ToList()) entry.State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<ChannelPriceHistory>().Where(entry => entry.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<AuditLog>().Where(entry => entry.State == EntityState.Added && entry.Entity.Action == PanelPriceOffers.AuditAction).ToList()) entry.State = EntityState.Detached;
    }

    // B: every offer of the connection counts as sent at its current values (the price mark each platform's build
    // computes and the variant's current stock version), so the changes made while the writes were off are never sent;
    // only what changes after this moment goes out. The offers are changed in the caller's unit of work.
    public static async Task<int> MarkCurrentValuesSentAsync(AppDbContext db, Guid tenantId, Guid connectionId, string platformCode, CancellationToken cancellationToken)
    {
        // Two queries: an AsNoTracking anywhere in one query would leave the offers untracked and the marks unsaved.
        var offers = await db.ChannelOffers.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).ToListAsync(cancellationToken);
        var variantIds = offers.Select(x => x.VariantId).Distinct().ToArray();
        var stockVersions = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
            .Select(x => new { x.VariantId, x.ProjectionVersion })
            .ToDictionaryAsync(x => x.VariantId, x => x.ProjectionVersion, cancellationToken);
        var changed = 0;
        foreach (var row in offers.Select(offer => new { Offer = offer, ProjectionVersion = stockVersions.TryGetValue(offer.VariantId, out var version) ? version : (long?)null }))
        {
            var priceHash = PriceInventoryComposer.CurrentPriceHash(platformCode, row.Offer);
            var stockVersion = row.ProjectionVersion ?? row.Offer.LastStockProjectionVersion;
            if (row.Offer.LastPriceHash == priceHash && row.Offer.LastStockProjectionVersion == stockVersion && row.Offer.LastSentCode is null) continue;
            row.Offer.LastPriceHash = priceHash;
            row.Offer.LastStockProjectionVersion = stockVersion;
            // The values the marketplace last confirmed are unknown from here on: the baseline is a decision not to send,
            // not a confirmation. Cleared, the first change after enabling is sent for every field it changed (Trendyol
            // compares a changed field with the confirmed value, PriceInventorySendPolicy.TrendyolFields), and a value
            // equal to an old confirmation is never taken as already held by the marketplace.
            // A value known without a code (read from the marketplace, or held before a panel price pulled the stored price)
            // is no confirmation and stays: Pazarama compares with it.
            var known = row.Offer.LastSentCode is null ? (row.Offer.LastSentSalePrice, row.Offer.LastSentListPrice) : (null, null);
            PriceInventorySendPolicy.ClearSent(row.Offer);
            (row.Offer.LastSentSalePrice, row.Offer.LastSentListPrice) = known;
            row.Offer.Version++;
            changed++;
        }
        return changed;
    }
}
