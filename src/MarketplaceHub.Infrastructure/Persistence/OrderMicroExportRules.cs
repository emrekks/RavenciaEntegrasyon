using System.Linq.Expressions;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// The one rule for a micro-export order (owner, 16 Nov 2026, SIPARIS-STOK-3: the "Mikro İhracat" type label's rule is
/// the rule). An order is micro export when its package (the customer snapshot) says so in any of three ways:
/// <list type="bullet">
/// <item>a flag micro, microExport or 3pByTrendyol is true (Trendyol's 3P partner exports send micro = false with
/// 3pByTrendyol = true);</item>
/// <item>shipmentPackageType or orderType names it (MICRO, İHRAC);</item>
/// <item>the buyer is Trendyol's former export partner (PM3 and ARVATO in the buyer's name), for old snapshots that
/// predate the flags.</item>
/// </list>
/// Any one positive indicator makes the order micro export, whatever the order of the fields: an explicit micro = false
/// does not cancel 3pByTrendyol = true (Trendyol documents exactly that pair for its export partnership). A missing field
/// says nothing. The label read the flags by first match before (JsonText), so {"micro":false,"3pByTrendyol":true} was
/// domestic and the same fields the other way round export; live on 16 Nov 2026 no order carried such a pair (0). The
/// indicators are read at any depth of the package, as the label always did: Trendyol sends them at the package's top
/// level (docs: the package fields micro and 3pByTrendyol) and live no order has micro = true anywhere but there (0 of
/// 1845 Trendyol orders), so top level and any depth are the same orders; any depth is what both engines can match alike.
/// The label (OrderSnapshotReader.Customer), the row's flag (RowMarks), the list's Mikro İhracat and Fatura Bölgesi filters
/// and the micro-export card all read this rule: the label and the flag in memory (Matches), the filters and the card in
/// the database (InDatabase), which are the same expression (OrderMicroExportRulesTests). The invoice keeps its own rule
/// (InvoiceIssuePolicy.IsMicroExport): which invoice an order gets is the owner's tax rule, not this one.
/// </summary>
internal static class OrderMicroExportRules
{
    // Written for both engines alike: case is spelled out letter by letter ([mM][iI]…) rather than left to a case-insensitive
    // match, because PostgreSQL folds İ and ı by its collation and .NET by its own table; JSON whitespace is listed
    // (space, tab, CR, LF) rather than \s, whose set differs between the two.
    private const string Space = "[ \t\r\n]*";

    private static string Letters(string word) => string.Concat(word.Select(letter =>
        char.IsAsciiLetter(letter) ? $"[{char.ToLowerInvariant(letter)}{char.ToUpperInvariant(letter)}]" : Regex.Escape(letter.ToString())));

    private static string Key(params string[] names) => $"\"(?:{string.Join("|", names.Select(Letters))})\"{Space}:{Space}";

    // A flag that reads as true: the JSON literal, the number 1, or the text "true" (any case, padded), "1", "YES", "EVET".
    internal static readonly string FlagPattern =
        Key("microExport", "micro", "3pByTrendyol") + $"(?:{Letters("true")}|1|\"{Space}{Letters("true")}{Space}\"|\"1\"|\"YES\"|\"EVET\"){Space}[,}}\\]]";

    // The package type's text naming a micro export (MICRO, İHRAC; the ASCII letters in any case).
    internal static readonly string TypePattern = Key("shipmentPackageType", "orderType") + $"\"[^\"]*(?:{Letters("MICRO")}|İ{Letters("HRAC")})";

    // Trendyol's former export partner as the buyer: PM3 and ARVATO in the buyer's name fields.
    private static readonly string NameKey = Key("customerFirstName", "customerLastName", "customerName", "customerFullName", "buyerName", "fullName");
    internal static readonly string PartnerPm3Pattern = NameKey + $"\"[^\"]*{Letters("PM3")}";
    internal static readonly string PartnerArvatoPattern = NameKey + $"\"[^\"]*{Letters("ARVATO")}";

    /// <summary>The rule on a customer snapshot, as the database runs it (Npgsql writes Regex.IsMatch as ~).</summary>
    internal static readonly Expression<Func<string, bool>> SnapshotIsMicroExport = json =>
        Regex.IsMatch(json, FlagPattern)
        || Regex.IsMatch(json, TypePattern)
        || (Regex.IsMatch(json, PartnerPm3Pattern) && Regex.IsMatch(json, PartnerArvatoPattern));

    private static readonly Func<string, bool> Compiled = SnapshotIsMicroExport.Compile();

    /// <summary>The rule in memory: the same expression, compiled.</summary>
    internal static bool Matches(string? customerSnapshotJson) => !string.IsNullOrWhiteSpace(customerSnapshotJson) && Compiled(customerSnapshotJson);

    /// <summary>The rule as a database condition on an order.</summary>
    internal static readonly Expression<Func<Order, bool>> InDatabase = PolicyExpressions.On<Order, string>(SnapshotIsMicroExport, order => order.CustomerSnapshotJson);

    /// <summary>An order that is no micro export: the "Türkiye" invoice region.</summary>
    internal static readonly Expression<Func<Order, bool>> NotInDatabase = Expression.Lambda<Func<Order, bool>>(Expression.Not(InDatabase.Body), InDatabase.Parameters);
}
