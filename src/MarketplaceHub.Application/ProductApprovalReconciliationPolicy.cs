namespace MarketplaceHub.Application;

public static class ProductApprovalReconciliationPolicy
{
    public static bool ShouldResetMissingPublication(int expectedVariantCount, IReadOnlyCollection<RemotePublicationStatus> statuses) =>
        expectedVariantCount > 0
        && statuses.Count == expectedVariantCount
        && statuses.All(status => string.Equals(status.Status?.Trim(), "NOT_FOUND", StringComparison.OrdinalIgnoreCase));
}
