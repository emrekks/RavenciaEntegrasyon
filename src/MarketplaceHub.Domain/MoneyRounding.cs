namespace MarketplaceHub.Domain;

// The one rounding of money amounts and rates: half a kuruş goes up (away from zero), as Turkish tax practice and the
// e-invoice amounts round (InvoiceAmounts). A figure rounded elsewhere with the default (to even) still differs at an
// exact half kuruş; those are listed in audit 2026-11-15 FATURA-FINANS-5 and wait for the owner's choice.
public static class MoneyRounding
{
    // To the kuruş.
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    // A rate or share to four places (0.1234 = 12.34 %).
    public static decimal Rate(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
