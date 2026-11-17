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
// Targets: per store, the cards the send will reach there (the window builds them with the same rule as Gönder: an
// editor family sends only its colour cards that are on the marketplace, the product list only the products on that
// store), so the summary counts exactly what Gönder sends.
public sealed record ProductUpdatePreviewRequest(IReadOnlyList<ProductUpdatePreviewTarget>? Targets, IReadOnlyList<string>? Fields);

public sealed record ProductUpdatePreviewTarget(Guid ConnectionId, IReadOnlyList<Guid>? ProductIds);

public sealed record ProductUpdatePreviewView(int MaxProducts, IReadOnlyList<ProductUpdatePreviewConnection> Connections);

// Fields: the fields the update sends on this connection that are not compared per colour (images, attributes, VAT…):
// their marketplace value is never known to the panel, so they are listed as unknown. Problems: the cards whose update
// could not be prepared (the send would refuse them the same way). KnownValuesReadAt: the oldest marketplace read behind
// a value compared here ("Pazaryerinde Okunan"); KnownValuesSentAt: the oldest accepted send behind one ("Son
// Gönderilen"). Each is null when no compared value came from it.
public sealed record ProductUpdatePreviewConnection(Guid ConnectionId, string PlatformCode, string DisplayName, IReadOnlyList<ProductUpdatePreviewModel> Models, IReadOnlyList<string> UnknownFields, IReadOnlyList<ProductUpdatePreviewProblem> Problems, DateTimeOffset? KnownValuesReadAt, DateTimeOffset? KnownValuesSentAt = null);

public sealed record ProductUpdatePreviewModel(string Label, IReadOnlyList<ProductUpdatePreviewField> Fields);

// Field: TITLE, DESCRIPTION or DESI. Unit: COLOR (a colour card) or VARIANT (a size). State: CHANGES when at least one
// unit differs, UNKNOWN when none differs but at least one has no known marketplace value, SENT_SAME when none differs or
// is unknown but at least one equals only a value the panel last sent (whether the marketplace applied it is not known,
// so it is never told as "no change"), SAME when every unit equals a value read from the marketplace. Changes: the
// differing units, known value → panel value. Same counts only values read from the marketplace; SentSame the units equal
// to the last sent value (independent review E1b, 18 Nov 2026: a wrong "no change" is worse than "unknown").
public sealed record ProductUpdatePreviewField(string Field, string Unit, string State, int Changed, int Same, int Unknown, IReadOnlyList<ProductUpdatePreviewChange> Changes, int SentSame = 0);

// Before/After: the whole title, the desi as a number, or for a description the 80 characters from the first difference.
// BeforeLength/AfterLength: a description's lengths in characters (after normalisation); null for other fields.
public sealed record ProductUpdatePreviewChange(string Label, string Before, string After, int? BeforeLength = null, int? AfterLength = null);

public sealed record ProductUpdatePreviewProblem(Guid ProductId, string Label, string Code, string Message);

public static class ProductUpdatePreviewStates
{
    public const string Changes = "CHANGES";
    public const string Same = "SAME";
    public const string Unknown = "UNKNOWN";
    public const string SentSame = "SENT_SAME";
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

// One known marketplace value: the value, where it came from (IMPORT: a product read of the marketplace carried it;
// SENT: the marketplace accepted an update carrying it) and when.
public sealed record KnownListingValue<T>(T Value, string Source, DateTimeOffset ReadAt);

// The marketplace's last known values of one listed variant, each with its own source and date. A null field is not known
// (never read and never sent, the read did not carry it, or a send left it unclear).
public sealed record KnownListingValues(KnownListingValue<string>? Title, KnownListingValue<string>? Description, KnownListingValue<decimal>? Desi);

// Where the last known marketplace values come from: catalog.marketplace_known_listing_values, written by the product
// import and by an accepted update (MarketplaceKnownListingValueStore).
public interface IMarketplaceKnownListingValues
{
    Task<IReadOnlyDictionary<Guid, KnownListingValues>> ReadAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

// How a marketplace text is compared with the panel's (title, description), so a text the marketplace merely re-wrote is
// not reported as a change:
//  1. HTML entities decoded (&amp; &nbsp; &#39; … → their characters), Unicode NFC, zero-width characters (U+200B-U+200D,
//     U+2060, U+FEFF) dropped;
//  2. HTML tag names in lower case (<P> = <p>); every <br>, <br/>, <br /> written as <br>; attributes kept (a different tag
//     is a change);
//  3. every whitespace run (spaces, tabs, line breaks, non-breaking spaces) becomes one space, and whitespace right
//     next to a tag is dropped ("<p> a </p>" = "<p>a</p>");
//  4. trimmed. The text's own case and punctuation count. Plain text against the same text wrapped in <p> is a change.
public static partial class MarketplaceTextComparison
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = ZeroWidth().Replace(WebUtility.HtmlDecode(text).Normalize(NormalizationForm.FormC), "");
        value = TagName().Replace(value, match => "<" + match.Groups[1].Value + match.Groups[2].Value.ToLowerInvariant());
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

    [GeneratedRegex("[​‌‍⁠﻿]", RegexOptions.CultureInvariant)]
    private static partial Regex ZeroWidth();

    // HTML tag names only (a "<" before a word in plain text keeps its case): the tag must end at a space, "/" or ">".
    [GeneratedRegex(@"<(/?)(p|br|b|i|u|s|em|strong|span|div|ul|ol|li|h[1-6]|table|thead|tbody|tr|td|th|a|img|hr|sub|sup|small|big|font|blockquote|center|pre|code)(?=[\s/>])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagName();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"[\s   ]+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@" <", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforeTag();

    [GeneratedRegex(@"> ", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceAfterTag();
}
