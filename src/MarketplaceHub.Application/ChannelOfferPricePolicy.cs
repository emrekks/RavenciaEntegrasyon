namespace MarketplaceHub.Application;

// The price a channel offer sends out. An offer stored without VAT (EXCLUDED) is grossed up with its
// own VAT rate; every outbound price is rounded to two decimals with the offer's rounding mode, which
// is the precision every marketplace accepts.
public static class ChannelOfferPricePolicy
{
    public const string VatIncluded = "INCLUDED";
    public const string VatExcluded = "EXCLUDED";
    public const string DefaultRoundingMode = "HALF_EVEN";
    public const int OutboundDecimals = 2;

    private static readonly IReadOnlyDictionary<string, MidpointRounding> RoundingModes = new Dictionary<string, MidpointRounding>(StringComparer.OrdinalIgnoreCase)
    {
        ["HALF_EVEN"] = MidpointRounding.ToEven,
        ["HALF_UP"] = MidpointRounding.AwayFromZero,
        ["DOWN"] = MidpointRounding.ToZero,
        ["FLOOR"] = MidpointRounding.ToNegativeInfinity,
        ["UP"] = MidpointRounding.ToPositiveInfinity,
        ["CEILING"] = MidpointRounding.ToPositiveInfinity
    };

    public static bool IsKnownVatInclusion(string? value) =>
        string.Equals(value?.Trim(), VatIncluded, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), VatExcluded, StringComparison.OrdinalIgnoreCase);

    public static bool IsKnownRoundingMode(string? value) => value is not null && RoundingModes.ContainsKey(value.Trim());

    public static bool IsVatExcluded(string? vatInclusion) => string.Equals(vatInclusion?.Trim(), VatExcluded, StringComparison.OrdinalIgnoreCase);

    // A row stored before the rounding mode was validated falls back to banker's rounding, the
    // mode the panel has always written.
    public static MidpointRounding Rounding(string? roundingMode) =>
        roundingMode is not null && RoundingModes.TryGetValue(roundingMode.Trim(), out var mode) ? mode : MidpointRounding.ToEven;

    public static decimal Outbound(decimal price, decimal vatRate, string? vatInclusion, string? roundingMode)
    {
        if (vatRate < 0) throw new ArgumentOutOfRangeException(nameof(vatRate));
        var gross = IsVatExcluded(vatInclusion) ? price * (1m + vatRate / 100m) : price;
        return decimal.Round(gross, OutboundDecimals, Rounding(roundingMode));
    }

    // The marketplace reports a VAT-inclusive price. An offer kept without VAT stores the net
    // amount with four decimals, chosen so that it grosses up to the reported price again under the
    // offer's rounding mode (a truncating mode needs the net rounded up, a ceiling mode rounded down).
    public static decimal StoredFromGross(decimal gross, decimal vatRate, string? vatInclusion, string? roundingMode)
    {
        if (vatRate < 0) throw new ArgumentOutOfRangeException(nameof(vatRate));
        if (!IsVatExcluded(vatInclusion)) return decimal.Round(gross, 4, MidpointRounding.ToEven);
        var net = decimal.Round(gross / (1m + vatRate / 100m), 4, MidpointRounding.ToEven);
        var target = decimal.Round(gross, OutboundDecimals, MidpointRounding.ToEven);
        foreach (var step in new[] { 0m, 0.0001m, -0.0001m, 0.0002m, -0.0002m })
        {
            var candidate = net + step;
            if (candidate >= 0 && Outbound(candidate, vatRate, vatInclusion, roundingMode) == target) return candidate;
        }
        return net;
    }
}
