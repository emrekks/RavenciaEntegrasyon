namespace MarketplaceHub.Application;

public static class InvoiceWorkspacePackagePolicy
{
    public static bool ShouldInclude(string? createdBy, bool hasLineAllocations, int packagesForOrder) =>
        !string.Equals(createdBy, "HEPSIBURADA_STATUS_FEED", StringComparison.Ordinal)
        || hasLineAllocations
        || packagesForOrder == 1;

    public static bool ShouldIncludeTrendyolPaymentPlaceholder(string? platformCode, string? externalPackageId, bool hasRealPackageForOrder) =>
        !string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
        || !string.Equals(externalPackageId, "0", StringComparison.Ordinal)
        || !hasRealPackageForOrder;
}
