using System.Text.RegularExpressions;

namespace MarketplaceHub.Application;

// The brand a catalog import gives a panel product (owner decision 28 Oct 2026, report item 19): the import never looks
// a panel brand up by name, never opens one and never writes a brand mapping. It only reads the user's own mappings in
// reverse: the panel brand whose mapping on this connection points at the listing's marketplace brand id. A listing
// whose brand nobody mapped comes in without a brand and is listed as an unmapped marketplace brand for the user.
public static partial class ImportedBrandRule
{
    public const string IssueCode = "BRAND_MAPPING_MISSING";

    public static string IssueKeyPrefix(Guid connectionId) => $"brand-mapping-missing:{connectionId:D}:";

    public static string IssueKey(Guid connectionId, string externalBrandId) => IssueKeyPrefix(connectionId) + externalBrandId.Trim();

    public static string IssueSummary(string platformName, string externalBrandId, string? brandName) =>
        $"{platformName} markası panelde eşlenmemiş: «{(string.IsNullOrWhiteSpace(brandName) ? "-" : brandName.Trim())}» (marka no {externalBrandId.Trim()}). Bu markanın ürünleri markasız aktarıldı; Eşleştirme Ayarları › Marka Eşleme'de eşleyin.";

    // The marketplace brand name the summary carries; null when it carried none.
    public static string? NameFromSummary(string summary)
    {
        var match = SummaryName().Match(summary);
        return match.Success && match.Groups[1].Value != "-" ? match.Groups[1].Value : null;
    }

    // The panel brand the user's mappings name for the listing's brand: exactly one active panel brand, else none
    // (two panel brands mapped to one marketplace brand is the user's to settle, not the import's to guess).
    public static Guid? Resolve(IEnumerable<Guid> mappedActiveLocalBrandIds)
    {
        var ids = mappedActiveLocalBrandIds.Distinct().Take(2).ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }

    // What the product's brand becomes on an import that writes the product: the mapped brand when there is one; a new
    // product without one stays without a brand, an existing product keeps the brand it has (unknown stays unknown).
    public static Guid? NextProductBrand(bool isNewProduct, Guid? currentBrandId, Guid? mappedBrandId) =>
        mappedBrandId ?? (isNewProduct ? null : currentBrandId);

    [GeneratedRegex("«([^»]*)»")]
    private static partial Regex SummaryName();
}

public sealed record UnmappedMarketplaceBrandView(string ExternalId, string? Name, int Occurrences, DateTimeOffset LastSeenAt);
