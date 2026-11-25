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
    // Canonicalize's words: a line read as approved or completed counts on any claim; a line with any other status of its
    // own (rejected, waiting, in transit, disputed, unknown) never does; only a line without a status follows its claim
    // (scan 25 Nov 2026 C-1).
    internal static readonly string AcceptedLinePattern = WordPattern(MarketplaceReturnStatus.WordsOf(ReturnClaimStatus.Approved, ReturnClaimStatus.Completed));

    // No status of its own: string.IsNullOrWhiteSpace, every white-space character of the Basic Multilingual Plane listed
    // (char.IsWhiteSpace), as PostgreSQL's \s follows its own locale.
    internal static readonly string BlankLinePattern = $"^{CharacterClass(0, char.IsWhiteSpace)}*$";

    // The long s, the one letter outside ASCII the invariant upper case turns into an ASCII letter (S): Canonicalize reads
    // "CLOſED" as CLOSED, so the word patterns take it for S.
    private const char LongS = 'ſ';

    // FATURA-FINANS-2: Canonicalize keeps every letter and digit (char.IsLetterOrDigit), not only the ASCII ones, so a
    // status with any other letter or digit in it ("ACCEPTEDé", "ACCEPTEDı") is no known word: it reads as unknown and
    // counts nothing. Every such character of the Basic Multilingual Plane is listed here (\uXXXX ranges, which .NET
    // and PostgreSQL both read; the long s aside). Characters beyond it are surrogate pairs to Canonicalize, never letters.
    internal static readonly string ForeignLetterPattern = CharacterClass(0x80, character => character != LongS && char.IsLetterOrDigit(character));

    private static string CharacterClass(int from, Func<char, bool> listedCharacter)
    {
        var ranges = new System.Text.StringBuilder("[");
        int? start = null;
        for (var code = from; code <= 0x10000; code++)
        {
            var listed = code <= 0xFFFF && listedCharacter((char)code);
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
        join claim in db.ReturnClaims.AsNoTracking().Where(CountsAsReturnInDatabase(db)) on new { line.TenantId, Id = line.ClaimId } equals new { claim.TenantId, claim.Id }
        where claim.TenantId == tenantId && claim.Status != ReturnClaimStatus.Cancelled
        select new ClaimLine { OrderId = claim.OrderId, OrderLineId = line.OrderLineId, Quantity = line.Quantity, RawStatus = line.RawStatus, ClaimStatus = claim.Status };

    // The claim's raw status Refunded, trimmed of what string.Trim strips and in any case, as CountsAsReturn compares it.
    internal static readonly string RefundedClaimPattern = ReturnSearchText.TrimmedIgnoreCasePattern(ReturnCustomerRequestPolicy.RefundedRawStatus);

    /// <summary>
    /// ReturnCustomerRequestPolicy.CountsAsReturn as a database condition, the one copy the invoice (InvoiceSaleReturns,
    /// through ClaimLines), the overview (DashboardReadService.AcceptedReturnQuantities) and the reports (ReportsService
    /// claim rows) read. An exchange is no return: the customer keeps a product and the sale goes on (owner, 6 Nov 2026).
    /// How the claim closed decides first: refunded (raw status Refunded) or ResolvedWith REFUND is a return, ResolvedWith
    /// CHANGE is none (scan 25 Nov 2026 C-2). A claim that does not say how it closed counts unless it is a Hepsiburada
    /// exchange request (ExchangeNoReturnPlatform; a Trendyol exchange request counts as before, review 18 Nov 2026).
    /// </summary>
    public static Expression<Func<ReturnClaim, bool>> CountsAsReturnInDatabase(AppDbContext db) => claim =>
        Regex.IsMatch(claim.RawStatus, RefundedClaimPattern)
        || claim.ResolvedWith != ReturnCustomerRequestPolicy.Change
            && (claim.ResolvedWith == ReturnCustomerRequestPolicy.Refund || claim.CustomerRequest != ReturnCustomerRequestPolicy.Change
                || !db.PlatformConnections.Any(connection => connection.TenantId == claim.TenantId && connection.Id == claim.ConnectionId
                    && connection.PlatformCode == ReturnCustomerRequestPolicy.ExchangeNoReturnPlatform));

    /// <summary>Whether a claim line takes its units back: the invoice's rule.</summary>
    public static bool LineCounts(ReturnClaimStatus claimStatus, string? lineRawStatus) =>
        ReturnDecisionLinePolicy.StockDispositionAllowed(claimStatus, lineRawStatus);

    /// <summary>LineCounts as a database condition.</summary>
    // A line without a status follows its claim; a status with a letter outside ASCII is unknown and counts nothing.
    public static readonly Expression<Func<ClaimLine, bool>> LineCountsInDatabase = row =>
        Regex.IsMatch(row.RawStatus ?? "", BlankLinePattern)
            ? row.ClaimStatus == ReturnClaimStatus.Approved || row.ClaimStatus == ReturnClaimStatus.Completed
            : !Regex.IsMatch(row.RawStatus ?? "", ForeignLetterPattern) && Regex.IsMatch(row.RawStatus ?? "", AcceptedLinePattern, RegexOptions.IgnoreCase);

    internal sealed class ClaimLine
    {
        public Guid OrderId { get; init; }
        public Guid OrderLineId { get; init; }
        public decimal Quantity { get; init; }
        public string? RawStatus { get; init; }
        public ReturnClaimStatus ClaimStatus { get; init; }
    }
}
