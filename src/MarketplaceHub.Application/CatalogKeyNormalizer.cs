using System.Globalization;
using System.Text;

namespace MarketplaceHub.Application;

// The one identity key for catalog codes and names: SKU, barcode, option names and values, brand and
// category names. Accents are dropped (FormD, marks removed), İ/ı/i all become I, letters are upper-cased
// with the invariant culture and every run of other characters (space, '-', '_', '/', '.') becomes one '-'.
// "ABC_01", "abc-01" and "ABC 01" share the key ABC-01; "Gümüş" and "GUMUS" share GUMUS.
//
// Rows written before this key existed may hold the old panel form (trimmed and upper-cased only), so
// database lookups use LookupKeys, which returns both forms, until the stored columns are recomputed;
// in-memory comparisons use SameKey.
public static class CatalogKeyNormalizer
{
    public const int DefaultMaximum = 160;

    public static string Key(string? value, int maximum = DefaultMaximum) => Fold(value, maximum, foldDotlessI: true);

    // The normalisation the panel stored before the shared key (trim + invariant upper case).
    public static string Legacy(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();

    // The key the marketplace import stored before the shared key: the same folding, except that the
    // invariant upper case keeps the dotless ı ("Kırmızı" → "KıRMıZı").
    public static string LegacyMarketplace(string? value, int maximum = DefaultMaximum) => Fold(value, maximum, foldDotlessI: false);

    // Every stored form a value may have: the shared key first, then the old marketplace and panel forms.
    public static string[] LookupKeys(string? value, int maximum = DefaultMaximum)
    {
        var keys = new List<string>(3);
        foreach (var candidate in new[] { Key(value, maximum), LegacyMarketplace(value, maximum), Legacy(value) })
            if (candidate.Length > 0 && !keys.Contains(candidate, StringComparer.Ordinal)) keys.Add(candidate);
        return [.. keys];
    }

    private static string Fold(string? value, int maximum, bool foldDotlessI)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var form = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(form.Length);
        foreach (var ch in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            // İ decomposes to I + combining dot (dropped above); ı has no decomposition and the invariant
            // upper case keeps it, so it is folded explicitly.
            if (foldDotlessI && ch == 'ı') builder.Append('I');
            else if (char.IsLetterOrDigit(ch)) builder.Append(char.ToUpperInvariant(ch));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var key = builder.ToString().Trim('-');
        return key.Length <= maximum ? key : key[..maximum];
    }

    public static string[] LookupKeys(IEnumerable<string?> values, int maximum = DefaultMaximum) =>
        values.SelectMany(value => LookupKeys(value, maximum)).Distinct(StringComparer.Ordinal).ToArray();

    // In-memory comparison of two values that may be raw text or a stored key in either form: Key is
    // idempotent over the legacy form (Key(Legacy(x)) == Key(x)), so both sides fold to the shared key.
    public static bool SameKey(string? left, string? right, int maximum = DefaultMaximum)
    {
        var key = Key(left, maximum);
        return key.Length > 0 && string.Equals(key, Key(right, maximum), StringComparison.Ordinal);
    }
}

// The one variant option signature: "Renk: Mavi | Beden: XL". Colour first, then size, then other
// options, then web colour; within a group by the shared key, so {Renk, beden} and {renk, Beden} give the
// same order. Parse reads this form and the old panel form "RENK=MAVI|BEDEN=XL".
public static class VariantOptionSignature
{
    public const int MaximumLength = 512;

    public static string Build(IEnumerable<KeyValuePair<string, string>>? options, string empty = "")
    {
        if (options is null) return empty;
        var signature = new StringBuilder(MaximumLength);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = options
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(CleanValue(pair.Value)))
            .Select(pair => (Pair: pair, Key: CatalogKeyNormalizer.Key(pair.Key, 80)))
            .Where(item => item.Key.Length > 0)
            .OrderBy(item => AxisRank(item.Pair.Key))
            .ThenBy(item => item.Key, StringComparer.Ordinal);
        foreach (var (pair, key) in ordered)
        {
            if (!seen.Add(key)) continue;
            var part = $"{Short(pair.Key, 80)}: {Short(CleanValue(pair.Value), 120)}";
            var separatorLength = signature.Length == 0 ? 0 : 3;
            if (signature.Length + separatorLength + part.Length > MaximumLength) break;
            if (separatorLength > 0) signature.Append(" | ");
            signature.Append(part);
        }
        return signature.Length == 0 ? empty : signature.ToString();
    }

    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return [];
        var result = new List<KeyValuePair<string, string>>();
        // Options are separated by " | "; a bare '|' may be part of a value ("Renk: Siyah|Beyaz"). Only the
        // old panel form ("RENK=MAVI|BEDEN=XL", '=' before any ':') used the bare '|' as separator.
        var parts = signature.Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => IsLegacyPart(part) ? part.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [part]);
        foreach (var part in parts)
        {
            var colon = part.IndexOf(':');
            var equals = part.IndexOf('=');
            var separator = colon < 0 ? equals : equals < 0 ? colon : Math.Min(colon, equals);
            if (separator <= 0) continue;
            var key = part[..separator].Trim();
            var value = CleanValue(part[(separator + 1)..]);
            if (key.Length > 0 && value.Length > 0) result.Add(new(key, value));
        }
        return result;
    }

    // Option name → value keyed by the shared key (first occurrence wins).
    public static IReadOnlyDictionary<string, string> ParseKeyed(string? signature)
    {
        var keyed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Parse(signature))
            keyed.TryAdd(CatalogKeyNormalizer.Key(pair.Key, 80), CatalogKeyNormalizer.Key(pair.Value, 120));
        return keyed;
    }

    public static bool IsColorName(string? name) => CatalogKeyNormalizer.Key(name, 320).Replace("-", "", StringComparison.Ordinal) is "RENK" or "COLOR" or "COLOUR";
    public static bool IsWebColorName(string? name) => CatalogKeyNormalizer.Key(name, 320).Replace("-", "", StringComparison.Ordinal) is "WEBCOLOR" or "WEBCOLOUR" or "WEBRENK";
    public static bool IsSizeName(string? name) => CatalogKeyNormalizer.Key(name, 320).Replace("-", "", StringComparison.Ordinal) is "BEDEN" or "SIZE" or "SIZ" or "NUMARA" or "NUMBER";

    private static bool IsLegacyPart(string part)
    {
        var equals = part.IndexOf('=');
        var colon = part.IndexOf(':');
        return equals > 0 && (colon < 0 || equals < colon);
    }

    private static int AxisRank(string name) => IsWebColorName(name) ? 3 : IsColorName(name) ? 0 : IsSizeName(name) ? 1 : 2;
    private static string CleanValue(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Trim('"', '“', '”').Trim();
    private static string Short(string value, int maximum) => value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..maximum];
}
