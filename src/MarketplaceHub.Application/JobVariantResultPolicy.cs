namespace MarketplaceHub.Application;

// How a publication job's per-variant outcome is named. A create or update job sends variants (SENT) or has them
// refused (REJECTED); an approval check finds them APPROVED, still PENDING or REJECTED. A variant of the product the
// job did not carry is NOT_SENT.
public static class JobVariantResultPolicy
{
    public const string Sent = "SENT";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Pending = "PENDING";
    public const string NotSent = "NOT_SENT";

    public const int ReasonMaxLength = 2000;

    // A variant already live from an earlier publication is left out when only its refused siblings are sent again.
    public const string AlreadyLiveReason = "Daha önceki bir yayınla zaten yayında; bu gönderime alınmadı.";

    // A variant the marketplace already holds (matched there, e.g. by an import) is left out the same way.
    public const string AlreadyOnMarketplaceReason = "Pazaryerinde zaten var (eşleşmiş); bu gönderime alınmadı.";

    public static bool AppliesTo(string jobType) => jobType is MarketplaceJobTypes.ProductCreate or MarketplaceJobTypes.ProductUpdate or MarketplaceJobTypes.ProductApprovalReconcile;

    // The outcome a listing's status stands for in a job of this type.
    public static string StatusFor(string jobType, string listingStatus)
    {
        var status = listingStatus.Trim().ToUpperInvariant();
        if (status is "CREATE_REJECTED" or "UPDATE_REJECTED" or "REJECTED" or "BLACKLISTED" or "LOCKED" or "ARCHIVED") return Rejected;
        if (jobType == MarketplaceJobTypes.ProductApprovalReconcile) return status == "LIVE" ? Approved : Pending;
        // The batch answered without this variant, or the job stopped before an answer: its outcome is not known yet.
        if (status is ProductBatchResultPolicy.ResultUnknownStatus or "MANUAL_REVIEW") return Pending;
        if (status == "BLOCKED") return Rejected;
        // A send's answer is accepted or refused; approval comes later (a card already live counts as sent too).
        return Sent;
    }

    // An older job without recorded rows, read from the card's current listing state of the same publication: a send
    // knows only its own refusal (a later approval refusal is not the send's answer).
    public static string StatusFromCardState(string jobType, string listingStatus)
    {
        var status = listingStatus.Trim().ToUpperInvariant();
        return jobType switch
        {
            MarketplaceJobTypes.ProductCreate => status == "CREATE_REJECTED" ? Rejected : status is ProductBatchResultPolicy.ResultUnknownStatus or "MANUAL_REVIEW" ? Pending : Sent,
            MarketplaceJobTypes.ProductUpdate => status == "UPDATE_REJECTED" ? Rejected : Sent,
            _ => StatusFor(jobType, status)
        };
    }

    // The marketplace's reasons for a refused line, verbatim and joined; null when it gave none.
    public static string? Reason(IEnumerable<string?> reasons)
    {
        var parts = reasons.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (parts.Count == 0) return null;
        var text = string.Join(" · ", parts);
        return text.Length <= ReasonMaxLength ? text : text[..(ReasonMaxLength - 1)] + "…";
    }

    // "Beden: L · Renk: Bordo" from the variant's options in their order.
    public static string? OptionsText(IEnumerable<(int SortOrder, string Label, string Value)> options)
    {
        var parts = options.OrderBy(x => x.SortOrder).ThenBy(x => x.Label, StringComparer.Ordinal)
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{x.Label.Trim()}: {x.Value.Trim()}")
            .ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
