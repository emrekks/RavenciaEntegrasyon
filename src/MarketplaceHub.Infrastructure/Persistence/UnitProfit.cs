using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// What a product needs for the profit of a price: its cost and VAT rate, and its store's cargo tariff per order.
internal sealed record UnitProfitFacts(decimal? Cost, decimal? VatRate, decimal? Cargo)
{
    public static readonly UnitProfitFacts None = new(null, null, null);
}

// The profit one unit sold on Trendyol at a price leaves, costed as ReportOrderProfit costs an order not settled yet:
// the commission, the product cost, the store's cargo tariff, the 13.19 TL service fee, 1 % withholding tax on the
// VAT-free price and the net VAT, each VAT rounded to kuruş on its own (470.64 TL at 21.5 %, cost 160, VAT 10 %, cargo
// 98.34: 101.19 + 160 + 98.34 + 13.19 + 4.28 − 7.22 net VAT leaves 100.86). Unknown without any of the facts.
internal static class UnitProfit
{
    public static (decimal? Profit, UnitProfitCostsView? Costs) Of(decimal price, decimal commission, UnitProfitFacts facts)
    {
        if (facts is not { Cost: { } cost, VatRate: { } vat, Cargo: { } cargo }) return (null, null);
        var fee = ReportCosts.PlatformServiceFeePerOrder;
        var stoppage = Round((price - ReportCosts.Vat(price, vat)) * ReportCosts.StoppageRate);
        var netVat = Round(ReportCosts.Vat(price, vat)) - Round(ReportCosts.Vat(cost, vat)) - Round(ReportCosts.Vat(commission, ReportCosts.ExpenseVatRate))
            - Round(ReportCosts.Vat(cargo, ReportCosts.ExpenseVatRate)) - Round(ReportCosts.Vat(fee, ReportCosts.ExpenseVatRate));
        var costs = new UnitProfitCostsView(cost, Round(cargo), fee, stoppage, netVat, vat);
        return (Round(price - commission - cost - costs.Cargo - fee - stoppage - netVat), costs);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
