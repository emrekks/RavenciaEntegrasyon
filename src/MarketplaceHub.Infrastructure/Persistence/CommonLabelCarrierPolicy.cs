namespace MarketplaceHub.Infrastructure.Persistence;

// The carriers a common label is offered for, matched by carrier code or name, never by a
// substring of another name ("TEX" is not in "TEXTIL", "ARAS" is not in "KARASU").
public static class CommonLabelCarrierPolicy
{
    public static bool Supports(string? cargoProviderExternalId) =>
        Matches(cargoProviderExternalId, codes: ["ARASMP", "TEXMP"], namePrefixes: ["ARASKARGO", "TRENDYOLEXPRESS"], tokens: ["ARAS", "TEX"]);

    public static bool SupportsHepsiburada(string? cargoProviderExternalId) =>
        Matches(cargoProviderExternalId, codes: ["ARASMP", "HEPSIJETMP"], namePrefixes: ["HEPSIJET", "ARASKARGO"], tokens: ["ARAS", "HEPSIJET"]);

    private static bool Matches(string? value, string[] codes, string[] namePrefixes, string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var upper = value.Trim().ToUpperInvariant();
        var compact = new string(upper.Where(char.IsLetterOrDigit).ToArray());
        if (codes.Contains(compact, StringComparer.Ordinal) || namePrefixes.Any(prefix => compact.StartsWith(prefix, StringComparison.Ordinal))) return true;
        var words = upper.Split(upper.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries);
        return words.Any(word => tokens.Contains(word, StringComparer.Ordinal));
    }
}
