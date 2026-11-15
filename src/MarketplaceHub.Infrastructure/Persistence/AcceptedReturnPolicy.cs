using System.Linq.Expressions;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// The one rule for a unit taken back by an accepted return (owner, 15 Nov 2026: the invoice's rule; an invoice is a legal
/// document, so a report counts a unit as returned exactly when the invoice side does). A claim counts unless it was
/// cancelled or is an exchange the marketplace did not close with a refund; a line of it counts when
/// ReturnDecisionLinePolicy.StockDispositionAllowed says so. The invoice (InvoiceSaleReturns) decides the lines in memory
/// with that function; the overview and the reports (DashboardReadService.AcceptedReturnQuantities) decide them in the
/// database with LineCountsInDatabase, which is the same function written as SQL (AcceptedReturnRuleParityTests).
/// </summary>
internal static class AcceptedReturnPolicy
{
    // Canonicalize's words: a line read as approved or completed counts on any claim; a line read as rejected, cancelled or
    // waiting for the seller never does; any other line (in transit, disputed, unknown, no status) follows its claim.
    internal static readonly string AcceptedLinePattern = WordPattern(MarketplaceReturnStatus.WordsOf(ReturnClaimStatus.Approved, ReturnClaimStatus.Completed));
    internal static readonly string RefusedLinePattern = WordPattern(MarketplaceReturnStatus.WordsOf(ReturnClaimStatus.Rejected, ReturnClaimStatus.Cancelled, ReturnClaimStatus.ActionRequired));

    // Canonicalize keeps letters and digits only and ignores case, so a word matches with anything else before, after or
    // between its letters ("Waiting_In_Action", "accepted "). Matched, not stripped and compared: Npgsql writes
    // Regex.Replace as a regexp_replace that replaces the first match only. The marketplaces' status words are ASCII; a
    // word with another alphabet's letter in it reads as unknown in both.
    private static string WordPattern(IEnumerable<string> words)
    {
        const string Other = "[^A-Za-z0-9]*";
        return $"^{Other}(?:{string.Join("|", words.Select(word => string.Join(Other, word.ToCharArray())))}){Other}$";
    }

    /// <summary>The lines of the tenant's return claims that can take units back, with their claim's status.</summary>
    public static IQueryable<ClaimLine> ClaimLines(AppDbContext db, Guid tenantId) =>
        from line in db.ReturnLines.AsNoTracking()
        join claim in db.ReturnClaims.AsNoTracking() on new { line.TenantId, Id = line.ClaimId } equals new { claim.TenantId, claim.Id }
        where claim.TenantId == tenantId && claim.Status != ReturnClaimStatus.Cancelled
            // An exchange is no return: the customer keeps a product and the sale goes on (owner, 6 Nov 2026). Only the
            // stored request says so; a claim that does not say is a return as before. An exchange claim the marketplace
            // closed with a refund (raw status Refunded) is a return.
            && (claim.CustomerRequest != ReturnCustomerRequestPolicy.Change || claim.RawStatus.ToUpper() == ReturnCustomerRequestPolicy.RefundedRawStatus)
        select new ClaimLine { OrderId = claim.OrderId, OrderLineId = line.OrderLineId, Quantity = line.Quantity, RawStatus = line.RawStatus, ClaimStatus = claim.Status };

    /// <summary>Whether a claim line takes its units back: the invoice's rule.</summary>
    public static bool LineCounts(ReturnClaimStatus claimStatus, string? lineRawStatus) =>
        ReturnDecisionLinePolicy.StockDispositionAllowed(claimStatus, lineRawStatus);

    /// <summary>LineCounts as a database condition.</summary>
    public static readonly Expression<Func<ClaimLine, bool>> LineCountsInDatabase = row =>
        Regex.IsMatch(row.RawStatus ?? "", AcceptedLinePattern, RegexOptions.IgnoreCase)
        || (row.ClaimStatus == ReturnClaimStatus.Approved || row.ClaimStatus == ReturnClaimStatus.Completed)
            && !Regex.IsMatch(row.RawStatus ?? "", RefusedLinePattern, RegexOptions.IgnoreCase);

    internal sealed class ClaimLine
    {
        public Guid OrderId { get; init; }
        public Guid OrderLineId { get; init; }
        public decimal Quantity { get; init; }
        public string? RawStatus { get; init; }
        public ReturnClaimStatus ClaimStatus { get; init; }
    }
}
