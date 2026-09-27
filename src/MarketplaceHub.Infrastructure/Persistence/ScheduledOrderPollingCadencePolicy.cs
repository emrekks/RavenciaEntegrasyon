namespace MarketplaceHub.Infrastructure.Persistence;

public static class ScheduledOrderPollingCadencePolicy
{
    public static (int IntervalSeconds, int JitterSeconds) ForPlatform(
        string platformCode,
        string resourceType,
        int intervalSeconds,
        int jitterSeconds)
    {
        if (string.Equals(resourceType, "ORDERS", StringComparison.Ordinal))
        {
            if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
                return (180, jitterSeconds);
            if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
                return (480, 30);
        }

        if (string.Equals(resourceType, "ORDER_LIFECYCLE", StringComparison.Ordinal))
        {
            if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
                return (180, jitterSeconds);
            if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
                return (480, jitterSeconds);
        }

        return (intervalSeconds, jitterSeconds);
    }
}
