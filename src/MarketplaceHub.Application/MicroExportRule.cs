using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Application;

/// <summary>What the one micro-export rule says of an order.</summary>
public enum MicroExportState
{
    Domestic,
    MicroExport,
    Unknown
}

/// <summary>The rule's answer; Reason (Turkish) says why an order is Unknown, null otherwise.</summary>
public sealed record MicroExportDecision(MicroExportState State, string? Reason = null)
{
    public bool IsMicroExport => State == MicroExportState.MicroExport;
}

/// <summary>
/// The one micro-export rule (owner's decision, 27 Nov 2026, scan finding C-11.9): the invoice, the order list's label,
/// row flag, region filter and micro-export card all read it. Three answers: MicroExport, Domestic, Unknown. Nothing is
/// inferred from a missing field, and signals inside one order that disagree make it Unknown, never a guess. The invoice
/// path stops on Unknown (InvoiceIssuePolicy.MicroExportUnknown) and waits for the owner.
/// <para>Documented signals (offline Trendyol docs, Ravencia-API-Docs/trendyol-api-son.md):</para>
/// <list type="bullet">
/// <item><c>micro</c>, a boolean at the package's top level: true for a micro-export order (FAQ of 31.12.2025, ~l.403
/// and l.802; package sample ~l.4100). It is independent of the address <c>countryCode</c>, which is not used.</item>
/// <item><c>3pByTrendyol</c>, a boolean at the package's top level: true for Trendyol's export partnership, and then
/// <c>micro</c> is false and the invoice goes to Trendyol's company (~l.3822-3824). It is no domestic sale to the buyer,
/// so it reads as MicroExport, as both earlier rules read it.</item>
/// <item>The storefront the order was read under (Order.StoreFrontCode, any case): the micro-export countries are SA,
/// AE, QA, KW, OM, BH, AZ, RO, BG, GR, UA, MD, RS (~l.314-318) and SK, CZ (country table ~l.4197-4211). A storefront is
/// never the signal by itself; it only contradicts a flag (micro export read under TR, or a domestic flag under one of
/// these). Other storefronts (DE, null) say nothing.</item>
/// </list>
/// <para>Precedence, on a Trendyol order (every other marketplace is Domestic: none has a micro-export concept):</para>
/// <list type="number">
/// <item>No marketplace known: Unknown.</item>
/// <item>A snapshot that is no readable JSON object (empty, invalid, not an object, or holding <c>\u0000</c>, which
/// PostgreSQL's JSON types refuse): Unknown.</item>
/// <item>A micro / microExport / 3pByTrendyol key (any spelling, any depth) other than micro and 3pByTrendyol once at
/// the top level: Unknown.</item>
/// <item>No top-level micro (unless 3pByTrendyol is true), or a value other than true/false: Unknown.</item>
/// <item>micro and 3pByTrendyol both true: Unknown (the docs say micro is then false).</item>
/// <item>micro true or 3pByTrendyol true: MicroExport, unless the order was read under the TR storefront (Unknown).</item>
/// <item>micro false: Domestic, unless the storefront is a micro-export country, or an older undocumented top-level
/// field says export (shipmentPackageType or orderType naming MICRO or İHRAC, or Trendyol's former export partner PM3
/// and ARVATO in the buyer's name fields): Unknown.</item>
/// </list>
/// <para>Classify runs the rule in memory; OrderMicroExportRules runs the same rule in PostgreSQL, decoding each snapshot
/// once. MicroExportRuleTests hold the two to the same answers on every case.</para>
/// </summary>
public static class MicroExportRule
{
    public const string Trendyol = "TRENDYOL";

    /// <summary>Trendyol's micro-export countries as storefront codes (docs ~l.314-318 and ~l.4197-4211).</summary>
    public static readonly string[] MicroExportStorefronts = ["SA", "AE", "QA", "KW", "OM", "BH", "AZ", "RO", "BG", "GR", "UA", "MD", "RS", "SK", "CZ"];

    /// <summary>The buyer's name fields the former export partner's name is looked for in (top level).</summary>
    public static readonly string[] NameFields = ["customerFirstName", "customerLastName", "customerName", "customerFullName", "buyerName", "fullName"];

    /// <summary>The package type fields an undocumented micro-export type is looked for in (top level).</summary>
    public static readonly string[] TypeFields = ["shipmentPackageType", "orderType"];

    // Written for both engines alike (.NET and PostgreSQL regex): case spelled out letter by letter, JSON whitespace listed.
    private const string Space = "[ \t\r\n]*";

    private static string Letters(string word) => string.Concat(word.Select(letter =>
        char.IsAsciiLetter(letter) ? $"[{char.ToLowerInvariant(letter)}{char.ToUpperInvariant(letter)}]" : Regex.Escape(letter.ToString())));

    /// <summary>A micro-export flag key, any spelling of micro, microExport or 3pByTrendyol, at any depth.</summary>
    public static readonly string SignalKeyPattern = $"\"(?:{Letters("micro")}(?:{Letters("export")})?|{Letters("3pByTrendyol")})\"{Space}:";

    /// <summary>Undocumented: a package type text naming a micro export (MICRO, İHRAC; the ASCII letters in any case).</summary>
    public static readonly string TypeWordPattern = $"(?:{Letters("MICRO")}|İ{Letters("HRAC")})";

    public static readonly string Pm3Pattern = Letters("PM3");

    public static readonly string ArvatoPattern = Letters("ARVATO");

    /// <summary>The <c>\u0000</c> escape: PostgreSQL's JSON types refuse it, so a snapshot holding it is unreadable.</summary>
    public const string NulEscape = "\\u0000";

    private static readonly Regex SignalKey = new(SignalKeyPattern, RegexOptions.CultureInvariant);
    private static readonly Regex TypeWord = new(TypeWordPattern, RegexOptions.CultureInvariant);
    private static readonly Regex Pm3 = new(Pm3Pattern, RegexOptions.CultureInvariant);
    private static readonly Regex Arvato = new(ArvatoPattern, RegexOptions.CultureInvariant);
    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 4096 };

    /// <summary>The rule in memory, with the reason an order is Unknown.</summary>
    public static MicroExportDecision Classify(string? platformCode, string? storeFrontCode, string? customerSnapshotJson)
    {
        if (platformCode is null) return Unknown("siparişin pazaryeri bilinmiyor");
        if (platformCode != Trendyol) return new(MicroExportState.Domestic);
        if (Read(customerSnapshotJson) is not { } package) return Unknown("Trendyol paket bilgisi okunamadı");
        var (micro, partner) = (package.Micro, package.Partner);
        if (SignalKey.Count(customerSnapshotJson!) != (micro is null ? 0 : 1) + (partner is null ? 0 : 1))
            return Unknown("mikro ihracat alanı (micro, microExport veya 3pByTrendyol) paketin üst düzeyi dışında da, başka bir yazımla ya da iki kez geçiyor");
        if (micro is null && partner != "true")
            return Unknown("Trendyol paketinde mikro ihracat alanı (micro) yok");
        if (micro == "true" && partner == "true")
            return Unknown("pakette micro ve 3pByTrendyol ikisi de true; Trendyol'a göre 3pByTrendyol true iken micro false gelir");
        var export = (micro == "true" && partner is null or "false") || (partner == "true" && micro is null or "false");
        if (export)
            return string.Equals(storeFrontCode, "TR", StringComparison.OrdinalIgnoreCase)
                ? Unknown("paket mikro ihracat diyor, ancak sipariş Türkiye vitrininden (TR) okundu")
                : new(MicroExportState.MicroExport);
        if (micro == "false" && partner is null or "false")
        {
            if (storeFrontCode is not null && MicroExportStorefronts.Contains(storeFrontCode, StringComparer.OrdinalIgnoreCase))
                return Unknown($"paket yurt içi diyor (micro = false), ancak sipariş bir mikro ihracat vitrininden ({storeFrontCode.ToUpperInvariant()}) okundu");
            if (package.Types is { } types && TypeWord.IsMatch(types) || package.Names is { } names && Pm3.IsMatch(names) && Arvato.IsMatch(names))
                return Unknown("paket yurt içi diyor (micro = false), ancak paketteki başka bir alan (paket ya da sipariş türü, veya eski ihracat ortağı alıcı adı) mikro ihracat gösteriyor");
            return new(MicroExportState.Domestic);
        }
        return Unknown("pakette micro veya 3pByTrendyol alanı true ya da false dışında bir değerle geldi");
    }

    private static MicroExportDecision Unknown(string reason) => new(MicroExportState.Unknown, reason);

    // The top-level fields the rule reads, each as its JSON text (true, false, null, a quoted string…), as PostgreSQL's
    // json_to_record hands them over; Types and Names join the string-valued type and name fields with "|" (null when
    // none). Null when the snapshot is no readable JSON object. A key given twice is caught by the key count.
    private sealed record Package(string? Micro, string? Partner, string? Types, string? Names);

    private static Package? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Contains(NulEscape, StringComparison.Ordinal)) return null;
        try
        {
            using var document = JsonDocument.Parse(json, ReadOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasValidText(root)) return null;
            string? Raw(string key)
            {
                string? last = null;
                foreach (var property in root.EnumerateObject())
                    if (property.NameEquals(key)) last = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetRawText();
                return last;
            }
            string? Strings(IEnumerable<string> keys)
            {
                var texts = keys.Select(Raw).Where(text => text is not null && text.StartsWith('"')).ToList();
                return texts.Count == 0 ? null : string.Join("|", texts);
            }
            return new(Raw("micro"), Raw("3pByTrendyol"), Strings(TypeFields), Strings(NameFields));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Every key and string decodes to valid text: an escaped lone surrogate (\ud800 without its pair) is refused by
    // PostgreSQL when it reads the fields, so the snapshot is unreadable in both engines.
    private static bool HasValidText(JsonElement element)
    {
        try
        {
            return Valid(element);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        static bool Valid(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                        if (!IsWellFormed(property.Name) || !Valid(property.Value)) return false;
                    return true;
                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                        if (!Valid(item)) return false;
                    return true;
                case JsonValueKind.String:
                    return IsWellFormed(value.GetString()!);
                default:
                    return true;
            }
        }

        static bool IsWellFormed(string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (char.IsHighSurrogate(text[index]))
                {
                    if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])) return false;
                    index++;
                }
                else if (char.IsLowSurrogate(text[index])) return false;
            }
            return true;
        }
    }
}
