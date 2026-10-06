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
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
            && resourceType is "ORDERS" or "ORDER_LIFECYCLE")
        {
            return (180, jitterSeconds);
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
