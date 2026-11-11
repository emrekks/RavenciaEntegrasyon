using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Regression (owner, 12.11.2026): the family gallery and the model fields written to every colour card leave the order
// matching and the report's cargo desi as they were. An MZ051BYC-shaped model (7 colour cards x 5 sizes, one model code,
// one brand) is matched line by line, and each line's desi is its own variant's.
public sealed class FamilyChangesRegressionTests
{
    private const string ModelCode = "MZ051BYC";
    private static readonly string[] Colours = ["Bordo", "Haki", "Gri", "Siyah", "Lacivert", "Kahverengi", "Mürdüm"];
    private static readonly string[] Sizes = ["S", "M", "L", "XL", "XXL"];

    private sealed record Variant(Guid Id, string Colour, string Size, string Sku, string HbSku);

    private static List<Variant> Family() =>
        [.. Colours.SelectMany((colour, c) => Sizes.Select((size, s) => new Variant(Guid.NewGuid(), colour, size, $"{ModelCode}-{c * Sizes.Length + s + 1:00}", $"HBCV{c * Sizes.Length + s + 1:0000000}")))];

    private static OrderLineLinkCandidate[] Links(IEnumerable<Variant> family, string platform) =>
        [.. family.Select(variant => new OrderLineLinkCandidate(variant.Id, platform == "HEPSIBURADA" ? variant.HbSku : variant.Sku, variant.Sku, variant.Sku))];

    private static OrderLinePanelCandidate[] Panel(IEnumerable<Variant> family) =>
        [.. family.Select(variant => new OrderLinePanelCandidate(variant.Id, variant.Sku, variant.Sku))];

    // Each marketplace's order line names its variant its own way; every line of every colour finds exactly its variant
    // among the model's 35, through the connection's links and, without links, through the panel's codes.
    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    public void EveryOrderLine_OfTheModel_FindsItsOwnColourAndSize(string platform)
    {
        var family = Family();
        foreach (var variant in family)
        {
            var codes = platform switch
            {
                "TRENDYOL" => OrderLineVariantMatcher.Codes(platform, null, variant.Sku, $$"""{"barcode":"{{variant.Sku}}","merchantSku":"{{variant.Sku}}"}"""),
                "HEPSIBURADA" => OrderLineVariantMatcher.Codes(platform, variant.Sku, null, $$"""{"hbSku":"{{variant.HbSku}}","merchantSku":"{{variant.Sku}}"}"""),
                "N11" => OrderLineVariantMatcher.Codes(platform, variant.Sku, null, $$"""{"stockCode":"{{variant.Sku}}"}"""),
                "PAZARAMA" => OrderLineVariantMatcher.Codes(platform, null, null, $$$"""{"product":{"code":"{{{variant.Sku}}}"}}"""),
                _ => OrderLineVariantMatcher.Codes(platform, null, null, $$"""{"UrunBarkod":"{{ModelCode}}","VariantBarkod":"{{variant.Sku}}"}""")
            };
            Assert.Equal(new OrderLineVariantMatch(OrderLineMatchKind.Matched, variant.Id), OrderLineVariantMatcher.Match(codes, Links(family, platform), Panel(family)));
            if (platform != "HEPSIBURADA")
                Assert.Equal(new OrderLineVariantMatch(OrderLineMatchKind.Matched, variant.Id), OrderLineVariantMatcher.Match(codes, [], Panel(family)));
        }
    }

    // The model code every colour card shares is never a variant's code: a PttAVM line naming only the product
    // (UrunBarkod) stays unmatched instead of taking one of the 35.
    [Fact]
    public void TheSharedModelCode_MatchesNoVariant()
    {
        var family = Family();
        var codes = OrderLineVariantMatcher.Codes("PTTAVM", null, null, $$"""{"UrunBarkod":"{{ModelCode}}"}""");
        Assert.Equal(OrderLineVariantMatch.NotFound, OrderLineVariantMatcher.Match(codes, Links(family, "PTTAVM"), Panel(family)));
    }

    // The report's parcel desi adds up each line's own variant desi (the order line's snapshot, else the variant's): a
    // model whose colours carry different desi still costs each order by the sizes it holds.
    [Fact]
    public void AParcelOfTwoColours_AddsUpEachLinesOwnDesi()
    {
        var siyahL = (EnteredDesi: (decimal?)1.5m, ProductId: (Guid?)Guid.NewGuid(), CategoryId: (Guid?)null, Units: 1m);
        var lacivertM = (EnteredDesi: (decimal?)2m, ProductId: (Guid?)Guid.NewGuid(), CategoryId: (Guid?)null, Units: 2m);
        Assert.Equal((5.5m, ReportParcelDesi.Manual), ReportParcelDesi.ForOrder(null, [siyahL, lacivertM], ReportParcelDesi.Learned.None));
        Assert.Equal((1.5m, ReportParcelDesi.Manual), ReportParcelDesi.ForOrder(null, [siyahL], ReportParcelDesi.Learned.None));
        // A cancelled line is not in the parcel; Hepsiburada's own desi of the order wins.
        Assert.Equal((1.5m, ReportParcelDesi.Manual), ReportParcelDesi.ForOrder(null, [siyahL, lacivertM with { Units = 0m }], ReportParcelDesi.Learned.None));
        Assert.Equal((3m, ReportParcelDesi.Marketplace), ReportParcelDesi.ForOrder(3m, [siyahL, lacivertM], ReportParcelDesi.Learned.None));
    }
}
