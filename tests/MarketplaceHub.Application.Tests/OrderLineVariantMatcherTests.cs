using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OrderLineVariantMatcherTests
{
    private static readonly Guid VariantA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid VariantB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    // A Hepsiburada line as it is stored live: Sku is the merchant SKU, Barcode the barcode padded to 13 characters,
    // and the record's "sku" is Hepsiburada's own hbSku, the id the connection's variant links are keyed by.
    private const string HepsiburadaSnapshot = """{"sku":"HBCV00008VUNWZ","merchantSku":"KRL01501K04","barcode":"00KRL01501K04","quantity":1}""";

    [Fact]
    public void Codes_ReadHepsiburadasHbSkuMerchantSkuAndBarcode()
    {
        var codes = OrderLineVariantMatcher.Codes("HEPSIBURADA", "KRL01501K04", "00KRL01501K04", HepsiburadaSnapshot);

        Assert.Equal("HBCV00008VUNWZ", codes.MarketplaceVariantId);
        Assert.Equal(["KRL01501K04"], codes.Skus);
        Assert.Equal(["00KRL01501K04"], codes.Barcodes);
        Assert.Equal("KRL01501K04", codes.DisplayCode);
    }

    [Fact]
    public void Match_HepsiburadaLineFindsItsVariantByTheHbSkuLinkWhenThePanelCodeDiffers()
    {
        var codes = OrderLineVariantMatcher.Codes("HEPSIBURADA", "KRL01501K04", "00KRL01501K04", HepsiburadaSnapshot);
        var links = new[] { new OrderLineLinkCandidate(VariantA, "HBCV00008VUNWZ", null, "SELLER-OTHER") };

        var match = OrderLineVariantMatcher.Match(codes, links, [new OrderLinePanelCandidate(VariantB, "UNRELATED", "UNRELATED")]);

        Assert.Equal(OrderLineMatchKind.Matched, match.Kind);
        Assert.Equal(VariantA, match.VariantId);
    }

    [Fact]
    public void Match_PaddedHepsiburadaBarcodeFindsThePanelBarcode()
    {
        // The panel's stock code differs from the merchant SKU; only the zero-padded barcode names the variant.
        var codes = OrderLineVariantMatcher.Codes("HEPSIBURADA", "SELLER-SKU", "00KRL01501K04", "{}");

        var match = OrderLineVariantMatcher.Match(codes, [], [new OrderLinePanelCandidate(VariantA, "PANEL-SKU", "KRL01501K04")]);

        Assert.Equal(VariantA, match.VariantId);
    }

    [Fact]
    public void Match_PaddingIsRemovedOneZeroAtATime()
    {
        var codes = OrderLineVariantMatcher.Codes("HEPSIBURADA", null, "0000077CY010", "{}");
        var variants = new[] { new OrderLinePanelCandidate(VariantA, "0077CY010", "0077CY010"), new OrderLinePanelCandidate(VariantB, "77CY010", "77CY010") };

        Assert.Equal(VariantA, OrderLineVariantMatcher.Match(codes, [], variants).VariantId);
    }

    [Fact]
    public void Match_LeavesTheLeadingZerosOfANumericBarcode()
    {
        var codes = OrderLineVariantMatcher.Codes("TRENDYOL", null, "08690000000011", "{}");

        Assert.Equal(OrderLineMatchKind.NotFound, OrderLineVariantMatcher.Match(codes, [], [new OrderLinePanelCandidate(VariantA, "SKU-1", "8690000000011")]).Kind);
    }

    [Fact]
    public void Match_ALinkWinsOverThePanelAndACodeTwoLinksShareNamesNoVariant()
    {
        var codes = OrderLineVariantMatcher.Codes("TRENDYOL", "SKU-1", "BAR-1", "{}");
        var panel = new[] { new OrderLinePanelCandidate(VariantB, "SKU-1", "BAR-1") };

        Assert.Equal(VariantA, OrderLineVariantMatcher.Match(codes, [new OrderLineLinkCandidate(VariantA, "1001", "BAR-1", null)], panel).VariantId);
        var shared = new[] { new OrderLineLinkCandidate(VariantA, "1001", "BAR-1", null), new OrderLineLinkCandidate(VariantB, "1002", "BAR-1", null) };
        Assert.Equal(OrderLineMatchKind.Ambiguous, OrderLineVariantMatcher.Match(codes, shared, panel).Kind);
    }

    [Fact]
    public void Match_CodesCompareByTheSharedKeyAndAStoredSharedKeyWinsOverALegacyOne()
    {
        var codes = OrderLineVariantMatcher.Codes("N11", "abc_01", null, "{}");
        var variants = new[] { new OrderLinePanelCandidate(VariantA, "ABC_01", null), new OrderLinePanelCandidate(VariantB, "ABC-01", null) };

        Assert.Equal(VariantB, OrderLineVariantMatcher.Match(codes, [], variants).VariantId);
    }

    [Fact]
    public void Match_ABarcodeStoredAsThePanelStockCodeStillMatches()
    {
        var codes = OrderLineVariantMatcher.Codes("PTTAVM", "FAMILY", "MZ125CD021", "{}");

        Assert.Equal(VariantA, OrderLineVariantMatcher.Match(codes, [], [new OrderLinePanelCandidate(VariantA, "MZ125CD021", null)]).VariantId);
    }

    [Fact]
    public void Codes_NeverTakePttAvmsFamilyBarcode()
    {
        var codes = OrderLineVariantMatcher.Codes("PTTAVM", "MZ125CD021", "MZ125CD021", """{"UrunBarkod":"MZ125CD","VariantBarkod":"MZ125CD021","UrunId":"1523598789"}""");

        Assert.DoesNotContain("MZ125CD", codes.All());
        Assert.Equal(OrderLineMatchKind.NotFound, OrderLineVariantMatcher.Match(codes, [], [new OrderLinePanelCandidate(VariantA, "MZ125CD", "MZ125CD")]).Kind);
    }

    [Fact]
    public void Codes_ReadTheOtherMarketplacesRecords()
    {
        Assert.Contains("RYP003T18", OrderLineVariantMatcher.Codes("PAZARAMA", null, null, """{"product":{"code":"RYP003T18","stockCode":"RYP003T18"}}""").Skus);
        var shopify = OrderLineVariantMatcher.Codes("SHOPIFY", null, null, """{"sku":"MZ1","variant":{"sku":"MZ1","barcodes":{"nodes":[{"value":"8690001"}]}}}""");
        Assert.Equal(["MZ1"], shopify.Skus);
        Assert.Equal(["8690001"], shopify.Barcodes);
        var trendyol = OrderLineVariantMatcher.Codes("TRENDYOL", "RYP00103", "RYP00103-B", """{"barcode":"RYP00103-B","merchantSku":"RYP00103","stockCode":"RYP-STOCK"}""");
        Assert.Equal(["RYP00103", "RYP-STOCK"], trendyol.Skus);
        Assert.Null(trendyol.MarketplaceVariantId);
        Assert.True(OrderLineVariantMatcher.Codes("TRENDYOL", null, null, "not json").IsEmpty);
    }

    [Fact]
    public void Match_ALineWithoutCodesOrWithoutAVariantStaysUnmatched()
    {
        Assert.Equal(OrderLineMatchKind.NotFound, OrderLineVariantMatcher.Match(OrderLineCodes.None, [], [new OrderLinePanelCandidate(VariantA, "X", "X")]).Kind);
        Assert.Equal(OrderLineMatchKind.NotFound, OrderLineVariantMatcher.Match(OrderLineVariantMatcher.Codes("TRENDYOL", "MZ001TK23", "MZ001TK23", "{}"), [], [new OrderLinePanelCandidate(VariantA, "MZ001TK2", "MZ001TK2")]).Kind);
    }

    [Theory]
    [InlineData("DELIVERED", true)]
    [InlineData("shipped", true)]
    [InlineData("CANCELLED", true)]
    [InlineData("RETURNED", true)]
    [InlineData("UNDELIVERED", true)]
    [InlineData("NEW", false)]
    [InlineData("PROCESSING", false)]
    [InlineData("PAYMENT_PENDING", false)]
    [InlineData(null, false)]
    public void LateMatch_OnlyASettledOrderAdoptsItsUnits(string? status, bool settled) =>
        Assert.Equal(settled, OrderLineLateMatchStockPolicy.OrderHasSettled(status));

    [Theory]
    // A delivered Hepsiburada line whose shipped quantity was never projected still committed its unit.
    [InlineData(1, 0, 0, 0, 1)]
    [InlineData(2, 0, 2, 2, 2)]
    [InlineData(1, 1, 0, 0, 0)]
    [InlineData(3, 1, 1, 1, 2)]
    public void LateMatch_TheBaselineIsWhatTheLineCommits(decimal ordered, decimal cancelled, decimal shipped, decimal delivered, decimal expected) =>
        Assert.Equal(expected, OrderLineLateMatchStockPolicy.AdoptedBaseline(ordered, cancelled, shipped, delivered));
}
