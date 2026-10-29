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

    // The products list's platform filter over a family's variants on one marketplace.
    public static bool MatchesFilter(string platformState, int variantCount, int matchedVariantCount, int missingVariantCount) => platformState switch
    {
        "ACTIVE" => variantCount > 0 && matchedVariantCount == variantCount,
        "PARTIAL" => matchedVariantCount > 0 && matchedVariantCount < variantCount,
        NotFound => matchedVariantCount == 0 && missingVariantCount > 0,
        "PASSIVE" => matchedVariantCount == 0 && missingVariantCount == 0,
        _ => true
    };

    private static bool IsArchived(string? profileStatus) => string.Equals(profileStatus?.Trim(), "ARCHIVED", StringComparison.OrdinalIgnoreCase);
}
