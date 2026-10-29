namespace MarketplaceHub.Application;

public static class ProductApprovalReconciliationPolicy
{
    public const string SplitApprovedContentsCode = "PRODUCT_APPROVAL_CONTENT_SPLIT";

    // The check window ran out while the marketplace kept the product pending or did not list it at all (Pazarama,
    // n11 and PTT AVM answer a just-sent product as not found while it waits); the card says how long it waited.
    public static string DeadlineExpiredMessage(string marketplaceName, DateTimeOffset startedAt, DateTimeOffset deadlineAt)
    {
        var days = Math.Max(1, (int)Math.Round((deadlineAt - startedAt).TotalDays, MidpointRounding.AwayFromZero));
        return $"{marketplaceName} ürün onayı {days} gün içinde sonuçlanmadı; ürün bu sürede onaylanmadı ya da {marketplaceName} listelerinde bulunamadı. Barkodu {marketplaceName} satıcı panelinden kontrol edip yayın durumunu yeniden sorgulayın.";
    }

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

    // How long a still-pending approval waits for its next check, after 30 days of live approvals (29.10.2026): n11
    // and Shopify settle at the first check, Hepsiburada within 5-10 minutes, Trendyol in about 2 hours, Pazarama in
    // hours to days (a person reviews it). So the first quarter hour is checked every 5 minutes, the first hour every
    // 15, the first six hours every 30 (a Trendyol approval shows within half an hour), the first two days every 2 hours
    // (a Pazarama review often ends on the second day) and then every 6: about 25 checks on the first day instead of 50.
    // The card's refresh still checks at once. The last check is never planned past the deadline, which closes the
    // reconciliation.
    public static TimeSpan PendingRecheckDelay(DateTimeOffset startedAt, DateTimeOffset deadlineAt, DateTimeOffset now)
    {
        var pendingFor = now - startedAt;
        var delay = pendingFor < TimeSpan.FromMinutes(15) ? TimeSpan.FromMinutes(5)
            : pendingFor < TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(15)
            : pendingFor < TimeSpan.FromHours(6) ? TimeSpan.FromMinutes(30)
            : pendingFor < TimeSpan.FromHours(48) ? TimeSpan.FromHours(2)
            : TimeSpan.FromHours(6);
        var untilDeadline = deadlineAt - now + TimeSpan.FromMinutes(1);
        return untilDeadline < delay ? (untilDeadline > TimeSpan.FromMinutes(1) ? untilDeadline : TimeSpan.FromMinutes(1)) : delay;
    }

    // How many list requests one walk of a marketplace's approved and unapproved lists may take to answer a
    // connection's waiting barcodes, or 0 when asking each barcode on its own is cheaper. Asked one at a time, every
    // waiting barcode costs at least one request; the walk needs about one page of 100 per hundred products the
    // panel knows on the connection (linked ones on the approved list, unsettled ones on the unapproved list) plus
    // the last page of each list and one page-0 probe. The walk is used only when that estimate is below the
    // per-barcode cost and is stopped at that cost, so a store with more products than the panel knows can at worst
    // spend one round's per-barcode cost on the walk before the barcodes it did not reach are asked on their own.
    public static int ListWalkBudget(int waitingBarcodes, int linkedVariants, int unsettledListings)
    {
        if (waitingBarcodes < 2) return 0;
        var estimate = (Math.Max(0, linkedVariants) + 99) / 100 + (Math.Max(0, unsettledListings) + 99) / 100 + 3;
        return estimate < waitingBarcodes ? waitingBarcodes : 0;
    }

    // A variant this reconciliation already found approved (listed LIVE, with its product and variant links) is not
    // read again on later checks: only the barcodes still waiting are asked about.
    public static bool IsSettledLive(string? listingStatus, bool hasProductLink, bool hasVariantLink) =>
        string.Equals(listingStatus, "LIVE", StringComparison.Ordinal) && hasProductLink && hasVariantLink;
}
