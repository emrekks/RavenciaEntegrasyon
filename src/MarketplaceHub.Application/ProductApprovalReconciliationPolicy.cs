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

    // A barcode the marketplace's own batch result reported as created (SUCCESS, recorded SENT on the create job) exists
    // there, whatever its product lists say a few minutes later: live 5.11.2026 Trendyol listed MZ021Y-CD-34..36 as
    // pending at 00:59, on neither list at 01:03; the reset sent them again and Trendyol refused the barcodes as already
    // present. So "not found" resets a card only when no barcode of it was confirmed created; a confirmed card stays
    // pending until the check's deadline, which hands it to review.
    public static bool ShouldResetMissingPublication(int expectedVariantCount, IReadOnlyCollection<RemotePublicationStatus> statuses, bool createConfirmed) =>
        !createConfirmed
        && ShouldResetMissingPublication(expectedVariantCount, statuses);

    // Shopify answers a SKU through its search (`sku:` query), whose index lags behind a product just created: a fresh
    // product reads "not found" for a while. Scan C-15 (13 Nov 2026): that answer reset the card at the first check
    // (queued with no delay) and dropped its links, so the next send opened a second product. Shopify's first check now
    // waits a few minutes, and "not found" resets a card only once the search has had its grace and no SKU of the card
    // was confirmed created (the same protection Trendyol has).
    public static readonly TimeSpan ShopifyFirstCheckDelay = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ShopifySearchGrace = TimeSpan.FromMinutes(15);

    public static bool ShouldResetMissingShopifyPublication(int expectedVariantCount, IReadOnlyCollection<RemotePublicationStatus> statuses, bool createConfirmed, DateTimeOffset checkStartedAt, DateTimeOffset now) =>
        now - checkStartedAt >= ShopifySearchGrace
        && ShouldResetMissingPublication(expectedVariantCount, statuses, createConfirmed);

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
    // hours to days (a person reviews it). So the first hour is checked every 15 minutes, the first six hours every 30 (a
    // Trendyol approval shows within half an hour), the first two days every 2 hours (a Pazarama review often ends on
    // the second day) and then every 6: about 23 checks on the first day instead of 50. The card's refresh still checks
    // at once. The last check is never planned past the deadline, which closes the reconciliation.
    public static TimeSpan PendingRecheckDelay(DateTimeOffset startedAt, DateTimeOffset deadlineAt, DateTimeOffset now)
    {
        var pendingFor = now - startedAt;
        var delay = pendingFor < TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(15)
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

    // A barcode whose create result was unknown (the result named no line for it, or Pazarama accepted the create without
    // ids) and that the marketplace's lists still do not show after this long goes to review: it may never have gone in
    // (live: 6 sizes sent, 1 opened, the other 5 waited as "Onay Bekliyor" for the whole 7-day check).
    public const string UnconfirmedNotFoundCode = "PRODUCT_CREATE_NOT_FOUND";
    public static readonly TimeSpan UnconfirmedNotFoundLimit = TimeSpan.FromHours(24);

    // The local status of a barcode the marketplace answers NOT_FOUND for. One whose create was confirmed waits for
    // approval as before (n11, Pazarama and PTT AVM list a just-sent product late). One whose create result is unknown
    // stays unknown (never taken as awaiting approval) until UnconfirmedNotFoundLimit after the check began, then goes
    // to review.
    public static string NotFoundStatus(string? listingStatus, DateTimeOffset checkStartedAt, DateTimeOffset now) =>
        !string.Equals(listingStatus, ProductBatchResultPolicy.ResultUnknownStatus, StringComparison.Ordinal) ? "APPROVAL_PENDING"
        : now - checkStartedAt >= UnconfirmedNotFoundLimit ? "MANUAL_REVIEW"
        : ProductBatchResultPolicy.ResultUnknownStatus;

    // A variant this reconciliation already found approved (listed LIVE, with its product and variant links) is not
    // read again on later checks: only the barcodes still waiting are asked about.
    public static bool IsSettledLive(string? listingStatus, bool hasProductLink, bool hasVariantLink) =>
        string.Equals(listingStatus, "LIVE", StringComparison.Ordinal) && hasProductLink && hasVariantLink;
}
