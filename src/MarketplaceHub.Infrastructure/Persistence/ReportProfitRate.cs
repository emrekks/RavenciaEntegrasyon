using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// The real profit on cost of a sales report, worked out as the Raporlar page's account does it (ReportsPage.tsx
// moneyBreakdown over profitSteps): the sales less the deductions (returns, the commission, and with Trendyol's statement
// its cargo and per-sale fees, each with what the period charged of it tied to none of its orders), the net VAT, the
// product cost and the deductions no order carries; over the product cost. Null where the page shows no profit: no order
// with an amount, no cost entered, or a unit kept without a cost (its deductions are taken off with the rest, so the
// profit would be too high by its missing cost; the page shows "Maliyet Eksik" then). Stok's Tahmini Kâr averages it over the last whole months (ReportsService.ProfitRateAsync).
internal static class ReportProfitRate
{
    // The per-sale fees (beside the commission) and the kinds of deduction no order carries, as the page names them.
    private static readonly string[] ChargeFeeCategories = ["SERVICE_FEE", "PAYMENT_FEE", "STOPPAGE"];
    private static readonly string[] OrderlessCategories = ["ADVERTISING", "PENALTY", "EARLY_PAYMENT", "OTHER_DEDUCTION"];

    public static decimal? Of(ReportSalesView data)
    {
        var summary = data.Summary;
        if (summary.OrdersWithAmount == 0) return null;
        var costs = data.Costs;
        var finance = costs is { Commission: not null, Cargo: not null } ? costs : null;
        // The period's deductions tied to none of its orders, from Trendyol's statement only, largest first (unlinkedDeductions).
        var apart = (costs?.Source == "trendyol-finance" ? costs.Unlinked ?? [] : [])
            .Where(line => line.IsCost && line.Amount != 0m).OrderByDescending(line => line.Amount).ToList();
        decimal Apart(string category) => apart.FirstOrDefault(line => line.Category == category)?.Amount ?? 0m;
        decimal Figure(string category) => (finance!.Lines.FirstOrDefault(line => line.Category == category)?.Amount ?? 0m) + Apart(category);

        var deducted = summary.ReturnedAmount;
        decimal? tax = null;
        decimal unlinked;
        if (finance is not null)
        {
            deducted += finance.Commission!.Combined + Apart("COMMISSION");
            deducted += finance.Cargo!.Combined + Apart("CARGO");
            deducted += ChargeFeeCategories.Sum(Figure);
            tax = finance.NetVat?.Net;
            unlinked = OrderlessCategories.Sum(Figure);
        }
        else
        {
            if (data.Commission.CoveredRevenue > 0m) deducted += data.Commission.EstimatedCommission;
            unlinked = apart.Sum(line => line.Amount);
        }
        if (data.Cost.CostedQuantity <= 0m || data.Cost.CostTotal == 0m || data.Cost.CostedQuantity < data.Cost.KeptQuantity) return null;
        var cost = data.Cost.CostTotal;
        var profit = summary.GrossSales - deducted - (tax ?? 0m) - cost - unlinked;
        return profit / cost;
    }
}
