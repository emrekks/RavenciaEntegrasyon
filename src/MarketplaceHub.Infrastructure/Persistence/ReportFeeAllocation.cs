using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;

namespace MarketplaceHub.Infrastructure.Persistence;

// A settlement line of a payout (ReportsService.FinanceAsync): the order it paid, its package, its country and amounts.
internal sealed record ReportPayoutLine(Guid ConnectionId, string PaymentOrderId, string OrderNumber, string? ShipmentPackageId, string RequestedType, string? Country, decimal Debt, decimal Credit);

// Trendyol bills the platform service fee, the international service fee and the e-commerce withholding tax per sale,
// but on invoices of a payment day: the record carries no order number and no package, only the payout it was taken
// from (paymentOrderId), and Trendyol stamps the same payout id on the settlement lines of the orders that payout paid.
// So each such invoice is split over the orders of its payout (owner, 5 Nov 2026: these belong to their orders, unlike
// advertising). Live, 30 payouts of Aug-Nov 2026: the platform fee was 13.19 TL x the payout's sale packages on every
// Turkish payout (10.99 TL on the AZ ones), the international fee 5.1-5.9 % of its micro-export sales and the
// withholding tax 0.7-0.9 % of its sales less returns (1 % of the VAT-free amount). The shares follow those bases:
//   platform service fee   equal per sale package;
//   international fee      by the sale amount of the payout's packages abroad (all its sales if none is abroad);
//   withholding tax        by the sale amount less the returns of each order (sales alone if that is not positive),
//                          so a return taken back in a payout gives its order its tax back.
// An invoice whose payout lines are not read yet (Trendyol lists a payout after paying it and its lines a day later),
// or that names no payout, is not split: it stays a record of no order.
internal static class ReportFeeAllocation
{
    public static bool IsPerSaleInvoice(ReportFinanceRow row) =>
        row.Source == TrendyolFinanceRequestContract.OtherFinancials && row.OrderNumber is null && !string.IsNullOrEmpty(row.PaymentOrderId)
        && row.Category is MarketplaceFinanceCategories.ServiceFee or MarketplaceFinanceCategories.Stoppage;

    // The invoice's shares, one per order of its payout (OrderNumber set, the amounts of each share adding up to the
    // invoice's to the kuruş), or null when its payout has no line to split by.
    public static IReadOnlyList<ReportFinanceRow>? Split(ReportFinanceRow invoice, IReadOnlyList<ReportPayoutLine> payoutLines)
    {
        if (!IsPerSaleInvoice(invoice)) return null;
        var lines = payoutLines.Where(line => line.ConnectionId == invoice.ConnectionId && line.PaymentOrderId == invoice.PaymentOrderId).ToList();
        var sales = lines.Where(line => IsSale(line.RequestedType)).ToList();
        var weights = invoice.Category == MarketplaceFinanceCategories.Stoppage ? StoppageWeights(lines, sales)
            : ReportCosts.IsInternationalFee(invoice) ? InternationalWeights(sales)
            : PackageWeights(sales);
        if (weights.Count == 0) return null;

        var total = TrendyolFinanceCategorizer.NetCost(invoice.Debt, invoice.Credit);
        var sum = weights.Values.Sum();
        var ordered = weights.OrderByDescending(weight => weight.Value).ThenBy(weight => weight.Key, StringComparer.Ordinal).ToList();
        var shares = ordered.ToDictionary(weight => weight.Key, weight => Math.Round(total * weight.Value / sum, 2, MidpointRounding.AwayFromZero), StringComparer.Ordinal);
        // The rounding left over goes to the largest share, so the shares add up to the invoice.
        shares[ordered[0].Key] += total - shares.Values.Sum();
        return ordered.Select(weight => new ReportFinanceRow
        {
            ConnectionId = invoice.ConnectionId,
            Source = invoice.Source,
            ExternalId = invoice.ExternalId,
            RequestedType = invoice.RequestedType,
            TransactionType = invoice.TransactionType,
            Category = invoice.Category,
            TransactionDate = invoice.TransactionDate,
            OrderNumber = weight.Key,
            PaymentOrderId = invoice.PaymentOrderId,
            Debt = shares[weight.Key] >= 0m ? shares[weight.Key] : 0m,
            Credit = shares[weight.Key] < 0m ? -shares[weight.Key] : 0m,
            AllocatedFrom = invoice.ExternalId
        }).ToList();
    }

    private static bool IsSale(string type) => string.Equals(type, "Sale", StringComparison.OrdinalIgnoreCase);
    private static bool IsReturn(string type) => string.Equals(type, "Return", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, decimal> PackageWeights(IReadOnlyList<ReportPayoutLine> sales) =>
        sales.GroupBy(line => line.OrderNumber, StringComparer.Ordinal)
            .ToDictionary(order => order.Key, order => (decimal)order.Select(line => line.ShipmentPackageId ?? "").Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);

    private static Dictionary<string, decimal> InternationalWeights(IReadOnlyList<ReportPayoutLine> sales)
    {
        var abroad = sales.Where(line => Abroad(line.Country)).ToList();
        return Positive(abroad.Count > 0 ? abroad : sales, line => line.Credit - line.Debt);
    }

    private static Dictionary<string, decimal> StoppageWeights(IReadOnlyList<ReportPayoutLine> lines, IReadOnlyList<ReportPayoutLine> sales)
    {
        var net = lines.Where(line => IsSale(line.RequestedType) || IsReturn(line.RequestedType))
            .GroupBy(line => line.OrderNumber, StringComparer.Ordinal)
            .ToDictionary(order => order.Key, order => order.Sum(line => line.Credit - line.Debt), StringComparer.Ordinal);
        var weights = net.Where(order => order.Value != 0m).ToDictionary(order => order.Key, order => order.Value, StringComparer.Ordinal);
        return weights.Values.Sum() > 0m ? weights : Positive(sales, line => line.Credit - line.Debt);
    }

    // The orders with a positive amount; none when nothing is positive.
    private static Dictionary<string, decimal> Positive(IEnumerable<ReportPayoutLine> lines, Func<ReportPayoutLine, decimal> amount) =>
        lines.GroupBy(line => line.OrderNumber, StringComparer.Ordinal)
            .Select(order => (order.Key, Amount: order.Sum(amount)))
            .Where(order => order.Amount > 0m)
            .ToDictionary(order => order.Key, order => order.Amount, StringComparer.Ordinal);

    // Trendyol names the country in Turkish ("Türkiye", "Suudi Arabistan"); an unnamed one is taken as domestic.
    private static bool Abroad(string? country)
    {
        var folded = TrendyolFinanceCategorizer.Fold(country);
        return folded.Length > 0 && folded != "turkiye" && folded != "turkey";
    }
}
