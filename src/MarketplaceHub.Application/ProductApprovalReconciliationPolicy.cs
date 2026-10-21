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

    // How long a still-pending approval waits for its next check. Most approvals land within the first hour; one
    // that takes longer is usually waiting on a person at the marketplace, so the checks thin out (5 minutes,
    // then 15, then hourly, then every six hours) instead of reading every barcode every five minutes for a week.
    // The last check is never planned past the deadline, which closes the reconciliation.
    public static TimeSpan PendingRecheckDelay(DateTimeOffset startedAt, DateTimeOffset deadlineAt, DateTimeOffset now)
    {
        var pendingFor = now - startedAt;
        var delay = pendingFor < TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(5)
            : pendingFor < TimeSpan.FromHours(6) ? TimeSpan.FromMinutes(15)
            : pendingFor < TimeSpan.FromHours(24) ? TimeSpan.FromHours(1)
            : TimeSpan.FromHours(6);
        var untilDeadline = deadlineAt - now + TimeSpan.FromMinutes(1);
        return untilDeadline < delay ? (untilDeadline > TimeSpan.FromMinutes(1) ? untilDeadline : TimeSpan.FromMinutes(1)) : delay;
    }

    // A variant this reconciliation already found approved (listed LIVE, with its product and variant links) is not
    // read again on later checks: only the barcodes still waiting are asked about.
    public static bool IsSettledLive(string? listingStatus, bool hasProductLink, bool hasVariantLink) =>
        string.Equals(listingStatus, "LIVE", StringComparison.Ordinal) && hasProductLink && hasVariantLink;
}
