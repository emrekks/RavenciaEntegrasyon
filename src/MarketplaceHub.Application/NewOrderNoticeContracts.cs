using System.Text.Json;

namespace MarketplaceHub.Application;

// "Siparişlerde bildirim olarak gelsin" (owner, 27 Nov 2026): new orders reach the bell. A run of an order read (a
// scheduled read, a webhook delivery, a recovery scan, a single-order pull) that stored orders the panel did not have
// records one shared notification per store (connection) in the notification center (ops.panel_notifications, UserId
// null, server key "new-orders:"); every browser of the tenant pulls it into its bell (GET /notifications/new-orders).
// Never one per line or order: ten orders of one run are one notification ("10 paket").
//
// Not a new order for the bell: an order already older than MaximumOrderAge when the panel first stores it, one that
// arrives cancelled, every order of a first full import (the activation's İlk Çekim or a "full" read) and every order a
// run stores for a store that had no order before the run began (a new store's first read, a read after a reset).
public static class NewOrderNoticePolicy
{
    public const string KeyPrefix = "new-orders:";
    public const string Title = "Yeni Sipariş";
    public const string ActionLabel = "Siparişlere Git";
    public const string SingleActionLabel = "Siparişe Git";
    // The Siparişler page's Yeni tab (OrdersPage status=NEW).
    public const string NewTabHref = "/orders?status=NEW";
    public static readonly TimeSpan MaximumOrderAge = TimeSpan.FromHours(24);
    // The bell keeps 20 entries (shared/notifications.ts historyLimit); it is offered no more than that.
    public const int ListLimit = 20;

    // One notification per store and run: the run's own id keeps two runs apart, the store's keeps two stores apart.
    public static string ClientKey(Guid connectionId, Guid runId) => $"{KeyPrefix}{connectionId:N}:{runId:N}";

    // A first full import never raises notifications: the activation's bootstrap job and any read asked to read in full.
    public static bool RunRaisesNotices(bool bootstrapJob, string? payloadJson) => !bootstrapJob && !IsFullRead(payloadJson);

    public static bool IsEligible(DateTimeOffset orderedAt, string? derivedStatus, DateTimeOffset now) =>
        now - orderedAt.ToUniversalTime() <= MaximumOrderAge
        && !string.Equals(derivedStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase);

    // "Trendyol: 2 yeni paket"; a single order also names its number as the order list shows it. No " · " in it: the bell
    // shows a message up to its first " · " (notification-describe.ts presentNotification).
    public static string Message(string platformName, int packages, string? singleOrderNumber) =>
        string.IsNullOrWhiteSpace(singleOrderNumber)
            ? $"{platformName}: {Math.Max(1, packages)} yeni paket"
            : $"{platformName}: {Math.Max(1, packages)} yeni paket, sipariş {singleOrderNumber.Trim()}";

    // One order opens the list filtered on its number (the panel has no order detail route); several open the Yeni tab.
    public static string Href(string? singleOrderNumber) =>
        string.IsNullOrWhiteSpace(singleOrderNumber) ? NewTabHref : $"/orders?search={Uri.EscapeDataString(singleOrderNumber.Trim())}";

    private static bool IsFullRead(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
