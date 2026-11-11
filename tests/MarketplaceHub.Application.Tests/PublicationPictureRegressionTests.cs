using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Regression (owner, 11.11.2026: "gönderimlerde ya da başka bir yerde sorun yaratacak mı, onların da testlerini yap"):
// every composer (Trendyol create and content update, Platformlarda/Hızlı Güncelle, Hepsiburada, N11, Pazarama, PttAVM)
// takes a variant's pictures from PublicationVariantMedia.For, now with the model's assignments. On every card shape that
// holds no other colour's assigned picture in its gallery the list is the same as before, picture by picture and in
// order; only the shape the change is for (a gallery holding pictures assigned to other colours) differs.
public sealed class PublicationPictureRegressionTests
{
    private sealed record Media(Guid? VariantId, Guid AssetId);

    private static readonly Dictionary<string, Guid> Assets = [];
    private static Guid Asset(string name) { lock (Assets) { if (!Assets.TryGetValue(name, out var id)) Assets[name] = id = Guid.NewGuid(); return id; } }

    private static List<Media> Card(string[] gallery, params (Guid Variant, string[] Pictures)[] variants) =>
        [.. gallery.Select(name => new Media(null, Asset(name))), .. variants.SelectMany(variant => variant.Pictures.Select(name => new Media(variant.Variant, Asset(name))))];

    private static PublicationColours Colours(IEnumerable<(Guid Variant, string Colour)> cardVariants, IEnumerable<(string Picture, string Colour)> assignments) =>
        new(cardVariants.ToDictionary(item => item.Variant, item => item.Colour), [.. assignments.Select(item => (Asset(item.Picture), item.Colour))]);

    private static void AssertSameAsBefore(List<Media> card, IEnumerable<Guid> variants, PublicationColours colours, string? chart = null)
    {
        var chartId = chart is null ? (Guid?)null : Asset(chart);
        foreach (var variant in variants)
            Assert.Equal(
                PublicationVariantMedia.For(card, variant, item => item.VariantId, item => item.AssetId, chartId).Select(item => item.AssetId),
                PublicationVariantMedia.For(card, variant, item => item.VariantId, item => item.AssetId, chartId, colours).Select(item => item.AssetId));
    }

    private static readonly string[] Siyah = ["s1", "s2", "s3"];
    private static readonly string[] Bordo = ["b1", "b2", "b3"];

    [Fact]
    public void AnImportedCard_GalleryOnItsFirstSize_IsUnchanged()
    {
        var (l, xl, xxl) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var card = Card(Siyah, (l, Siyah));
        AssertSameAsBefore(card, [l, xl, xxl], Colours([(l, "siyah"), (xl, "siyah"), (xxl, "siyah")], [.. Siyah.Select(p => (p, "siyah")), .. Bordo.Select(p => (p, "bordo"))]));
    }

    [Fact]
    public void ACardWithEverySizeAssigned_AndItsSizeChart_IsUnchanged()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var card = Card([.. Siyah, "tablo"], (l, [.. Siyah, "tablo"]), (xl, [.. Siyah, "tablo"]));
        AssertSameAsBefore(card, [l, xl], Colours([(l, "siyah"), (xl, "siyah")], [.. Siyah.Select(p => (p, "siyah")), ("tablo", "siyah"), ("tablo", "bordo")]), "tablo");
    }

    [Fact]
    public void ACardWithPicturesOnlyOnItsVariants_IsUnchanged()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var card = Card([], (l, Siyah));
        AssertSameAsBefore(card, [l, xl], Colours([(l, "siyah"), (xl, "siyah")], Siyah.Select(p => (p, "siyah"))));
    }

    [Fact]
    public void ACardWithoutAnyPicture_IsUnchanged()
    {
        var l = Guid.NewGuid();
        AssertSameAsBefore([], [l], Colours([(l, "siyah")], []));
    }

    [Fact]
    public void AMultiColourCard_EverySizeAssigned_IsUnchanged()
    {
        var (sl, sxl, bl, bxl) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var card = Card([.. Siyah, .. Bordo], (sl, Siyah), (sxl, Siyah), (bl, Bordo), (bxl, Bordo));
        AssertSameAsBefore(card, [sl, sxl, bl, bxl], Colours([(sl, "siyah"), (sxl, "siyah"), (bl, "bordo"), (bxl, "bordo")], [.. Siyah.Select(p => (p, "siyah")), .. Bordo.Select(p => (p, "bordo"))]));
    }

    // Changes on purpose: an unassigned size goes with its colour's assignment, not with a gallery picture its colour's
    // assigned sizes do not carry, so every size of the colour sends one list (Trendyol: one list per colour content).
    // The live dry run (11.11.2026, every model) found this shape on RYP003T and MZ051BYC only.
    [Fact]
    public void AnUnassignedSize_TakesItsColoursAssignment_NotAnExtraGalleryPicture()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var card = Card([.. Siyah, "kombin"], (xl, Siyah));
        var colours = Colours([(l, "siyah"), (xl, "siyah")], Siyah.Select(p => (p, "siyah")));
        Assert.Equal(Siyah.Concat(["kombin"]).Select(Asset), PublicationVariantMedia.For(card, l, item => item.VariantId, item => item.AssetId).Select(item => item.AssetId));
        Assert.Equal(Siyah.Select(Asset), PublicationVariantMedia.For(card, l, item => item.VariantId, item => item.AssetId, colours: colours).Select(item => item.AssetId));
        AssertSameAsBefore(card, [xl], colours);
    }

    // The one shape the change is for: an unassigned size of a card whose gallery holds another colour's assigned pictures.
    [Fact]
    public void OnlyAGalleryHoldingAnotherColoursAssignedPictures_Changes()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var card = Card([.. Bordo, .. Siyah], (xl, Siyah));
        var colours = Colours([(l, "siyah"), (xl, "siyah")], [.. Siyah.Select(p => (p, "siyah")), .. Bordo.Select(p => (p, "bordo"))]);
        Assert.Equal(Bordo.Concat(Siyah).Select(Asset), PublicationVariantMedia.For(card, l, item => item.VariantId, item => item.AssetId).Select(item => item.AssetId));
        Assert.Equal(Siyah.Select(Asset), PublicationVariantMedia.For(card, l, item => item.VariantId, item => item.AssetId, colours: colours).Select(item => item.AssetId));
        AssertSameAsBefore(card, [xl], colours);
    }
}
