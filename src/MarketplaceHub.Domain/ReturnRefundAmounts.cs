namespace MarketplaceHub.Domain;

// The amount a return gives back, calculated from the order (marketplaces do not send it): each
// returned unit is worth what the buyer paid for it, the line's unit price less its share of the
// order discount. The discount is spread over the lines by their gross, as the invoice spreads it
// (InvoiceAmounts.TryCalculatePackage), so a refund never exceeds what was invoiced for the line.
public static class ReturnRefundAmounts
{
    // The part of every gross lira the buyer did not pay (0..1). orderLineGross is the sum of the
    // order's active lines (quantity × unit price), used only when the order carries no gross total.
    public static decimal DiscountRatio(decimal orderGross, decimal orderDiscount, decimal orderPayable, decimal orderLineGross)
    {
        var gross = orderGross > 0 ? orderGross : orderLineGross;
        if (gross <= 0) return 0;
        var discount = orderDiscount > 0 ? orderDiscount : orderPayable > 0 ? Math.Max(0, gross - orderPayable) : 0;
        return Math.Clamp(discount / gross, 0, 1);
    }

    public static decimal PaidUnitPrice(decimal unitPrice, decimal discountRatio) =>
        Math.Max(0, unitPrice) * (1 - Math.Clamp(discountRatio, 0, 1));

    public static decimal LineRefund(decimal returnedQuantity, decimal unitPrice, decimal discountRatio) =>
        decimal.Round(Math.Max(0, returnedQuantity) * PaidUnitPrice(unitPrice, discountRatio), 2, MidpointRounding.AwayFromZero);

    // Null when no returned line could be matched to its order line: no amount is shown then.
    public static decimal? ClaimTotal(IEnumerable<decimal?> lineRefunds)
    {
        decimal? total = null;
        foreach (var refund in lineRefunds)
            if (refund is { } amount) total = (total ?? 0) + amount;
        return total;
    }
}
