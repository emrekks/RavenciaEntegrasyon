using System.Linq.Expressions;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-9: the Returns page's search read by the database. MatchesReturnSearch looks for the folded term
// (ReturnSearchText) in a card's shown fields: the claim id, the reason code and text, the order number and the customer
// name ("—" both without an order), the first line's barcode, and the card's lines' SKU, barcode and title. All but two
// are plain columns the database matches exactly (ReturnSearchInDatabase). The customer name (read from three snapshots by
// Customer) and a barcode the catalog lends a line without one are worked out only when the view is built: a claim the
// database's columns do not match is decided from its view, as before. The page's database pre-filter
// (ApplyReturnFilters) still applies in front of both, as it always did.
internal sealed partial class ReturnQueryService
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

    // The two fields the view works out, where the database knows them: the order's kept customer name
    // (Order.CustomerSearchName, OrderCustomerSearchNames) and the barcode a line without one shows from its own variant
    // (ResolveReturnVariant takes the line's VariantId first when that variant exists). True means MatchesReturnSearch
    // matches on the whole card.
    internal Expression<Func<ReturnClaim, bool>> ReturnSearchOnKeptFields(Guid tenantId, ReturnSearchTerm term)
    {
        var pattern = term.Pattern;
        var nameMatches = db.ReturnClaims.Where(other => other.TenantId == tenantId
            && db.Orders.Any(order => order.TenantId == tenantId && order.Id == other.OrderId && order.CustomerSearchName != null && Regex.IsMatch(order.CustomerSearchName, pattern))).Select(other => other.Id);
        var lentMatches = db.ReturnLines.Where(line => line.TenantId == tenantId && db.OrderLines.Any(orderLine => orderLine.TenantId == tenantId && orderLine.Id == line.OrderLineId
                && orderLine.Barcode == null && orderLine.VariantId != null
                && db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == orderLine.VariantId && variant.Barcode != null && Regex.IsMatch(variant.Barcode, pattern))))
            .Select(line => line.ClaimId);
        return claim => nameMatches.Contains(claim.Id) || lentMatches.Contains(claim.Id);
    }

    // The claims the database cannot decide: an order whose customer name is not kept yet, or (only when some variant of
    // the tenant has a barcode the term matches) a line without a barcode whose variant the catalog finds by other keys
    // (SKU, normalized keys, marketplace links), which only the view resolves.
    internal Expression<Func<ReturnClaim, bool>> ReturnSearchUndecided(Guid tenantId, bool lentBarcodeMayMatch)
    {
        var unnamed = db.ReturnClaims.Where(other => other.TenantId == tenantId
            && db.Orders.Any(order => order.TenantId == tenantId && order.Id == other.OrderId && order.CustomerSearchName == null)).Select(other => other.Id);
        if (!lentBarcodeMayMatch) return claim => unnamed.Contains(claim.Id);
        var unresolved = db.ReturnLines.Where(line => line.TenantId == tenantId && db.OrderLines.Any(orderLine => orderLine.TenantId == tenantId && orderLine.Id == line.OrderLineId
                && orderLine.Barcode == null && !(orderLine.VariantId != null && db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == orderLine.VariantId))))
            .Select(line => line.ClaimId);
        return claim => unnamed.Contains(claim.Id) || unresolved.Contains(claim.Id);
    }

    // Whether any variant of the tenant has a barcode the term matches: without one, no line can show a matching barcode
    // lent by the catalog.
    internal Task<bool> ReturnSearchLentBarcodeMayMatchAsync(Guid tenantId, ReturnSearchTerm term, CancellationToken cancellationToken)
    {
        var pattern = term.Pattern;
        return db.ProductVariants.AnyAsync(variant => variant.TenantId == tenantId && variant.Barcode != null && Regex.IsMatch(variant.Barcode, pattern), cancellationToken);
    }

    // At most this many claims are read view by view for one search; a broader one is refused with a request to narrow it.
    internal const int ReturnSearchViewLimit = 5000;

    internal static ArgumentException ReturnSearchTooBroad() =>
        new("İade araması çok geniş; aramayı daraltın (en fazla 5.000 iade tek tek incelenebilir).");

    private sealed record SplitSearchLine(Guid ReturnLineId, Guid ClaimId, Guid OrderLineId, string Sku, string? Barcode, string Title, Guid? VariantId, bool VariantExists, string? VariantBarcode);

    // The split claims' cards decided from their rows, as MatchesReturnSearch decides them on the view: the claim's fields,
    // the first line's barcode (PrimaryBarcode), and the card's own lines. Null for a claim only its view can decide (no
    // kept customer name, or a needed barcode the catalog lends by other keys while one may match).
    private async Task<Dictionary<Guid, Func<IReadOnlyCollection<Guid>, bool>?>> SplitCardSearchAsync(Guid tenantId, IReadOnlyDictionary<Guid, ReturnClaimRow> rows, ReturnSearchTerm term, bool lentBarcodeMayMatch, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, Func<IReadOnlyCollection<Guid>, bool>?>();
        if (rows.Count == 0) return result;
        var claimIds = rows.Keys.ToArray();
        var orderIds = rows.Values.Select(row => row.OrderId).Distinct().ToArray();
        var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id))
            .Select(x => new { x.Id, x.OrderNumber, x.CustomerSearchName }).ToDictionaryAsync(x => x.Id, cancellationToken);
        var lines = await (from line in db.ReturnLines.AsNoTracking()
                           where line.TenantId == tenantId && claimIds.Contains(line.ClaimId)
                           join orderLine in db.OrderLines.AsNoTracking() on new { line.TenantId, Id = line.OrderLineId } equals new { orderLine.TenantId, orderLine.Id }
                           orderby line.Id
                           select new SplitSearchLine(line.Id, line.ClaimId, orderLine.Id, orderLine.Sku, orderLine.Barcode, orderLine.TitleSnapshot, orderLine.VariantId,
                               orderLine.VariantId != null && db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == orderLine.VariantId),
                               db.ProductVariants.Where(variant => variant.TenantId == tenantId && variant.Id == orderLine.VariantId).Select(variant => variant.Barcode).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        // In the database's id order (the view's order; .NET orders Guids differently).
        var byClaim = lines.ToLookup(line => line.ClaimId);
        var needle = ReturnSearchText.Needle(term.Term);
        bool Has(string? value) => value is not null && ReturnSearchText.Fold(value).Contains(needle, StringComparison.Ordinal);
        foreach (var (claimId, row) in rows)
        {
            var order = orders.GetValueOrDefault(row.OrderId);
            if (order is not null && order.CustomerSearchName is null) { result[claimId] = null; continue; }
            var claimLines = byClaim[claimId].ToList();
            // A line's shown barcode: its own, else its own variant's; unknown (only the view resolves it) when the catalog
            // would find the variant by other keys and a lent barcode may match.
            bool? LineBarcodeMatches(SplitSearchLine line) =>
                line.Barcode is not null ? Has(line.Barcode)
                : line.VariantExists ? Has(line.VariantBarcode)
                : lentBarcodeMayMatch ? null : false;
            var first = claimLines.FirstOrDefault();
            var claimMatch = Has(row.ExternalClaimId) || Has(row.ReasonText) || Has(row.ReasonCode)
                || Has(order?.OrderNumber ?? "—") || Has(order?.CustomerSearchName ?? "—");
            var primary = first is null ? false : LineBarcodeMatches(first);
            if (!claimMatch && primary is null) { result[claimId] = null; continue; }
            var undecidedLines = claimLines.Where(line => LineBarcodeMatches(line) is null).Select(line => line.OrderLineId).ToHashSet();
            var matchingLines = claimLines.Where(line => Has(line.Sku) || Has(line.Title) || LineBarcodeMatches(line) == true).Select(line => line.OrderLineId).ToHashSet();
            if (claimMatch || primary == true) { result[claimId] = _ => true; continue; }
            // A card whose own lines leave a barcode unknown is decided by the view.
            if (undecidedLines.Count > 0) { result[claimId] = null; continue; }
            result[claimId] = cardLines => cardLines.Any(matchingLines.Contains);
        }
        return result;
    }
}
