namespace MarketplaceHub.Infrastructure.Persistence;

public static class ScheduledOrderPollingCadencePolicy
{
    public const int MaximumIntervalSeconds = 259_200;

    public static (int IntervalSeconds, int JitterSeconds) ForPlatform(
        string platformCode,
        string resourceType,
        int intervalSeconds,
        int jitterSeconds)
    {
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase) && resourceType == "ORDERS")
        {
            return (180, jitterSeconds);
        }

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

        // Trendyol reconciliation re-reads whole stream windows, so the 1- and 3-day passes run
        // hourly and every six hours; the 90-day pass stays daily.
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase) && resourceType is "ORDER_RECONCILE_SHORT" or "ORDER_RECONCILE_MEDIUM")
        {
            return (resourceType == "ORDER_RECONCILE_SHORT" ? 3600 : 21_600, jitterSeconds);
        }

        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
            && IsShopifyReadPolicy(resourceType))
        {
            var interval = resourceType switch
            {
                "ORDERS" or "ORDER_LIFECYCLE" => 540,
                _ => Math.Clamp(intervalSeconds, 30, MaximumIntervalSeconds / 3) * 3
            };
            var jitter = resourceType == "ORDERS" ? 30 : jitterSeconds;
            return (interval, jitter);
        }

        // Hepsiburada invoice state is read from order details, not from the
        // ordinary order stream. A 15 minute interval leaves delivered orders
        // stale while a rotating batch waits to reach them, so keep that read
        // reconciliation on a five minute default cadence.
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            && resourceType == "ORDER_INVOICE_RECONCILIATION"
            && intervalSeconds == 900)
        {
            return (300, Math.Min(jitterSeconds, 10));
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
