namespace MarketplaceHub.Application;

public enum InvoiceReturnScope
{
    None,
    // Some of the invoiced goods were returned: the invoice stays and is still delivered; the returned lines need a
    // return document ("İade Belgesi Gerekli").
    Partial,
    // Every invoiced line was returned in full: the invoice is adjusted as before (AdjustmentRequired, e-Arşiv
    // cancellation or a return invoice), and its delivery stops.
    Full
}

/// <summary>
/// How much of an issued sale invoice came back through approved returns (owner's rule of 5 Nov 2026): a partial
/// return never blocks the invoice's delivery nor forces a cancellation; it is a separate "to do" on the invoice.
/// </summary>
public static class InvoiceReturnAdjustmentPolicy
{
    public const string ReturnDocumentRequiredCode = "INVOICE_RETURN_DOCUMENT_REQUIRED";

    /// <param name="invoicedQuantities">The invoice's quantity per order line (its lines that name one).</param>
    /// <param name="returnedQuantities">The approved returned quantity per order line.</param>
    /// <param name="hasUnmappedLines">The invoice has lines that name no order line: it is never judged returned in full.</param>
    public static InvoiceReturnScope Classify(IReadOnlyDictionary<Guid, decimal> invoicedQuantities, IReadOnlyDictionary<Guid, decimal> returnedQuantities, bool hasUnmappedLines = false)
    {
        var invoiced = invoicedQuantities.Where(line => line.Value > 0).ToList();
        if (invoiced.Count == 0) return InvoiceReturnScope.None;
        var anyReturned = invoiced.Any(line => returnedQuantities.GetValueOrDefault(line.Key) > 0);
        if (!anyReturned) return InvoiceReturnScope.None;
        return !hasUnmappedLines && invoiced.All(line => returnedQuantities.GetValueOrDefault(line.Key) >= line.Value)
            ? InvoiceReturnScope.Full
            : InvoiceReturnScope.Partial;
    }
}
