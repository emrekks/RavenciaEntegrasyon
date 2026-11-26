using System.Linq.Expressions;
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
/// row flag, filters and micro-export card all read it. Three answers: MicroExport, Domestic, Unknown. Nothing is
/// inferred from a missing field, and signals inside one order that disagree make it Unknown, never a guess. The invoice
/// path stops on Unknown (InvoiceIssuePolicy.MicroExportUnknown) and waits for the owner.
/// <para>Documented signals (offline Trendyol docs, Ravencia-API-Docs/trendyol-api-son.md):</para>
/// <list type="bullet">
/// <item><c>micro</c>, a boolean at the package's top level: true for a micro-export order (FAQ of 31.12.2025, ~l.403
/// and l.802; package sample ~l.4100). It is independent of the address <c>countryCode</c>, which is not used.</item>
/// <item><c>3pByTrendyol</c>, a boolean at the package's top level: true for Trendyol's export partnership, and then
/// <c>micro</c> is false and the invoice goes to Trendyol's company (~l.3822-3824). It is no domestic sale to the buyer,
/// so it reads as MicroExport, as both earlier rules read it.</item>
/// <item>The storefront the order was read under (Order.StoreFrontCode): the micro-export countries are SA, AE, QA, KW,
/// OM, BH, AZ, RO, BG, GR, UA, MD, RS (~l.314-318) and SK, CZ (country table ~l.4197-4211). A storefront is never the
/// signal by itself; it only contradicts a flag (micro export read under TR, or a domestic flag under one of these).
/// Other storefronts (DE, null) say nothing.</item>
/// </list>
/// <para>Precedence, on a Trendyol order (every other marketplace is Domestic: none has a micro-export concept):</para>
/// <list type="number">
/// <item>No marketplace known: Unknown.</item>
/// <item>A micro / microExport / 3pByTrendyol key anywhere else than micro and 3pByTrendyol at the top level (nested,
/// another spelling, or twice): Unknown.</item>
/// <item>No top-level micro (unless 3pByTrendyol is true), or a value other than true/false: Unknown.</item>
/// <item>micro and 3pByTrendyol both true: Unknown (the docs say micro is then false).</item>
/// <item>micro true or 3pByTrendyol true: MicroExport, unless the order was read under the TR storefront (Unknown).</item>
/// <item>micro false: Domestic, unless the storefront is a micro-export country, or an older undocumented indicator says
/// export (a package or order type naming MICRO or İHRAC, or Trendyol's former export partner PM3 / ARVATO as the buyer):
/// Unknown.</item>
/// </list>
/// <para>Classify runs the rule in memory; StateRule is the same rule as an expression the database runs
/// (OrderMicroExportRules), held to the same answers by MicroExportRuleTests in memory and in PostgreSQL.</para>
/// </summary>
public static class MicroExportRule
{
    public const string Trendyol = "TRENDYOL";

    /// <summary>Trendyol's micro-export countries as storefront codes (docs ~l.314-318 and ~l.4197-4211).</summary>
    public static readonly string[] MicroExportStorefronts = ["SA", "AE", "QA", "KW", "OM", "BH", "AZ", "RO", "BG", "GR", "UA", "MD", "RS", "SK", "CZ"];

    // Written for both engines alike (.NET and PostgreSQL regex): case spelled out letter by letter, JSON whitespace listed.
    private const string Space = "[ \t\r\n]*";

    private static string Letters(string word) => string.Concat(word.Select(letter =>
        char.IsAsciiLetter(letter) ? $"[{char.ToLowerInvariant(letter)}{char.ToUpperInvariant(letter)}]" : Regex.Escape(letter.ToString())));

    private static string Key(params string[] names) => $"\"(?:{string.Join("|", names.Select(Letters))})\"{Space}:{Space}";

    /// <summary>A micro-export flag key, any spelling of micro, microExport or 3pByTrendyol, at any depth.</summary>
    public static readonly string SignalKeyPattern = $"\"(?:{Letters("micro")}(?:{Letters("export")})?|{Letters("3pByTrendyol")})\"{Space}:";

    /// <summary>Undocumented: a package or order type naming a micro export (MICRO, İHRAC; the ASCII letters in any case).</summary>
    public static readonly string TypePattern = Key("shipmentPackageType", "orderType") + $"\"[^\"]*(?:{Letters("MICRO")}|İ{Letters("HRAC")})";

    private static readonly string NameKey = Key("customerFirstName", "customerLastName", "customerName", "customerFullName", "buyerName", "fullName");

    /// <summary>Undocumented: Trendyol's former export partner as the buyer (PM3 and ARVATO in the buyer's name fields).</summary>
    public static readonly string PartnerPm3Pattern = NameKey + $"\"[^\"]*{Letters("PM3")}";

    public static readonly string PartnerArvatoPattern = NameKey + $"\"[^\"]*{Letters("ARVATO")}";

    private static readonly Regex SignalKey = new(SignalKeyPattern, RegexOptions.CultureInvariant);

    /// <summary>
    /// The top-level value of a key as JSON text (true, false, null, a quoted string, a number…), or null when the key is
    /// missing or the text is no JSON object. The database runs it as jsonb (OrderMicroExportRules maps it); only the
    /// answers true, false and null matter to the rule, and both engines write those alike.
    /// </summary>
    public static string? TopLevelJson(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(key, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                _ => value.GetRawText()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>How many micro-export flag keys the text holds at any depth (the database runs regexp_count).</summary>
    public static int SignalKeyCount(string? json) => string.IsNullOrEmpty(json) ? 0 : SignalKey.Count(json);

    /// <summary>The rule as one expression over (marketplace, storefront, customer snapshot), for the database.</summary>
    public static readonly Expression<Func<string?, string?, string?, MicroExportState>> StateRule = (platform, storefront, json) =>
        platform == null ? MicroExportState.Unknown
        : platform != Trendyol ? MicroExportState.Domestic
        : SignalKeyCount(json) != (TopLevelJson(json, "micro") != null ? 1 : 0) + (TopLevelJson(json, "3pByTrendyol") != null ? 1 : 0) ? MicroExportState.Unknown
        : TopLevelJson(json, "micro") == "true" && TopLevelJson(json, "3pByTrendyol") == "true" ? MicroExportState.Unknown
        : (TopLevelJson(json, "micro") == "true" && (TopLevelJson(json, "3pByTrendyol") == null || TopLevelJson(json, "3pByTrendyol") == "false"))
          || (TopLevelJson(json, "3pByTrendyol") == "true" && (TopLevelJson(json, "micro") == null || TopLevelJson(json, "micro") == "false"))
            ? (storefront == "TR" ? MicroExportState.Unknown : MicroExportState.MicroExport)
        : TopLevelJson(json, "micro") == "false" && (TopLevelJson(json, "3pByTrendyol") == null || TopLevelJson(json, "3pByTrendyol") == "false")
            ? (storefront != null && MicroExportStorefronts.Contains(storefront)
               || Regex.IsMatch(json!, TypePattern)
               || (Regex.IsMatch(json!, PartnerPm3Pattern) && Regex.IsMatch(json!, PartnerArvatoPattern))
                ? MicroExportState.Unknown : MicroExportState.Domestic)
        : MicroExportState.Unknown;

    /// <summary>The rule in memory, with the reason an order is Unknown.</summary>
    public static MicroExportDecision Classify(string? platformCode, string? storeFrontCode, string? customerSnapshotJson)
    {
        if (platformCode is null) return Unknown("siparişin pazaryeri bilinmiyor");
        if (platformCode != Trendyol) return new(MicroExportState.Domestic);
        var json = customerSnapshotJson;
        var micro = TopLevelJson(json, "micro");
        var partner = TopLevelJson(json, "3pByTrendyol");
        if (SignalKeyCount(json) != (micro is null ? 0 : 1) + (partner is null ? 0 : 1))
            return Unknown("mikro ihracat alanı (micro, microExport veya 3pByTrendyol) paketin üst düzeyi dışında da, başka bir yazımla ya da iki kez geçiyor");
        if (micro is null && partner != "true")
            return Unknown(IsJsonObject(json) ? "Trendyol paketinde mikro ihracat alanı (micro) yok" : "Trendyol paket bilgisi okunamadı");
        if (micro == "true" && partner == "true")
            return Unknown("pakette micro ve 3pByTrendyol ikisi de true; Trendyol'a göre 3pByTrendyol true iken micro false gelir");
        var export = (micro == "true" && partner is null or "false") || (partner == "true" && micro is null or "false");
        if (export)
            return storeFrontCode == "TR"
                ? Unknown("paket mikro ihracat diyor, ancak sipariş Türkiye vitrininden (TR) okundu")
                : new(MicroExportState.MicroExport);
        if (micro == "false" && partner is null or "false")
        {
            if (storeFrontCode is not null && MicroExportStorefronts.Contains(storeFrontCode))
                return Unknown($"paket yurt içi diyor (micro = false), ancak sipariş bir mikro ihracat vitrininden ({storeFrontCode}) okundu");
            if (Regex.IsMatch(json!, TypePattern) || (Regex.IsMatch(json!, PartnerPm3Pattern) && Regex.IsMatch(json!, PartnerArvatoPattern)))
                return Unknown("paket yurt içi diyor (micro = false), ancak paketteki başka bir alan (paket ya da sipariş türü, veya eski ihracat ortağı alıcı adı) mikro ihracat gösteriyor");
            return new(MicroExportState.Domestic);
        }
        return Unknown("pakette micro veya 3pByTrendyol alanı true ya da false dışında bir değerle geldi");
    }

    private static MicroExportDecision Unknown(string reason) => new(MicroExportState.Unknown, reason);

    private static bool IsJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
