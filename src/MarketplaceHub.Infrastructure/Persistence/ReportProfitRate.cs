using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// The product cost the profit takes off (owner, 13 Nov 2026). The deductions come off every sale, so the cost must cover
// every unit the customers kept too, or the profit comes out too high by the missing cost. The profit is worked out only
// when at least 95 % of the kept units carry a cost (Usable; below it the page shows "Maliyet Eksik"). Between 95 and 100 %
// the units without a cost are priced by their share of the sales: the known cost's ratio to the revenue of its units
// (CostTotal / CostedRevenue) times the revenue of the units without one (KeptRevenue - CostedRevenue). A price-weighted
// share fits a catalogue of cheap and dear products better than an average cost per unit, which would give a cheap
// product a dear one's cost. Without the revenue figure (an older report) the average cost per unit stands in. The page
// marks a profit with an estimated part "Tahmini" and says how many units lack a cost (ReportsPage.tsx, profitCost in
// report-format.ts, the same rule).
public sealed record ReportProfitCost(bool Usable, decimal Known, decimal Estimated, decimal Missing)
{
    public const decimal MinimumCoverage = 0.95m;

    public decimal Total => Known + Estimated;

    public static ReportProfitCost Of(ReportCostView cost)
    {
        var missing = Math.Max(0m, cost.KeptQuantity - cost.CostedQuantity);
        var usable = cost.CostedQuantity > 0m && cost.CostTotal != 0m && cost.CostedQuantity >= cost.KeptQuantity * MinimumCoverage;
        if (!usable || missing == 0m) return new(usable, cost.CostTotal, 0m, missing);
        var estimated = cost.CostedRevenue > 0m && cost.KeptRevenue >= cost.CostedRevenue
            ? cost.CostTotal * (cost.KeptRevenue - cost.CostedRevenue) / cost.CostedRevenue
            : cost.CostTotal * missing / cost.CostedQuantity;
        return new(true, cost.CostTotal, Math.Round(estimated, 2), missing);
    }
}

// The real profit on cost of a sales report, worked out as the Raporlar page's account does it (ReportsPage.tsx
// moneyBreakdown over profitSteps): the sales less the deductions (returns, the commission, and with Trendyol's statement
// its cargo and per-sale fees, each with what the period charged of it tied to none of its orders), the net VAT, the
// product cost (ReportProfitCost: with the estimate for the units without one) and the deductions no order carries; over
// that product cost. Null where the page shows no profit: no order with an amount, no cost entered, or less than 95 % of
// the kept units with a cost (the page shows "Maliyet Eksik" then). Stok's Tahmini Kâr averages it over the last whole
// months (ReportsService.ProfitRateAsync).
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
        var productCost = ReportProfitCost.Of(data.Cost);
        if (!productCost.Usable || productCost.Total == 0m) return null;
        var cost = productCost.Total;
        var profit = summary.GrossSales - deducted - (tax ?? 0m) - cost - unlinked;
        return profit / cost;
    }
}
