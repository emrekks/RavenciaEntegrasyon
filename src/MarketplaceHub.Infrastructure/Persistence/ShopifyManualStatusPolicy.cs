using System.Security.Cryptography;
using System.Text;

namespace MarketplaceHub.Infrastructure.Persistence;

// A panel status set on a Shopify order (deep audit 2026-10-20 S20). Its history row names the
// marketplace status the order had at that moment and the request key; the panel status holds
// while the marketplace status stays that one, so a webhook that only touches the order (a note,
// a tag) no longer reverts it, and the same request sent again is recognised.
public static class ShopifyManualStatusPolicy
{
    public const string RawStatusPrefix = "MANUAL_SHOPIFY_STATUS:";
    private const string SourcePrefix = "manual:shopify-status:";

    public static string KeySuffix(string idempotencyKey) =>
        ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey.Trim())))[..32];

    public static string SourceEventId(string remoteStatus, string idempotencyKey) =>
        SourcePrefix + remoteStatus + KeySuffix(idempotencyKey);

    // The marketplace status recorded with a manual status; null for rows written before it was recorded.
    public static string? RemoteStatusAt(string sourceEventId)
    {
        if (!sourceEventId.StartsWith(SourcePrefix, StringComparison.Ordinal)) return null;
        var parts = sourceEventId[SourcePrefix.Length..].Split(':');
        return parts.Length == 2 && parts[1].Length == 32 && parts[0].Length > 0 && parts[0].All(character => char.IsAsciiLetterUpper(character) || character == '_') ? parts[0] : null;
    }

    public static bool StillHolds(DateTimeOffset manualAt, string? remoteStatusAtManual, DateTimeOffset lastRemoteModifiedAt, string remoteStatusNow) =>
        manualAt >= lastRemoteModifiedAt
        || remoteStatusAtManual is not null && string.Equals(remoteStatusAtManual, remoteStatusNow, StringComparison.Ordinal);
}
