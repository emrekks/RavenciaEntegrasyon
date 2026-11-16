using System.Globalization;
using System.Text;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-9: exact PostgreSQL forms of the Returns page's text rules, built from .NET's own character tables so the
// database and the in-memory rules can never disagree, whatever the database locale (the live one is en_US, the test
// clusters C):
// - the page search (MarketplaceSalesService.MatchesReturnSearch): a value matches when its Turkish lower case with the
//   four i letters folded (Fold) contains the folded term. Folding is one code point to one code point, so the folded
//   value contains the needle exactly when the value has a run of code points each folding to the needle's code point at
//   that place: one bracket expression per needle code point, listing every code point that folds to it.
// - a trimmed, case-insensitive equality (IsInvoicedRemoteStatus) and a superset of the invoice words a customer snapshot
//   can carry (InvoiceLabelPolicy.InvoiceLabel), for the invoice label.
// Every code point is written as a \u or \U escape, which PostgreSQL's advanced regular expressions read alike inside and
// outside a bracket expression, so no character of a value or term is ever special.
internal static class ReturnSearchText
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    // The page search's folding of a value (MatchesReturnSearch).
    internal static string Fold(string value) => TurkishSearchText.Fold(value.ToLower(Turkish));

    // The folded term the page search looks for.
    internal static string Needle(string term) => Fold(term.Trim());

    // Each folded code point with the code points that fold to it, when that is not only itself. Built once from every
    // code point; a code point whose fold is not one code point would break the rule above and stops the build.
    private static readonly Lazy<Dictionary<int, List<int>>> Folds = new(() =>
    {
        var folds = new Dictionary<int, List<int>>();
        for (var code = 1; code <= 0x10FFFF; code++)
        {
            if (code is >= 0xD800 and <= 0xDFFF) continue;
            var value = char.ConvertFromUtf32(code);
            var folded = Fold(value);
            if (folded == value) continue;
            if (Rune.DecodeFromUtf16(folded, out var rune, out var used) != System.Buffers.OperationStatus.Done || used != folded.Length)
                throw new InvalidOperationException($"U+{code:X4} folds to more than one code point.");
            if (!folds.TryGetValue(rune.Value, out var list)) folds[rune.Value] = list = [];
            list.Add(code);
        }
        return folds;
    });

    // Every code point (NUL aside: no database text holds it) that folds to this one.
    internal static IReadOnlyList<int> FoldingTo(int folded)
    {
        var codes = new List<int>();
        if (folded != 0 && Fold(char.ConvertFromUtf32(folded)) == char.ConvertFromUtf32(folded)) codes.Add(folded);
        if (Folds.Value.TryGetValue(folded, out var others)) codes.AddRange(others);
        codes.Sort();
        return codes;
    }

    // The search as a regular expression over a raw value, or null when the needle cannot be sent to the database (a lone
    // surrogate or a NUL: no text value can hold either, and the term cannot be sent at all).
    internal static string? SearchPattern(string needle)
    {
        if (needle.Length == 0 || needle.Contains('\0')) return null;
        var pattern = new StringBuilder();
        var at = 0;
        while (at < needle.Length)
        {
            if (Rune.DecodeFromUtf16(needle.AsSpan(at), out var rune, out var used) != System.Buffers.OperationStatus.Done) return null;
            var codes = FoldingTo(rune.Value);
            // A needle code point no value code point folds to: the needle can match nothing.
            if (codes.Count == 0) return NeverMatches;
            pattern.Append(Class(codes));
            at += used;
        }
        return pattern.ToString();
    }

    // A pattern no database text matches: it finds only a NUL, which PostgreSQL text never holds.
    internal const string NeverMatches = "[^\\u0001-\\U0010FFFF]";

    // Every code point .NET's string.Trim() strips (char.IsWhiteSpace).
    private static readonly int[] WhiteSpace = [.. Enumerable.Range(0, char.MaxValue + 1).Where(code => char.IsWhiteSpace((char)code))];

    // A whole value equal to the word after a .NET Trim() and under OrdinalIgnoreCase (IsInvoicedRemoteStatus).
    internal static string TrimmedIgnoreCasePattern(string word) =>
        "^" + Class(WhiteSpace) + "*" + string.Concat(word.Select(letter => Class(SameIgnoringCase(letter)))) + Class(WhiteSpace) + "*$";

    // A whole value .NET's string.IsNullOrWhiteSpace calls blank (null aside).
    internal static string BlankPattern => "^" + Class(WhiteSpace) + "*$";

    // The UTF-16 units equal to this letter under OrdinalIgnoreCase (a supplementary code point never equals a BMP one).
    private static int[] SameIgnoringCase(char letter) =>
        [.. Enumerable.Range(1, char.MaxValue).Where(code => code is < 0xD800 or > 0xDFFF && string.Equals(((char)code).ToString(), letter.ToString(), StringComparison.OrdinalIgnoreCase))];

    // The UTF-16 units whose ToUpperInvariant is this letter.
    private static int[] UpperInvariantTo(char letter) =>
        [.. Enumerable.Range(1, char.MaxValue).Where(code => code is < 0xD800 or > 0xDFFF && char.ToUpperInvariant((char)code) == letter)];

    // A raw JSON text that may decode to a string holding one of the words, compared after ToUpperInvariant: each letter is
    // written either as itself or as a \uXXXX escape (the only JSON escape that gives a letter). A superset: the snapshot
    // is then read by JsonText itself.
    internal static string JsonWordsPattern(params string[] words) =>
        "(?:" + string.Join('|', words.Select(word => string.Concat(word.Select(letter => "(?:" + Class(UpperInvariantTo(letter)) + "|\\\\u[0-9A-Fa-f]{4})")))) + ")";

    private static string Class(IEnumerable<int> codes) =>
        "[" + string.Concat(codes.Select(code => code <= 0xFFFF ? $"\\u{code:X4}" : $"\\U{code:X8}")) + "]";
}
