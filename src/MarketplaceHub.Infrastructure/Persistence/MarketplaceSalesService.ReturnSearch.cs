using System.Linq.Expressions;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-9: the Returns page's search read by the database. MatchesReturnSearch looks for the folded term
// (ReturnSearchText) in a card's shown fields: the claim id, the reason code and text, the order number and the customer
// name ("—" both without an order), the first line's barcode, and the card's lines' SKU, barcode and title. All but two
// are plain columns the database matches exactly (ReturnSearchInDatabase). The customer name (read from three snapshots by
// Customer) and a barcode the catalog lends a line without one are worked out only when the view is built: a claim the
// database's columns do not match is decided from its view, as before. The page's database pre-filter
// (ApplyReturnFilters) still applies in front of both, as it always did.
public sealed partial class MarketplaceSalesService
{
    // The term the page searches for, ready for the database: the pattern (null when the term cannot be sent: a lone
    // surrogate or a NUL) and whether the "—" an order-less claim shows matches it.
    internal sealed record ReturnSearchTerm(string Term, string Pattern, bool DashMatches);

    internal static ReturnSearchTerm? ReturnSearchTermOf(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var needle = ReturnSearchText.Needle(search);
        var pattern = ReturnSearchText.SearchPattern(needle);
        return pattern is null ? null : new(search, pattern, ReturnSearchText.Fold("—").Contains(needle, StringComparison.Ordinal));
    }

    // A whole card's search on the columns alone (every line of the claim counts): true means MatchesReturnSearch matches;
    // false leaves the customer name and the catalog's barcodes to the view.
    internal Expression<Func<ReturnClaim, bool>> ReturnSearchInDatabase(Guid tenantId, ReturnSearchTerm term)
    {
        var pattern = term.Pattern;
        var dash = term.DashMatches;
        // The order and line matches are sets of claim ids read once each (an uncorrelated IN over the tenant's claims and
        // return lines), never a sub-query per claim: the patterns then run once per claim's order and per return line
        // whatever plan the database picks (a correlated NOT EXISTS ran them once per claim and line pair on a tenant
        // without statistics, minutes for 600 claims).
        var withOrder = db.ReturnClaims.Where(other => other.TenantId == tenantId && db.Orders.Any(order => order.TenantId == tenantId && order.Id == other.OrderId)).Select(other => other.Id);
        var orderMatches = db.ReturnClaims.Where(other => other.TenantId == tenantId
            && db.Orders.Any(order => order.TenantId == tenantId && order.Id == other.OrderId && Regex.IsMatch(order.OrderNumber, pattern))).Select(other => other.Id);
        var lineMatches = db.ReturnLines.Where(line => line.TenantId == tenantId && db.OrderLines.Any(orderLine => orderLine.TenantId == tenantId && orderLine.Id == line.OrderLineId
                && (Regex.IsMatch(orderLine.Sku, pattern) || orderLine.Barcode != null && Regex.IsMatch(orderLine.Barcode, pattern) || Regex.IsMatch(orderLine.TitleSnapshot, pattern))))
            .Select(line => line.ClaimId);
        return claim => Regex.IsMatch(claim.ExternalClaimId, pattern)
            || claim.ReasonText != null && Regex.IsMatch(claim.ReasonText, pattern)
            || claim.ReasonCode != null && Regex.IsMatch(claim.ReasonCode, pattern)
            || (withOrder.Contains(claim.Id) ? orderMatches.Contains(claim.Id) : dash)
            || lineMatches.Contains(claim.Id);
    }
}
