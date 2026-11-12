using System.Text.RegularExpressions;

namespace MarketplaceHub.Application;

// The brand a catalog import gives a panel product (owner decision 12 Nov 2026, replacing the 28 Oct rule that the import
// only read the user's mappings): the listing's brand is mapped like its category and attributes. The mapping on the
// connection for the listing's brand id wins; else the panel brand with the same name is taken (and mapped); else a new
// panel brand is opened (and mapped). MarketplaceJobProcessor.ImportBrandId does it from what the listing carries, with
// no marketplace call. A brand the import cannot use (its panel brand switched off, or mapped to two panel brands) is
// listed for the user as an unmapped marketplace brand.
public static partial class ImportedBrandRule
{
    public const string IssueCode = "BRAND_MAPPING_MISSING";

    public static string IssueKeyPrefix(Guid connectionId) => $"brand-mapping-missing:{connectionId:D}:";

    public static string IssueKey(Guid connectionId, string externalBrandId) => IssueKeyPrefix(connectionId) + externalBrandId.Trim();

    public static string IssueSummary(string platformName, string externalBrandId, string? brandName) =>
        $"{platformName} markası panelde eşlenmemiş: «{SummaryBrandName(brandName)}» (marka no {externalBrandId.Trim()}). Bu markanın ürünleri markasız aktarıldı; Eşleştirme Ayarları › Marka Eşleme'de eşleyin.";

    // Why the import could not map the brand itself; the marketplace brand name stays the first «» for NameFromSummary.
    public static string UnusableSummary(string platformName, string externalBrandId, string? brandName, string reason) =>
        $"{platformName} markası aktarımda eşlenemedi: «{SummaryBrandName(brandName)}» (marka no {externalBrandId.Trim()}), {reason}. Eşleştirme Ayarları › Marka Eşleme'de kontrol edin.";

    // The marketplace brand name the summary carries; null when it carried none.
    public static string? NameFromSummary(string summary)
    {
        var match = SummaryName().Match(summary);
        return match.Success && match.Groups[1].Value != "-" ? match.Groups[1].Value : null;
    }

    // The one active panel brand the mappings name, else none (two panel brands mapped to one marketplace brand is the
    // user's to settle, not the import's to guess).
    public static Guid? Resolve(IEnumerable<Guid> mappedActiveLocalBrandIds)
    {
        var ids = mappedActiveLocalBrandIds.Distinct().Take(2).ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }

    // What the product's brand becomes on an import that writes the product (a new one, or an existing one the run
    // updates because the user left it unedited or chose to overwrite): the import's brand when there is one; a new
    // product without one stays without a brand, an existing product keeps the brand it has (unknown stays unknown).
    // An existing product the run preserves (local edits, "Mevcut Ürün Bilgilerini Güncelle" off, sizes-only runs,
    // observe-only marketplaces) never reaches this, so its brand is not touched.
    public static Guid? NextProductBrand(bool isNewProduct, Guid? currentBrandId, Guid? mappedBrandId) =>
        mappedBrandId ?? (isNewProduct ? null : currentBrandId);

    private static string SummaryBrandName(string? brandName) => string.IsNullOrWhiteSpace(brandName) ? "-" : brandName.Trim();

    [GeneratedRegex("«([^»]*)»")]
    private static partial Regex SummaryName();
}

public sealed record UnmappedMarketplaceBrandView(string ExternalId, string? Name, int Occurrences, DateTimeOffset LastSeenAt);
