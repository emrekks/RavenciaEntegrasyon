namespace MarketplaceHub.Domain;

// The one rounding of money amounts: half a kuruş goes up (away from zero), as Turkish tax practice and the e-invoice
// amounts round (InvoiceAmounts). The owner chose the same rule for the reports, the overview, profitability, payouts
// (Hakediş) and ad reports (16 Nov 2026, FATURA-FINANS-5). Only our own computed sums, shares, VAT splits, estimates
// and differences are rounded here; an amount a marketplace reported is shown as received (they come at whole kuruş).
// Figures that are not money (ratios, percentages, days, units per day) go through MeasureRounding.
// MoneyRoundingArchitectureTests keeps every rounding in those areas on an explicit rule.
public static class MoneyRounding
{
    // To the kuruş.
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    // A rate or share to four places (0.1234 = 12.34 %).
    public static decimal Rate(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}

// The rounding of report figures that are not money: ratios and shares (4 places), percentages (2), averages of days,
// hours and units per day (1 or 2). They keep the midpoint rule they always had (to even); the owner's rule of
// 16 Nov 2026 is about kuruş (MoneyRounding).
public static class MeasureRounding
{
    public static decimal Round(decimal value, int decimals) => decimal.Round(value, decimals, MidpointRounding.ToEven);
}
