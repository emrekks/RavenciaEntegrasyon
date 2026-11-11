using Xunit;

namespace MarketplaceHub.Application.Tests;

// The model's shared fields (owner, 11.11.2026): a colour card's name keeps its own colour word, and the editor names the
// colours whose model-row fields differ from the opened card's.
public sealed class ProductFamilySharedFieldsTests
{
    [Fact]
    public void TitleFor_PutsEachCardsColourWhereTheOpenedCardsStands_ElseKeepsTheName()
    {
        Assert.Equal("Kadın Füme V Yaka Hırka", ProductFamilySharedFields.TitleFor("Kadın Bordo V Yaka Hırka", "Bordo", "Füme"));
        // MZ051BYC: the names hold no colour; every card takes the same name (no colour appended).
        Assert.Equal("Kışlık Triko Bluz", ProductFamilySharedFields.TitleFor("Kışlık Triko Bluz", "Siyah", "Mürdüm"));
        Assert.Equal("Kadın İndigo Hırka", ProductFamilySharedFields.TitleFor(" Kadın İndigo Hırka ", "indigo", null));
        // A colour word inside another word is not the colour.
        Assert.Equal("Grimsi Bluz", ProductFamilySharedFields.TitleFor("Grimsi Bluz", "Gri", "Siyah"));
    }

    private static ProductFamilySharedFields.CardFields Card(string color, string title, string description = "Açıklama", decimal? vat = 10m) =>
        new(Guid.NewGuid(), color, title, description, Guid.Empty, Guid.Empty, vat, "INCLUDED", null, 1);

    [Fact]
    public void Differences_NameTheColoursThatDiffer_AndIgnoreTheirOwnColourWord()
    {
        var bordo = Card("Bordo", "Kadın Bordo Hırka");
        var fume = Card("Füme", "Kadın Füme Hırka");
        var mor = Card("Mor", "Kadın Hırka", description: "Eski", vat: 20m);
        var murdum = Card("Mürdüm", "Kadın Hırka Mürdüm", description: "Eski");

        // The cards in the family's order: the answer does not depend on which card was opened.
        var differences = ProductFamilySharedFields.Differences([bordo, fume, mor, murdum]);

        Assert.Equal([ProductFamilySharedFields.Title, ProductFamilySharedFields.Description, ProductFamilySharedFields.Vat], differences.Select(item => item.Field));
        Assert.Equal(["Mor", "Mürdüm"], differences[0].Colors);
        // Two against two: the first card's value is the model's, the others are named.
        Assert.Equal(["Mor", "Mürdüm"], differences[1].Colors);
        Assert.Equal(["Mor"], differences[2].Colors);
        Assert.Empty(ProductFamilySharedFields.Differences([bordo, fume]));
        // The same VAT stored with another scale (10.0000 and 10.00) is the same VAT.
        Assert.Empty(ProductFamilySharedFields.Differences([Card("Siyah", "Bluz", vat: 10.0000m), Card("Gri", "Bluz", vat: 10.00m)]));
    }

    // "desi marka ortak" (owner, 11.11.2026): live MZ010D has sizes with desi 1 and sizes without one; KENE01 has its two
    // colours under two brands. Both show in the note.
    [Fact]
    public void Differences_NameTheColoursWhoseDesiOrBrandDiffers()
    {
        var siyah = Card("Siyah", "Bluz") with { Desis = [1.00m, 1m] };
        var gri = Card("Gri", "Bluz") with { Desis = [1m, 1m] };
        var lacivert = Card("Lacivert", "Bluz") with { Desis = [1m, null] };
        var desi = Assert.Single(ProductFamilySharedFields.Differences([siyah, gri, lacivert]));
        Assert.Equal(ProductFamilySharedFields.Desi, desi.Field);
        Assert.Equal(["Lacivert"], desi.Colors);

        var mavi = Card("Mavi", "Kot Etek") with { BrandId = Guid.NewGuid() };
        var denim = Card("Denim Rengi", "Kot Etek") with { BrandId = Guid.NewGuid() };
        var brand = Assert.Single(ProductFamilySharedFields.Differences([denim], [mavi, denim]));
        Assert.Equal(ProductFamilySharedFields.Brand, brand.Field);
        Assert.Equal(["Denim Rengi"], brand.Colors);
    }
}
