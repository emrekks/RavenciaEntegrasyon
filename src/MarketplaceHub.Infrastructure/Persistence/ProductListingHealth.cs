using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// Where a product stands on one connection once the variant links' health is applied: a variant the marketplace no
// longer knows (its link NOT_FOUND or REJECTED, see MarketplaceVariantLinkHealth) is not published there. A SUSPECT or
// UNVERIFIED link, and a variant with no link at all, change nothing: only an explicit broken health counts. The
// status is computed for the views and the products filter; no listing table is written.
internal static class ProductListingHealth
{
    // Every variant that was matched or live on the connection is missing there now.
    public const string NotFound = "NOT_FOUND";

    private static readonly HashSet<string> LiveStatuses = new(StringComparer.Ordinal) { "LIVE", "PARTIAL_LIVE", "ACTIVE", "ON_SALE" };
    private static readonly string[] ListedProfileStatuses = ["LIVE", "PARTIAL_LIVE", "ACTIVE", "ON_SALE"];

    // "Bulunamadı" is said only of a listing known to have been on the marketplace: a code a scan or lookup saw there
    // (LastSeenAt), or a card whose publication there went live. A broken link never known there (a code typed by hand
    // that the marketplace does not hold) is no listing at all: the card reads as not published, not as gone.
    public static IQueryable<MarketplaceVariantLink> KnownOnMarketplace(this IQueryable<MarketplaceVariantLink> links, AppDbContext db, Guid tenantId) =>
        links.Where(link => (link.HealthStatus != MarketplaceVariantLinkHealth.NotFound && link.HealthStatus != MarketplaceVariantLinkHealth.Rejected)
            || link.LastSeenAt != null
            || db.ChannelListingProfiles.Any(profile => profile.TenantId == tenantId && profile.ConnectionId == link.ConnectionId && ListedProfileStatuses.Contains(profile.ActualStatus)
                && db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == link.VariantId && variant.ProductId == profile.ProductId)));

    // The statuses a missing variant may replace. An in-progress send, a refusal or an archive keeps its own status:
    // the card then tells what the panel is doing, not what the last scan saw.
    private static readonly HashSet<string> ReplaceableStatuses = new(StringComparer.Ordinal) { "LIVE", "PARTIAL_LIVE", "ACTIVE", "ON_SALE", "UNKNOWN", "LINKED", "PARTIAL_LINKED" };

    // A product archived in the panel, or a listing archived on the marketplace, is not looked for there: its links
    // keep their old meaning and it never reads "Bulunamadı".
    public static bool CountsAsMissing(string? linkHealthStatus, bool productArchived, string? profileStatus) =>
        MarketplaceVariantLinkHealth.IsBroken(linkHealthStatus) && !productArchived && !IsArchived(profileStatus);

    public static string Status(string? profileStatus, int matchedVariantCount, int missingVariantCount, int variantCount)
    {
        var current = profileStatus ?? (matchedVariantCount == variantCount && variantCount > 0 ? "LINKED" : matchedVariantCount > 0 ? "PARTIAL_LINKED" : "UNLINKED");
        if (missingVariantCount <= 0) return current;
        var normalized = profileStatus?.Trim().ToUpperInvariant();
        if (normalized is not null && !ReplaceableStatuses.Contains(normalized)) return current;
        if (matchedVariantCount <= 0) return NotFound;
        return normalized is not null && LiveStatuses.Contains(normalized) ? "PARTIAL_LIVE" : "PARTIAL_LINKED";
    }

    // The products filter's "Satışa Kapalı": a matched variant the marketplace closed to sale (ListingSaleState) for a
    // reason other than a stock of 0. The sale state never changes Status, so price/stock eligibility stays as it was.
    public const string OffSale = "OFF_SALE";

    // The products filter's "Hatalı" and "Onay Bekliyor" (owner, 14 Nov 2026): a card whose publication the marketplace
    // refused or that failed (the products list's red mark: product-platform-status.ts errorStatuses), or one it still
    // reviews.
    public const string Error = "ERROR";
    public const string Waiting = "APPROVAL";
    public static readonly IReadOnlyCollection<string> ErrorStatuses =
    [
        "REJECTED", "PARTIAL_REJECTED", "CREATE_REJECTED", "UPDATE_REJECTED", "PARTIAL_FAILURE", "UPDATE_PARTIAL_FAILURE",
        "ARCHIVE_REJECTED", "ARCHIVE_PARTIAL_FAILURE", "UPDATE_BLOCKED", "BLOCKED", "MANUAL_REVIEW", "LOCKED", "BLACKLISTED"
    ];
    public static readonly IReadOnlyCollection<string> WaitingStatuses = ["APPROVAL_PENDING", "APPROVAL_PARTIAL_PENDING"];

    // A card whose product update is on its way: queued (UPDATE_QUEUED), sent in one of its phases and waiting for the
    // marketplace's batch result (UPDATE_{UNAPPROVED|CONTENT|VARIANTS|DELIVERY}_SUBMITTED, PTT AVM's UPDATE_SUBMITTED) or
    // between phases (UPDATE_IN_PROGRESS). Only the update job writes these on the profile. A refused variant of such a
    // card was sent again and waits for the marketplace's answer ("Onaya Gönderildi"): it is neither "Reddedildi" nor
    // "Hatalı" until that answer comes (owner, 15 Nov 2026: MZ056 Kahverengi/Mürdüm).
    public static readonly IReadOnlyCollection<string> UpdateInFlightStatuses =
    [
        "UPDATE_QUEUED", "UPDATE_SUBMITTED", "UPDATE_UNAPPROVED_SUBMITTED", "UPDATE_CONTENT_SUBMITTED", "UPDATE_VARIANTS_SUBMITTED",
        "UPDATE_DELIVERY_SUBMITTED", "UPDATE_IN_PROGRESS"
    ];
    public static bool IsUpdateInFlight(string? profileStatus) => profileStatus is not null && UpdateInFlightStatuses.Contains(profileStatus.Trim().ToUpperInvariant());

    // The products list's platform filter over a family's variants on one marketplace.
    public static bool MatchesFilter(string platformState, int variantCount, int matchedVariantCount, int missingVariantCount, int offSaleVariantCount = 0) => platformState switch
    {
        "ACTIVE" => variantCount > 0 && matchedVariantCount == variantCount,
        "PARTIAL" => matchedVariantCount > 0 && matchedVariantCount < variantCount,
        NotFound => matchedVariantCount == 0 && missingVariantCount > 0,
        "PASSIVE" => matchedVariantCount == 0 && missingVariantCount == 0,
        OffSale => offSaleVariantCount > 0,
        _ => true
    };

    private static bool IsArchived(string? profileStatus) => string.Equals(profileStatus?.Trim(), "ARCHIVED", StringComparison.OrdinalIgnoreCase);
}
