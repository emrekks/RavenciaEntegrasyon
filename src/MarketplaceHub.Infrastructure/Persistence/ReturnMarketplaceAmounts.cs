using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// A return's amounts as the marketplace wrote them for the returned line, never calculated from the order's totals.
// They are read from the line's stored marketplace snapshot (order_lines.SourceSnapshotJson: the order package line,
// or the claim's own line when the order was built from the claim), per returned unit × the returned quantity:
// - Trendyol: amount (sale price per unit), discount (discount per unit), price (invoiced price per unit); the
//   per-unit discountDetails are summed when the whole line is returned. A line built from a claim carries only
//   price (getClaims orderLine), so only the invoiced amount is known there.
// - Hepsiburada: the claim's refundAmount, when the snapshot is that claim (number = claim number).
// - n11: price (sale price per unit); for a single unit also sellerInvoiceAmount and totalSellerDiscountPrice.
// - Pazarama, PTT AVM, Shopify: no amount is stored for the returned line, so every figure stays unknown.
// A figure the marketplace did not write stays null: it is shown as unknown, never filled in from the order.
public static class ReturnMarketplaceAmounts
{
    public static ReturnAmountsView? Line(string? platformCode, string? sourceSnapshotJson, decimal returnedQuantity, string? externalClaimId)
    {
        if (string.IsNullOrWhiteSpace(sourceSnapshotJson) || returnedQuantity <= 0) return null;
        try
        {
            using var document = JsonDocument.Parse(sourceSnapshotJson);
            var line = document.RootElement;
            if (line.ValueKind != JsonValueKind.Object) return null;
            var view = (platformCode ?? "").Trim().ToUpperInvariant() switch
            {
                "TRENDYOL" => Trendyol(line, returnedQuantity),
                "HEPSIBURADA" => Hepsiburada(line, externalClaimId),
                "N11" => N11(line, returnedQuantity),
                _ => null
            };
            return view is { SaleAmount: null, DiscountAmount: null, InvoicedAmount: null, RefundAmount: null } ? null : view;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A claim's (or a card's) amounts: each figure is the sum over its lines only when every line has it; one line
    // without it leaves the figure unknown, as a partial sum would read as the whole return's amount.
    public static ReturnAmountsView? Total(IReadOnlyCollection<ReturnAmountsView?> lines)
    {
        if (lines.Count == 0) return null;
        decimal? Sum(Func<ReturnAmountsView, decimal?> figure)
        {
            decimal total = 0;
            foreach (var line in lines)
            {
                if (line is null || figure(line) is not { } amount) return null;
                total += amount;
            }
            return total;
        }
        var view = new ReturnAmountsView(Sum(x => x.SaleAmount), Sum(x => x.DiscountAmount), Sum(x => x.InvoicedAmount), Sum(x => x.RefundAmount));
        return view is { SaleAmount: null, DiscountAmount: null, InvoicedAmount: null, RefundAmount: null } ? null : view;
    }

    private static ReturnAmountsView? Trendyol(JsonElement line, decimal quantity)
    {
        var price = Number(line, "price") ?? Number(line, "lineUnitPrice");
        var amount = Number(line, "amount");
        var discount = Number(line, "discount") ?? Number(line, "lineTotalDiscount");
        if (amount is null)
            return price is { } claimPrice ? new(null, null, Money(claimPrice * quantity), null) : null;
        // The whole line returned: its units as Trendyol split them (a discount of 83.33 over three units is
        // 27.78 + 27.78 + 27.77), so the total equals Trendyol's own to the kuruş.
        if (line.TryGetProperty("discountDetails", out var details) && details.ValueKind == JsonValueKind.Array
            && details.GetArrayLength() == quantity && quantity == decimal.Truncate(quantity))
        {
            decimal unitPrices = 0, unitDiscounts = 0;
            var complete = true;
            foreach (var unit in details.EnumerateArray())
            {
                if (Number(unit, "lineItemPrice") is not { } unitPrice || Number(unit, "lineItemDiscount") is not { } unitDiscount) { complete = false; break; }
                unitPrices += unitPrice;
                unitDiscounts += unitDiscount;
            }
            if (complete) return new(Money(unitPrices + unitDiscounts), Money(unitDiscounts), Money(unitPrices), null);
        }
        return new(Money(amount.Value * quantity), discount is { } unitDiscountAmount ? Money(unitDiscountAmount * quantity) : null,
            price is { } unitPriceAmount ? Money(unitPriceAmount * quantity) : null, null);
    }

    private static ReturnAmountsView? Hepsiburada(JsonElement line, string? externalClaimId)
    {
        // The snapshot is this claim's own record only when it names this claim; an order line or another claim says nothing here.
        var claimNumber = Text(line, "number") ?? Text(line, "claimNumber");
        if (claimNumber is null || externalClaimId is null || !string.Equals(claimNumber.Trim(), externalClaimId.Trim(), StringComparison.Ordinal)) return null;
        var refund = Number(line, "refundAmount");
        if (refund is null && line.TryGetProperty("refundAmount", out var refundObject) && refundObject.ValueKind == JsonValueKind.Object)
            refund = Number(refundObject, "amount");
        return refund is { } amount ? new(null, null, null, Money(amount)) : null;
    }

    private static ReturnAmountsView? N11(JsonElement line, decimal quantity)
    {
        var price = Number(line, "price");
        // sellerInvoiceAmount and totalSellerDiscountPrice are the line's; read only when one unit was ordered and returned.
        var single = quantity == 1 && Number(line, "quantity") == 1;
        return new(price is { } unit ? Money(unit * quantity) : null,
            single ? Number(line, "totalSellerDiscountPrice") is { } discount ? Money(discount) : null : null,
            single ? Number(line, "sellerInvoiceAmount") is { } invoiced ? Money(invoiced) : null : null,
            null);
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
}
