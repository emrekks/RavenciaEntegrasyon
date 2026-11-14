using Xunit;

namespace MarketplaceHub.Application.Tests;

// The model's shared fields (owner, 11.11 and 14.11.2026): Temel Ürün Bilgileri are one value for the whole model, the
// value most colour cards hold, shown the same whichever colour row was opened; a name keeps each card's colour word.
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

    private static ProductFamilySharedFields.CardFields Card(string color, string title, string description = "Açıklama", decimal? vat = 10m, int? warranty = null) =>
        new(Guid.NewGuid(), color, title, description, Guid.Empty, Guid.Empty, vat, "INCLUDED", warranty, 1);

    // Live MZ056 (14.11.2026): five cards read "Kadın Zümrüt …" (the Gri, İndigo, Tarçın and Bordo cards too, copied from
    // the Zümrüt card), two read the name without a colour; the descriptions differ on Lacivert.
    [Fact]
    public void ModelValues_AreWhatMostCardsHold_TheNameInTheOpenedCardsColour_TheSameFromEveryRow()
    {
        const string name = "Dik Çizgi Desenli Anne Penye Bluz";
        IReadOnlyList<ProductFamilySharedFields.CardFields> cards =
        [
            Card("Gri", $"Kadın Zümrüt {name}"), Card("Lacivert", $"Kadın {name}", description: "Uzun açıklama"), Card("Pudra", $"Kadın {name}"),
            Card("İndigo", $"Kadın Zümrüt {name}"), Card("Tarçın", $"Kadın Zümrüt {name}"), Card("Zümrüt", $"Kadın Zümrüt {name}"), Card("Bordo", $"Kadın Zümrüt {name}")
        ];
        foreach (var opened in cards)
        {
            var model = ProductFamilySharedFields.ModelValues(cards, opened.Color);
            Assert.Equal($"Kadın {opened.Color} {name}", model.Title);
            Assert.Equal("Açıklama", model.Description);
        }
    }

    [Fact]
    public void ModelValues_OnATie_TakeTheFirstCards_AndCompareTheVatWhateverItsScale()
    {
        var bordo = Card("Bordo", "Kadın Hırka", description: "Bordo açıklama", vat: 10.0000m, warranty: 0);
        var fume = Card("Füme", "Kadın Hırka", description: "Füme açıklama", vat: 10.00m, warranty: 24);
        var model = ProductFamilySharedFields.ModelValues([bordo, fume], "Füme");
        Assert.Equal("Kadın Hırka", model.Title);
        Assert.Equal("Bordo açıklama", model.Description);
        Assert.Equal(10m, model.VatRate);
        Assert.Equal(0, model.WarrantyMonths);
        Assert.Equal(model, ProductFamilySharedFields.ModelValues([bordo, fume], "Bordo"));
    }

    [Fact]
    public void ModelAttributeCard_IsTheSetMostCardsHold_OnATieTheFirst()
    {
        Assert.Equal(1, ProductFamilySharedFields.ModelAttributeCard(["a", "b", "b"]));
        Assert.Equal(0, ProductFamilySharedFields.ModelAttributeCard(["a", "b"]));
    }
}
