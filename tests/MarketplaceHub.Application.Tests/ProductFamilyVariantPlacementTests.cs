using Xunit;
using Row = MarketplaceHub.Application.ProductFamilyVariantPlacement.ExistingRow;

namespace MarketplaceHub.Application.Tests;

// Live 2026-11-01 (MZ021Y-CD, seven colour cards): Varyantları Oluştur on the Mavi card saved 18 new rows of six other
// colours on the Mavi card. A new row now goes to the card of its own colour.
public sealed class ProductFamilyVariantPlacementTests
{
    private static readonly Guid Mavi = Guid.Parse("01a178e0-9ecb-790a-bbf7-30da96781869");
    private static readonly Guid Bordo = Guid.Parse("01a178e0-9186-7325-964c-02c23b7bfc32");
    private static readonly Guid Murdum = Guid.Parse("01a198bc-37ca-7c58-903f-7ca1f48682fd");

    private static IEnumerable<Row> Card(Guid productId, string color, params string[] sizes) =>
        sizes.Select(size => new Row(productId, $"Renk: {color} | Beden: {size}"));

    private static IReadOnlyDictionary<string, string> Options(string color, string size) =>
        new Dictionary<string, string> { ["Renk"] = color, ["Beden"] = size };

    private static List<Row> Family() =>
        [.. Card(Mavi, "Mavi", "3XL", "4XL", "5XL"), .. Card(Bordo, "Bordo", "3XL", "4XL", "5XL"), .. Card(Murdum, "Mürdüm", "L", "XL", "2XL")];

    [Fact]
    public void NewRows_GoToTheCardOfTheirColour_WhicheverCardIsOpen()
    {
        var placement = ProductFamilyVariantPlacement.Place(Mavi, [Mavi, Bordo, Murdum], Family(),
            [Options("Bordo", "L"), Options("Mavi", "L"), Options("MÜRDÜM", "3XL"), Options("murdum", "4XL")]);

        Assert.Empty(placement.ColorsWithoutCard);
        Assert.Equal([Bordo, Mavi, Murdum, Murdum], placement.ProductIds);
    }

    [Fact]
    public void AColourWithoutACard_InAFamilyOfSeveralCards_IsNamedForANewCard_PerRow()
    {
        var placement = ProductFamilyVariantPlacement.Place(Mavi, [Mavi, Bordo, Murdum], Family(),
            [Options("Haki", "L"), Options("Bordo", "L"), Options("haki", "XL"), Options("Siyah", "L")]);

        Assert.Equal(["Haki", "Siyah"], placement.ColorsWithoutCard);
        Assert.Equal(["Haki", null, "Haki", "Siyah"], placement.RowColorsWithoutCard);
        Assert.Equal(Bordo, placement.ProductIds[1]);
    }

    // Live 2026-11-06 (MZ028IH): the opened Siyah card's title names its colour; the new card names its own.
    [Theory]
    [InlineData("Kadın Siyah V Yaka Düğmeli Anne Triko Hırka", "Siyah", "Zümrüt", "Kadın Zümrüt V Yaka Düğmeli Anne Triko Hırka")]
    [InlineData("KADIN SİYAH HIRKA", "siyah", "Zümrüt", "KADIN Zümrüt HIRKA")]
    [InlineData("Kadın İndigo Hırka", "Indigo", "Mor", "Kadın Mor Hırka")]
    [InlineData("Kadın Siyahımsı Hırka", "Siyah", "Mor", "Kadın Siyahımsı Hırka Mor")]
    [InlineData("Kadın Hırka", null, "Mor", "Kadın Hırka Mor")]
    public void ANewColourCard_TakesTheOpenedTitle_WithItsOwnColour(string title, string? openedColor, string newColor, string expected) =>
        Assert.Equal(expected, ProductFamilyNewColorCard.Title(title, openedColor, newColor));

    [Fact]
    public void OptionKey_IgnoresSpellingAndOrder()
    {
        Assert.Equal(ProductFamilyVariantPlacement.OptionKey("Renk: SİYAH | Beden: Standart"), ProductFamilyVariantPlacement.OptionKey(new Dictionary<string, string> { ["Beden"] = "standart", ["renk"] = "Siyah" }));
        Assert.NotEqual(ProductFamilyVariantPlacement.OptionKey("Renk: Siyah | Beden: Standart"), ProductFamilyVariantPlacement.OptionKey(Options("Zümrüt", "Standart")));
        Assert.Null(ProductFamilyVariantPlacement.OptionKey((string?)null));
    }

    [Fact]
    public void ASingleCard_TakesEveryNewColour_AndARowWithoutColourStaysOnTheOpenedCard()
    {
        var single = ProductFamilyVariantPlacement.Place(Mavi, [Mavi], Card(Mavi, "Mavi", "L").ToList(), [Options("Haki", "L")]);
        Assert.Empty(single.ColorsWithoutCard);
        Assert.Equal([Mavi], single.ProductIds);

        var withoutColour = ProductFamilyVariantPlacement.Place(Mavi, [Mavi, Bordo], Family(), [new Dictionary<string, string> { ["Beden"] = "6XL" }, null]);
        Assert.Empty(withoutColour.ColorsWithoutCard);
        Assert.Equal([Mavi, Mavi], withoutColour.ProductIds);
    }

    [Fact]
    public void AFamilyNotSplitByColour_KeepsNewRowsOnTheOpenedCard()
    {
        var rows = new List<Row> { new(Mavi, "Beden: L"), new(Bordo, "Beden: XL") };
        var placement = ProductFamilyVariantPlacement.Place(Mavi, [Mavi, Bordo], rows, [Options("Haki", "L")]);

        Assert.Empty(placement.ColorsWithoutCard);
        Assert.Equal([Mavi], placement.ProductIds);
    }

    // The Mavi card as live: its own rows plus wrong Bordo rows. A new Bordo row goes to the Bordo card (the card holding
    // only Bordo), even from the Mavi card that also holds Bordo rows.
    [Fact]
    public void ACardHoldingOnlyTheColour_WinsOverAMixedCard()
    {
        var rows = Family().Concat(Card(Mavi, "Bordo", "L", "XL", "2XL")).ToList();
        var placement = ProductFamilyVariantPlacement.Place(Mavi, [Mavi, Bordo, Murdum], rows, [Options("Bordo", "6XL")]);

        Assert.Equal([Bordo], placement.ProductIds);
    }

    // Deleting a card's every row and creating new rows of its colour in one save keeps them on that card.
    [Fact]
    public void RowsBeingDeleted_StillNameTheirCard_WhenNoOtherRowHoldsTheColour()
    {
        var rows = Card(Mavi, "Mavi", "L").Concat(Card(Bordo, "Bordo", "L").Select(row => row with { Deleting = true })).ToList();
        var placement = ProductFamilyVariantPlacement.Place(Mavi, [Mavi, Bordo], rows, [Options("Bordo", "L")]);

        Assert.Empty(placement.ColorsWithoutCard);
        Assert.Equal([Bordo], placement.ProductIds);
    }

    [Fact]
    public void TheRenkOption_IsReadBeforeAWebColour()
    {
        Assert.Equal("Bordo", ProductFamilyVariantPlacement.ColorOf(new Dictionary<string, string> { ["Web Color"] = "Kırmızı", ["renk"] = " Bordo " }));
        Assert.Equal("Kırmızı", ProductFamilyVariantPlacement.ColorOf(new Dictionary<string, string> { ["Web Color"] = "Kırmızı", ["Beden"] = "L" }));
        Assert.Null(ProductFamilyVariantPlacement.ColorOf(new Dictionary<string, string> { ["Beden"] = "L" }));
    }
}
