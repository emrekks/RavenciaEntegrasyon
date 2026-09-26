namespace MarketplaceHub.Application;

public static class ProductApprovalReconciliationPolicy
{
    public const string SplitApprovedContentsCode = "PRODUCT_APPROVAL_CONTENT_SPLIT";

    public static bool ShouldResetMissingPublication(int expectedVariantCount, IReadOnlyCollection<RemotePublicationStatus> statuses) =>
        expectedVariantCount > 0
        && statuses.Count == expectedVariantCount
        && statuses.All(status => string.Equals(status.Status?.Trim(), "NOT_FOUND", StringComparison.OrdinalIgnoreCase));

    public static bool HasSplitApprovedContents(IEnumerable<RemotePublicationStatus> statuses) => statuses
        .Where(status => string.Equals(status.Status?.Trim(), "APPROVED", StringComparison.OrdinalIgnoreCase))
        .Select(status => status.ExternalProductId?.Trim())
        .Where(externalProductId => !string.IsNullOrWhiteSpace(externalProductId))
        .Distinct(StringComparer.Ordinal)
        .Take(2)
        .Count() > 1;

    public static bool RequiresContentSplitReview(string? statusCode) =>
        string.Equals(statusCode?.Trim(), SplitApprovedContentsCode, StringComparison.OrdinalIgnoreCase);
}
