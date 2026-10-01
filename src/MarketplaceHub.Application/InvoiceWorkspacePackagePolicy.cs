namespace MarketplaceHub.Application;

public static class InvoiceWorkspacePackagePolicy
{
    public static bool ShouldInclude(string? createdBy, bool hasLineAllocations) =>
        !string.Equals(createdBy, "HEPSIBURADA_STATUS_FEED", StringComparison.Ordinal)
        || hasLineAllocations;
}
