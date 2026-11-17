using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The "Platformlarda Güncelle" summary's comparison rule (MarketplaceTextComparison) and its reading of the update
// payloads: a text the marketplace merely re-wrote is no change, and only a field the request carries is compared.
public sealed class ProductUpdatePreviewTests
{
    [Theory]
    [InlineData("Kadın Siyah Triko Bluz", "  Kadın   Siyah\tTriko\nBluz ")]
    [InlineData("Kadın Siyah Triko Bluz", "Kadın Siyah Triko Bluz")]
    [InlineData("Pamuk &amp; Keten", "Pamuk & Keten")]
    [InlineData("<p>Yumuşak triko</p><br/>uzun kollu", "<p> Yumuşak triko </p>\r\n<br>uzun kollu")]
    [InlineData("İpek &#39;özel&#39;", "İpek 'özel'")]
    [InlineData("Kazak<BR />Uzun", "Kazak<br>Uzun")]
    public void TextsThatDifferOnlyByWhitespaceEntitiesOrLineBreakTagsAreTheSame(string marketplace, string panel) =>
        Assert.True(MarketplaceTextComparison.SameText(marketplace, panel));

    [Theory]
    [InlineData("Kadın Siyah Triko Bluz", "Kadın Siyah Kruvaze Bluz")]
    [InlineData("kadın siyah triko bluz", "Kadın Siyah Triko Bluz")]
    [InlineData("<p>Triko</p>", "<div>Triko</div>")]
    [InlineData("Bluz.", "Bluz")]
    public void ARealDifferenceStays(string marketplace, string panel) =>
        Assert.False(MarketplaceTextComparison.SameText(marketplace, panel));

    [Fact]
    public void DesiIsComparedAtTwoDecimalsAndZeroIsNoValue()
    {
        Assert.True(MarketplaceTextComparison.SameDesi(2.5m, 2.50m));
        Assert.True(MarketplaceTextComparison.SameDesi(1.004m, 1m));
        Assert.False(MarketplaceTextComparison.SameDesi(2m, 3m));
        Assert.True(MarketplaceTextComparison.SameDesi(0m, null));
        Assert.Equal("2,5", MarketplaceTextComparison.DesiText(2.5m));
        Assert.Equal("—", MarketplaceTextComparison.DesiText(null));
    }

    [Fact]
    public void ADescriptionExcerptStartsAtTheFirstDifference()
    {
        var common = new string('a', 100);
        var (before, after) = MarketplaceTextComparison.Excerpts(common + "XYZ eski metin burada", common + "XYZ yeni metin burada", 10);
        Assert.Equal("…eski metin…", before);
        Assert.Equal("…yeni metin…", after);
        var (shortBefore, shortAfter) = MarketplaceTextComparison.Excerpts("Kısa", "Kısa ve uzun");
        Assert.Equal("", shortBefore);
        Assert.Equal("… ve uzun", shortAfter);
    }

    [Fact]
    public void TrendyolPayloadsAreReadByBarcode_OnlyTheFieldsTheyCarry()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        PublicationVariantDraft[] variants = [new(first, "MZ-1M", "8690001"), new(second, "MZ-1L", "8690002")];
        var approved = new ProductUpdateDraft(Guid.NewGuid(), new ProductUpdatePublication(Guid.NewGuid(), "APPROVED", "h", "{}",
            """{"items":[{"barcode":"8690001","title":"Bluz","description":"Açıklama"},{"barcode":"8690002","title":"Bluz"}]}""",
            """{"items":[{"barcode":"8690002","dimensionalWeight":2.5}]}""", """{"items":[]}"""), variants);
        var values = ProductUpdatePreviewService.SentValuesOf(approved);
        Assert.Equal("Bluz", values[first].Title);
        Assert.Equal("Açıklama", values[first].Description);
        Assert.False(values[first].DesiSent);
        Assert.False(values[second].DescriptionSent);
        Assert.False(values[second].Carries(ProductUpdateFields.Description));
        Assert.True(values[second].DesiSent);
        Assert.Equal(2.5m, values[second].Desi);

        // An unapproved product takes the whole card in one request, whatever was picked.
        var unapproved = new ProductUpdateDraft(Guid.NewGuid(), new ProductUpdatePublication(Guid.NewGuid(), "UNAPPROVED", "h",
            """{"items":[{"barcode":"8690001","title":"Bluz","description":"Uzun","dimensionalWeight":1}]}""", "{}", """{"items":[]}""", """{"items":[]}"""), variants);
        var whole = ProductUpdatePreviewService.SentValuesOf(unapproved);
        Assert.True(whole[first].Carries(ProductUpdateFields.Title) && whole[first].Carries(ProductUpdateFields.Description) && whole[first].Carries(ProductUpdateFields.Desi));
        Assert.False(whole.ContainsKey(second));
    }

    [Fact]
    public void HepsiburadaPayloadIsReadByStockCode()
    {
        var variant = Guid.NewGuid();
        var draft = new ProductUpdateDraft(Guid.NewGuid(), new ProductUpdatePublication(Guid.NewGuid(), "APPROVED", "h", "{}",
            """{"merchantId":"m","items":[{"hbSku":"HB1","merchantSku":"mz-1m","productName":"Bluz","productDescription":"Uzun","barcode":"other","desi":"2,5"}]}""",
            """{"items":[]}""", """{"items":[]}"""), [new(variant, "MZ-1M", "8690001")]);
        var values = ProductUpdatePreviewService.SentValuesOf(draft);
        Assert.Equal("Bluz", values[variant].Title);
        Assert.Equal("Uzun", values[variant].Description);
        Assert.Equal(2.5m, values[variant].Desi);
    }

    // Hepsiburada's package names every field, a missing one as null (HepsiburadaProductUpdateComposer: String() of an
    // absent attribute). The file goes with "productName": null, so the field is sent; what Hepsiburada makes of it is not
    // known, so the summary compares it as unknown instead of leaving it out.
    [Fact]
    public void AHepsiburadaFieldSentAsNullIsSent_WithNoValue()
    {
        var variant = Guid.NewGuid();
        var draft = new ProductUpdateDraft(Guid.NewGuid(), new ProductUpdatePublication(Guid.NewGuid(), "APPROVED", "h", "{}",
            """{"merchantId":"m","items":[{"hbSku":"HB1","merchantSku":"MZ-1M","productName":null,"productDescription":"Uzun","desi":null}]}""",
            """{"items":[]}""", """{"items":[]}"""), [new(variant, "MZ-1M", "8690001")]);
        var values = ProductUpdatePreviewService.SentValuesOf(draft)[variant];
        Assert.True(values.Carries(ProductUpdateFields.Title));
        Assert.Null(values.Title);
        Assert.True(values.Carries(ProductUpdateFields.Desi));
        Assert.Null(values.Desi);
        Assert.Equal("Uzun", values.Description);
    }

    // Review finding (d): with Desi alone picked on an approved Trendyol product only the variant request goes, carrying
    // dimensionalWeight; the title and description are not sent and must not be compared.
    [Fact]
    public void ADesiOnlyTrendyolUpdateCarriesNoTitleOrDescription()
    {
        var variant = Guid.NewGuid();
        var draft = new ProductUpdateDraft(Guid.NewGuid(), new ProductUpdatePublication(Guid.NewGuid(), "APPROVED", "h", "{}",
            """{"items":[]}""", """{"items":[{"barcode":"8690001","dimensionalWeight":2}]}""", """{"items":[]}"""), [new(variant, "MZ-1M", "8690001")]);
        var values = ProductUpdatePreviewService.SentValuesOf(draft)[variant];
        Assert.True(values.Carries(ProductUpdateFields.Desi));
        Assert.False(values.Carries(ProductUpdateFields.Title));
        Assert.False(values.Carries(ProductUpdateFields.Description));
        Assert.False(values.Carries(ProductUpdateFields.Images));
    }
}
