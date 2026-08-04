namespace MarketplaceHub.Infrastructure.Persistence;

public static class LocalReconciliationPolicy
{
    public static bool Supports(string platformCode) => string.Equals(platformCode, "TRENDYOL", StringComparison.Ordinal);
}
