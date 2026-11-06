namespace MarketplaceHub.Domain;

/// <summary>
/// The units of an order line that came back to the seller: one rule for the reports and the sale invoice. Each piece
/// of stored evidence (the line's returned quantity, its package line's returned quantity, the units of a package in
/// Returned state, the units an approved return claim accepted) counts the same units, so the largest one counts, never
/// their sum. A returned order took every unit back. Nothing is inferred from a missing field: no evidence is no
/// return, and an open claim (requested, on its way, waiting for the seller) returns nothing until it is approved
/// (owner, 6 Nov 2026).
/// </summary>
public static class OrderLineReturnPolicy
{
    public static decimal ReturnedUnits(decimal activeQuantity, bool orderReturned, params decimal[] evidence)
    {
        if (activeQuantity <= 0) return 0m;
        if (orderReturned) return activeQuantity;
        var back = evidence.Length == 0 ? 0m : evidence.Max();
        return back <= 0 ? 0m : Math.Min(activeQuantity, back);
    }
}

/// <summary>An order line as a sale invoice sees it: its units still ordered (not cancelled) and those returned.</summary>
public sealed record InvoiceLineUnits(Guid OrderLineId, decimal UnitPrice, decimal ActiveQuantity, decimal ReturnedQuantity)
{
    public decimal KeptQuantity => ActiveQuantity <= 0 ? 0m : ActiveQuantity - Math.Clamp(ReturnedQuantity, 0m, ActiveQuantity);
}

public enum InvoiceSaleReturnScope
{
    None,
    // Some units came back: the invoice covers only the units still kept.
    Partial,
    // Every unit came back: no sale invoice at all.
    Full
}

/// <summary>
/// A sale invoice of an order not invoiced yet covers only what the customer kept (owner's rule of 6 Nov 2026): an
/// order whose every unit was returned gets no sale invoice, a partly returned order is invoiced for its kept units
/// (a line of 3 with 1 returned is invoiced for 2). No return invoice is ever issued, and an invoice already issued is
/// never touched (InvoiceReturnAdjustmentPolicy).
/// </summary>
public static class InvoiceSaleReturnPolicy
{
    public const string FullyReturnedCode = "INVOICE_ORDER_FULLY_RETURNED";
    public const string FullyReturnedMessage = "Tüm ürünler iade edildi, fatura kesilmez.";

    public static InvoiceSaleReturnScope Classify(IReadOnlyCollection<InvoiceLineUnits> lines)
    {
        var active = lines.Where(line => line.ActiveQuantity > 0).ToList();
        if (active.Count == 0 || active.All(line => line.KeptQuantity == line.ActiveQuantity)) return InvoiceSaleReturnScope.None;
        return active.All(line => line.KeptQuantity <= 0) ? InvoiceSaleReturnScope.Full : InvoiceSaleReturnScope.Partial;
    }

    /// <summary>
    /// The order totals for the kept units only. The marketplace's totals still hold the returned units, so the kept
    /// units take their list price and the same share of the order discount that InvoiceAmounts.TryCalculatePackage
    /// gives each line (proportional to its price). Totals that already left the returned units out (they match the kept
    /// units, not all of them) are used as they are; totals that match neither are returned unchanged, so the
    /// calculation refuses them as before.
    /// </summary>
    public static InvoiceOrderTotals KeptTotals(IReadOnlyCollection<InvoiceLineUnits> lines, InvoiceOrderTotals totals)
    {
        var fullGross = lines.Sum(line => Money(Math.Max(0m, line.ActiveQuantity) * line.UnitPrice));
        var keptGross = lines.Sum(line => Money(line.KeptQuantity * line.UnitPrice));
        if (keptGross == fullGross) return totals;
        var gross = totals.GrossAmount > 0 ? Money(totals.GrossAmount) : fullGross;
        if (gross <= 0 || Math.Abs(fullGross - gross) > 0.01m) return totals;
        var payable = Money(totals.PayableAmount);
        var discount = totals.DiscountAmount > 0 ? Money(totals.DiscountAmount) : Math.Max(0m, gross - payable);
        if (discount > gross || Math.Abs(gross - discount - payable) > 0.01m) return totals;
        var keptDiscount = Money(discount * keptGross / gross);
        return new(keptGross, keptDiscount, keptGross - keptDiscount);
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
