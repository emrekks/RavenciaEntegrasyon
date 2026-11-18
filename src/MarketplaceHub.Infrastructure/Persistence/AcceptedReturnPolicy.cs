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

    // The long s, the one letter outside ASCII the invariant upper case turns into an ASCII letter (S): Canonicalize reads
    // "CLOſED" as CLOSED, so the word patterns take it for S.
    private const char LongS = 'ſ';

    // FATURA-FINANS-2: Canonicalize keeps every letter and digit (char.IsLetterOrDigit), not only the ASCII ones, so a
    // status with any other letter or digit in it ("ACCEPTEDé", "ACCEPTEDı") is no known word: it reads as unknown and
    // follows its claim. Every such character of the Basic Multilingual Plane is listed here (\uXXXX ranges, which .NET
    // and PostgreSQL both read; the long s aside). Characters beyond it are surrogate pairs to Canonicalize, never letters.
    internal static readonly string ForeignLetterPattern = ForeignLetters();

    private static string ForeignLetters()
    {
        var ranges = new System.Text.StringBuilder("[");
        int? start = null;
        for (var code = 0x80; code <= 0x10000; code++)
        {
            var listed = code <= 0xFFFF && code != LongS && char.IsLetterOrDigit((char)code);
            if (listed && start is null) start = code;
            if (listed || start is not { } first) continue;
            ranges.Append(first == code - 1 ? $"\\u{first:X4}" : $"\\u{first:X4}-\\u{code - 1:X4}");
            start = null;
        }
        return ranges.Append(']').ToString();
    }

    // Canonicalize keeps letters and digits only and ignores case, so a word matches with anything else before, after or
    // between its letters ("Waiting_In_Action", "accepted "). Matched, not stripped and compared: Npgsql writes
    // Regex.Replace as a regexp_replace that replaces the first match only. A letter outside ASCII is never skipped (the
    // long s stands for S; any other one makes the status unknown, ForeignLetterPattern).
    private static string WordPattern(IEnumerable<string> words)
    {
        var other = $"[^A-Za-z0-9{LongS}]*";
        static string Letter(char letter) => letter == 'S' ? $"[Ss{LongS}]" : letter.ToString();
        return $"^{other}(?:{string.Join("|", words.Select(word => string.Join(other, word.Select(Letter))))}){other}$";
    }

    /// <summary>The lines of the tenant's return claims that can take units back, with their claim's status.</summary>
    public static IQueryable<ClaimLine> ClaimLines(AppDbContext db, Guid tenantId) =>
        from line in db.ReturnLines.AsNoTracking()
        join claim in db.ReturnClaims.AsNoTracking() on new { line.TenantId, Id = line.ClaimId } equals new { claim.TenantId, claim.Id }
        where claim.TenantId == tenantId && claim.Status != ReturnClaimStatus.Cancelled
            // An exchange is no return: the customer keeps a product and the sale goes on (owner, 6 Nov 2026). Only the
            // stored request says so; a claim that does not say is a return as before. An exchange claim the marketplace
            // closed with a refund (raw status Refunded) is a return.
            && (claim.CustomerRequest != ReturnCustomerRequestPolicy.Change || claim.RawStatus.ToUpper() == ReturnCustomerRequestPolicy.RefundedRawStatus
                // Only Hepsiburada's (ReturnCustomerRequestPolicy.ExchangeNoReturnPlatform): a Trendyol exchange counts as before.
                || !db.PlatformConnections.Any(connection => connection.TenantId == claim.TenantId && connection.Id == claim.ConnectionId
                    && connection.PlatformCode == ReturnCustomerRequestPolicy.ExchangeNoReturnPlatform))
        select new ClaimLine { OrderId = claim.OrderId, OrderLineId = line.OrderLineId, Quantity = line.Quantity, RawStatus = line.RawStatus, ClaimStatus = claim.Status };

    /// <summary>Whether a claim line takes its units back: the invoice's rule.</summary>
    public static bool LineCounts(ReturnClaimStatus claimStatus, string? lineRawStatus) =>
        ReturnDecisionLinePolicy.StockDispositionAllowed(claimStatus, lineRawStatus);

    /// <summary>LineCounts as a database condition.</summary>
    // A status with a letter outside ASCII is unknown: the line follows its claim. The long list is written once (a
    // conditional), as the reports repeat this condition in their statements.
    public static readonly Expression<Func<ClaimLine, bool>> LineCountsInDatabase = row =>
        Regex.IsMatch(row.RawStatus ?? "", ForeignLetterPattern)
            ? row.ClaimStatus == ReturnClaimStatus.Approved || row.ClaimStatus == ReturnClaimStatus.Completed
            : Regex.IsMatch(row.RawStatus ?? "", AcceptedLinePattern, RegexOptions.IgnoreCase)
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
