namespace MarketplaceHub.Infrastructure.Persistence;

public static class ScheduledOrderPollingCadencePolicy
{
    public const int MaximumIntervalSeconds = 259_200;
    // A test (stage) connection has no customers; it is read every ten minutes at most, so it neither
    // spends the marketplace's test quota nor takes the worker lane from the production connection.
    public const int StageMinimumIntervalSeconds = 600;

    // Owner decision (30 Oct 2026): the order reads that no notification replaces run every two minutes at most, so a
    // new order takes its stock off the panel within about two minutes. Measured per pass on live: Hepsiburada 2 requests
    // for new orders and 22 for the open-order scan (limit 600/min), N11 1 (600/min), Pazarama 1 (60/min), Shopify 1
    // (2/s). Trendyol too (owner, same day: "2 yapalım onu da diğerleri gibi"): its order endpoints share 30 requests a
    // minute, 40% (12) kept for new-order reads and user actions; a new-order pass takes 4 requests, so two minutes is
    // 2 a minute, and the week before showed no rate-limit refusal. Trendyol, Hepsiburada and Shopify read at this pace
    // only while their notifications are unhealthy; healthy notifications stretch the read to the 8-minute safety net.
    public const int UnnotifiedOrderIntervalSeconds = 120;

    public static int ForEnvironment(int intervalSeconds, string? environment) =>
        string.Equals(environment, "STAGE", StringComparison.OrdinalIgnoreCase) ? Math.Max(intervalSeconds, StageMinimumIntervalSeconds) : intervalSeconds;

    public static (int IntervalSeconds, int JitterSeconds) ForPlatform(
        string platformCode,
        string resourceType,
        int intervalSeconds,
        int jitterSeconds)
    {
        // The Trendyol order stream brings every status change; the open-package scan is a
        // half-hourly safety net that reads the packages fifty at a time.
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase) && resourceType == "ORDER_LIFECYCLE")
        {
            return (1800, jitterSeconds);
        }

        // Packages delivered without cargo details are rare; checking them every half hour is enough.
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase) && resourceType == "ORDER_CARGO_INFO")
        {
            return (1800, jitterSeconds);
        }

        // Trendyol and n11 reconciliation re-read whole windows, so the 1- and 3-day passes run hourly
        // and every six hours; the 90-day pass stays daily. Trendyol return reconciliation follows suit.
        if (platformCode?.ToUpperInvariant() is "TRENDYOL" or "N11" && resourceType is "ORDER_RECONCILE_SHORT" or "ORDER_RECONCILE_MEDIUM"
            || string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase) && resourceType is "RETURN_RECONCILE_SHORT" or "RETURN_RECONCILE_MEDIUM")
        {
            return (resourceType.EndsWith("_SHORT", StringComparison.Ordinal) ? 3600 : 21_600, jitterSeconds);
        }

        // The incremental return reads already bring status changes; the open-return scans of
        // Trendyol and Hepsiburada are a half-hourly safety net.
        if (platformCode?.ToUpperInvariant() is "TRENDYOL" or "HEPSIBURADA" && resourceType == "RETURN_LIFECYCLE")
        {
            return (1800, jitterSeconds);
        }

        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
            && IsShopifyReadPolicy(resourceType))
        {
            // New and changed orders every two minutes (read by updated_at); the open-order scan
            // half-hourly; recovery hourly and incremental; reconciliation daily.
            var interval = resourceType switch
            {
                "ORDERS" => UnnotifiedOrderIntervalSeconds,
                "ORDER_LIFECYCLE" => 1800,
                "ORDER_RECOVERY" => 3600,
                _ => Math.Clamp(intervalSeconds, 30, MaximumIntervalSeconds / 3) * 3
            };
            var jitter = resourceType == "ORDERS" ? 30 : jitterSeconds;
            return (interval, jitter);
        }

        // PTT AVM documents no rate limit and answers a whole order-date window per request: new orders
        // every two minutes, the open-order status scan every quarter hour.
        if (string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase) && resourceType is "ORDERS" or "ORDER_LIFECYCLE")
        {
            return (resourceType == "ORDERS" ? 120 : 900, Math.Min(jitterSeconds, 30));
        }

        // New orders of Trendyol, Hepsiburada, N11 and Pazarama every two minutes at most; Hepsiburada's and Pazarama's
        // open-order scans bring cancellations (stock back to the panel) and keep the same pace. A shorter configured
        // cadence stays.
        if (platformCode?.ToUpperInvariant() is "TRENDYOL" or "HEPSIBURADA" or "N11" or "PAZARAMA" && resourceType == "ORDERS"
            || platformCode?.ToUpperInvariant() is "HEPSIBURADA" or "PAZARAMA" && resourceType == "ORDER_LIFECYCLE")
        {
            return (Math.Min(intervalSeconds, UnnotifiedOrderIntervalSeconds), jitterSeconds);
        }

        // Hepsiburada invoice state is read from order details and the missing-invoice list. The
        // batch covers every open Hepsiburada order at today's volume, and invoices sent from the
        // panel are recorded when they are delivered, so a half-hourly pass is enough.
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            && resourceType == "ORDER_INVOICE_RECONCILIATION"
            && intervalSeconds is 900 or 300)
        {
            return (1800, Math.Min(jitterSeconds, 10));
        }

        return (intervalSeconds, jitterSeconds);
    }

    private static bool IsShopifyReadPolicy(string resourceType) => resourceType is
        "ORDERS"
        or "ORDER_RECOVERY"
        or "ORDER_LIFECYCLE"
        or "ORDER_RECONCILE_SHORT"
        or "ORDER_RECONCILE_MEDIUM"
        or "ORDER_RECONCILE_DAILY";
}
