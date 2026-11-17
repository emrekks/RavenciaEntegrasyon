using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Application;

// "Platformlarda Güncelle" summary (owner, 17 Nov 2026): before the owner presses Gönder, which fields the update will
// change on each marketplace, per model, compared with the marketplace's last known value. Read only: no job, no write,
// no marketplace call. The panel value is exactly what the update composer would send (its payload, not a re-derived
// value); the marketplace value comes from IMarketplaceKnownListingValues, and a field whose marketplace value is not
// known is reported as unknown, never as unchanged.
public sealed record ProductUpdatePreviewRequest(IReadOnlyList<Guid>? ProductIds, IReadOnlyList<Guid>? ConnectionIds, IReadOnlyList<string>? Fields);

public sealed record ProductUpdatePreviewView(int MaxProducts, IReadOnlyList<ProductUpdatePreviewConnection> Connections);

// Fields: the fields the update sends on this connection that are not compared per colour (images, attributes, VAT…):
// their marketplace value is never known to the panel, so they are listed as unknown. Problems: the cards whose update
// could not be prepared (the send would refuse them the same way). KnownValuesReadAt: the oldest read behind a known
// marketplace value used here (how fresh the comparison is); null when no value was known.
public sealed record ProductUpdatePreviewConnection(Guid ConnectionId, string PlatformCode, string DisplayName, IReadOnlyList<ProductUpdatePreviewModel> Models, IReadOnlyList<string> UnknownFields, IReadOnlyList<ProductUpdatePreviewProblem> Problems, DateTimeOffset? KnownValuesReadAt);

public sealed record ProductUpdatePreviewModel(string Label, IReadOnlyList<ProductUpdatePreviewField> Fields);

// Field: TITLE, DESCRIPTION or DESI. Unit: COLOR (a colour card) or VARIANT (a size). State: CHANGES when at least one
// unit differs, UNKNOWN when none differs but at least one has no known marketplace value, SAME when every unit is known
// and equal. Changes: the differing units, marketplace value → panel value.
public sealed record ProductUpdatePreviewField(string Field, string Unit, string State, int Changed, int Same, int Unknown, IReadOnlyList<ProductUpdatePreviewChange> Changes);

// Before/After: the whole title, the desi as a number, or for a description the 80 characters from the first difference.
// BeforeLength/AfterLength: a description's lengths in characters (after normalisation); null for other fields.
public sealed record ProductUpdatePreviewChange(string Label, string Before, string After, int? BeforeLength = null, int? AfterLength = null);

public sealed record ProductUpdatePreviewProblem(Guid ProductId, string Label, string Code, string Message);

public static class ProductUpdatePreviewStates
{
    public const string Changes = "CHANGES";
    public const string Same = "SAME";
    public const string Unknown = "UNKNOWN";
    public const string ColorUnit = "COLOR";
    public const string VariantUnit = "VARIANT";
}

public interface IProductUpdatePreviewService
{
    // At most ProductUpdatePreviewPolicy.MaxProducts cards per request; more is refused (422), the send is not affected.
    Task<ServiceResult<ProductUpdatePreviewView>> PreviewAsync(Guid tenantId, ProductUpdatePreviewRequest request, CancellationToken cancellationToken);
}

public static class ProductUpdatePreviewPolicy
{
    // Each card costs one composer run per selected store (a dozen reads); 50 cards keep the answer within a few seconds.
    public const int MaxProducts = 50;
    public const int MaxConnections = 20;
    // A description change shows this many characters from its first difference.
    public const int ExcerptLength = 80;
}

// The marketplace's last known values of one listed variant, as a catalog read saw them, and when. A null field is not
// known (never read, or the read did not carry it).
public sealed record KnownListingValues(string? Title, string? Description, decimal? Desi, DateTimeOffset ReadAt);

// Where the last known marketplace values come from. Today the panel keeps none of them (17 Nov 2026): the product import
// reads the title, description and desi but only keeps a hash of the listing (marketplace_product_links.
// LastImportedPayloadHash) and its staging rows are deleted when the import completes; listing profiles made by an
// import carry no override. So the production source answers nothing and every field is reported as unknown until a
// store is added (see the report of the feature branch feat/guncelleme-fark-ozeti).
public interface IMarketplaceKnownListingValues
{
    Task<IReadOnlyDictionary<Guid, KnownListingValues>> ReadAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

// How a marketplace text is compared with the panel's (title, description), so a text the marketplace merely re-wrote is
// not reported as a change:
//  1. HTML entities decoded (&amp; &nbsp; &#39; … → their characters), Unicode NFC;
//  2. every <br>, <br/>, <br /> written as <br>; tag names and attributes otherwise kept (a different tag is a change);
//  3. every whitespace run (spaces, tabs, line breaks, non-breaking spaces) becomes one space, and whitespace right
//     next to a tag is dropped ("<p> a </p>" = "<p>a</p>");
//  4. trimmed. Case and punctuation count.
public static partial class MarketplaceTextComparison
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = WebUtility.HtmlDecode(text).Normalize(NormalizationForm.FormC);
        value = LineBreakTag().Replace(value, "<br>");
        value = Whitespace().Replace(value, " ");
        value = SpaceBeforeTag().Replace(value, "<");
        value = SpaceAfterTag().Replace(value, ">");
        return value.Trim();
    }

    public static bool SameText(string? marketplace, string? panel) => string.Equals(Normalize(marketplace), Normalize(panel), StringComparison.Ordinal);

    // The desi as both sides carry it: two decimals; a missing or non-positive value is no value (ImportedDesi.From).
    public static bool SameDesi(decimal? marketplace, decimal? panel) => ImportedDesi.From(marketplace) == ImportedDesi.From(panel);

    public static string DesiText(decimal? value) => ImportedDesi.From(value) is { } desi ? desi.ToString("0.##", CultureInfo.GetCultureInfo("tr-TR")) : "—";

    // The two normalised texts from their first differing character, at most `length` characters each.
    public static (string Before, string After) Excerpts(string? marketplace, string? panel, int length = ProductUpdatePreviewPolicy.ExcerptLength)
    {
        var before = Normalize(marketplace);
        var after = Normalize(panel);
        var start = 0;
        var shortest = Math.Min(before.Length, after.Length);
        while (start < shortest && before[start] == after[start]) start++;
        return (Cut(before, start, length), Cut(after, start, length));
    }

    private static string Cut(string text, int start, int length)
    {
        if (start >= text.Length) return "";
        var part = text.Substring(start, Math.Min(length, text.Length - start));
        return (start > 0 ? "…" : "") + part + (start + length < text.Length ? "…" : "");
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"[\s   ]+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@" <", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforeTag();

    [GeneratedRegex(@"> ", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceAfterTag();
}
