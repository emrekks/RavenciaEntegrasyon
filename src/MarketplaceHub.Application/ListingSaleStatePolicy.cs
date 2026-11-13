namespace MarketplaceHub.Application;

// A variant's listing state ("LIVE") is what gates its price and stock sends (TrendyolWriteComposers reads only LIVE
// states). A product update, or the approval check that follows it, reports on the update: it does not take a product
// the marketplace sells off sale. Before this rule a refused, blocked or merely pending update moved every variant of
// the card out of LIVE, so no stock reached the marketplace until an approval check or an import set it back
// (scan A-1, 13 Nov 2026; PTT AVM already kept its listings as they were). The update's outcome stays visible on the
// card and the listing rows; only the state that drives the sends keeps LIVE.
public static class ListingSaleStatePolicy
{
    public const string Live = "LIVE";

    // The listing state after a product update writes `next` on it: a LIVE variant stays LIVE.
    public static string AfterUpdate(string? current, string next) =>
        string.Equals(current, Live, StringComparison.Ordinal) ? Live : next;

    // The listing state after an approval check that could not settle the card (deadline, unreadable answer, a partial
    // result) writes `next`: a variant already LIVE stays LIVE, since nothing showed it left sale. An identity conflict
    // (the marketplace's id belongs to another local variant) does take it out: its sends could reach the wrong product.
    public static string AfterUnsettledApproval(string? current, string next, string? rejectionCode) =>
        string.Equals(current, Live, StringComparison.Ordinal) && !IsIdentityConflict(rejectionCode) ? Live : next;

    // A Hepsiburada import result (trackingId lines) answers on the file that was sent, not on the listing: a variant
    // already LIVE whose update waits in review or was refused still sells with what it had.
    public static string AfterImportResult(string? current, string next) =>
        string.Equals(current, Live, StringComparison.Ordinal) && next is "APPROVAL_PENDING" or "REJECTED" ? Live : next;

    public static bool IsIdentityConflict(string? rejectionCode) =>
        rejectionCode?.Contains("IDENTITY_CONFLICT", StringComparison.Ordinal) == true;
}
