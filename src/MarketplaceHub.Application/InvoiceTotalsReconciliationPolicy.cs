using System.Globalization;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// The local checks of an invoice's totals: its lines add up to the payable total, and the tax-exclusive total plus the
// tax equals it.
public static class InvoiceTotalsReconciliationPolicy
{
    public static IReadOnlyList<ReconciliationDifferenceView> LocalDifferences(Invoice invoice, IReadOnlyCollection<InvoiceLine> lines)
    {
        var differences = new List<ReconciliationDifferenceView>();
        var calculated = lines.Sum(x => x.LineTotal);
        if (calculated != invoice.PayableTotal)
            differences.Add(new("INVOICE", invoice.Id.ToString("D"), "PAYABLE_TOTAL", Money(invoice.PayableTotal), Money(calculated), "LOCAL_REVIEW_REQUIRED"));
        // TaxExclusiveTotal is already net of discount (see InvoiceAmounts.TryCalculatePackage), so the discount is not subtracted again.
        if (invoice.TaxExclusiveTotal + invoice.TaxTotal != invoice.PayableTotal)
            differences.Add(new("INVOICE", invoice.Id.ToString("D"), "TOTAL_EQUATION", Money(invoice.PayableTotal), Money(invoice.TaxExclusiveTotal + invoice.TaxTotal), "LOCAL_REVIEW_REQUIRED"));
        return differences;
    }

    private static string Money(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
