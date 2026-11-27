using System.Text.Json;

namespace MarketplaceHub.Application;

// "Siparişlerde bildirim olarak gelsin" (owner, 27 Nov 2026): new orders reach the bell. A run of an order read (a
// scheduled read, a webhook delivery, a recovery scan, a single-order pull) in which orders became actionable records one
// shared notification per store (connection) in the notification center (ops.panel_notifications, UserId null, server
// key "new-orders:"); every browser of the tenant pulls it into its bell (GET /notifications/new-orders). Never one per
// line or order: ten orders of one run are one notification ("10 yeni paket").
//
// An order is announced once: the first time it is stored in an actionable state, or the first time it leaves
// PAYMENT_PENDING (Trendyol's awaiting-payment order whose real package arrived), decided from its stored status before
// and after the read (BecameActionable). Awaiting payment, cancelled or already shipped is never announced. Not announced
// either: an order already older than MaximumOrderAge, every order of a first full import (the activation's İlk Çekim or
// a "full" read), and every order stored while the store's order read has not completed once since its cursor was
// (re)created (CaughtUpCursor without LastSuccessAt: a new store, or the sliced catch-up after Tam Sıfırlama or a
// deleted connection).
//
// The number is the count of those orders, worded "paket" like the Siparişler › Yeni card, which counts the orders with
// a package in Yeni and labels them "Paket".
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

    // The order read whose completed run marks a store as caught up (MarketplaceJobProcessor.TelemetryResource of every
    // marketplace's order sync). A reset or a deleted connection removes it; a run's slices do not move LastSuccessAt.
    public const string CaughtUpCursor = "ORDERS_HOT";

    public const string PaymentPending = "PAYMENT_PENDING";

    // Statuses in which an order is not (or no longer) work for the seller: never announced.
    private static readonly HashSet<string> NotActionable = new(StringComparer.OrdinalIgnoreCase)
    {
        PaymentPending, "CANCELLED", "SHIPPED", "DELIVERED", "UNDELIVERED", "RETURN_IN_TRANSIT", "RETURNED"
    };

    public static bool IsActionable(string? derivedStatus) => !string.IsNullOrWhiteSpace(derivedStatus) && !NotActionable.Contains(derivedStatus.Trim());

    // previous: the stored status before this read, null for an order this read inserted. True once per order: when it is
    // inserted actionable, or when it first leaves awaiting payment for an actionable status.
    public static bool BecameActionable(string? previous, string? current) =>
        IsActionable(current) && (previous is null || string.Equals(previous.Trim(), PaymentPending, StringComparison.OrdinalIgnoreCase));

    public static bool IsEligible(DateTimeOffset orderedAt, string? derivedStatus, DateTimeOffset now) =>
        now - orderedAt.ToUniversalTime() <= MaximumOrderAge && IsActionable(derivedStatus);

    // "Trendyol: 2 yeni paket"; a single order also names its number as the order list shows it. No " · " in it: the bell
    // shows a message up to its first " · " (notification-describe.ts presentNotification).
    public static string Message(string platformName, int orders, string? singleOrderNumber) =>
        string.IsNullOrWhiteSpace(singleOrderNumber)
            ? $"{platformName}: {Math.Max(1, orders)} yeni paket"
            : $"{platformName}: 1 yeni paket, sipariş {singleOrderNumber.Trim()}";

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
