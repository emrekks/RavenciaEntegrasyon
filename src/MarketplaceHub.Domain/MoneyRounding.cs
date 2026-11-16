namespace MarketplaceHub.Domain;

// The one rounding of money amounts: half a kuruş goes up (away from zero), as Turkish tax practice and the e-invoice
// amounts round (InvoiceAmounts). The owner chose the same rule for the reports, the overview, profitability, payouts
// (Hakediş) and ad reports (16 Nov 2026, FATURA-FINANS-5). Only our own computed sums, shares, VAT splits, estimates
// and differences are rounded here; an amount a marketplace reported is shown as received (they come at whole kuruş).
// Percentages and rates go through PercentRounding; other figures that are not money (days, hours, units per day,
// orders per customer) through MeasureRounding. MoneyRoundingArchitectureTests keeps every rounding in those areas on an
// explicit rule.
public static class MoneyRounding
{
    // To the kuruş.
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

// The one rounding of percentages and rates in the reports (owner, 16 Nov 2026, FATURA-FINANS-5 follow-up): half up
// (away from zero), as money rounds, so 12.345 % shows as 12.35 %. A rate kept as a fraction goes to four places
// (0.1234 = 12.34 %), a percentage on the 0–100 scale and the return on ad spend (a multiple of the spend) to two.
public static class PercentRounding
{
    // A share or rate as a fraction, four places: 0.12345 → 0.1235.
    public static decimal Rate(decimal value) => Round(value, 4);

    // A percentage on the 0–100 scale, or the return on ad spend, two places: 12.345 → 12.35.
    public static decimal Percent(decimal value) => Round(value, 2);

    private static decimal Round(decimal value, int decimals) => decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
}

// The rounding of report figures that are neither money nor a percentage: averages of days and hours (1 place), units
// per day and orders per customer (2), days of stock cover (1). They keep the midpoint rule they always had (to even).
// A percentage or rate never comes here (MoneyRoundingArchitectureTests lists every use).
public static class MeasureRounding
{
    public static decimal Round(decimal value, int decimals) => decimal.Round(value, decimals, MidpointRounding.ToEven);
}
