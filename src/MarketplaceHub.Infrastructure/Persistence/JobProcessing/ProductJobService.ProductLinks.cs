using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class ProductJobService
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

    internal static string? SafeCode(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(256, value.Trim().Length)];

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
