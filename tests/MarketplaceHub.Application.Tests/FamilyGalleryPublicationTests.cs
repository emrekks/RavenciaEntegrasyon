using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// What Trendyol gets per colour card, pinned before and after the editor began writing a model's gallery colour by
// colour (owner, 11.11.2026, MZ051BYC). The composer is unchanged: each colour card is composed from its own media rows
// (PublicationVariantMedia.For, then the 8-picture limit with the size chart kept last). These tests fix the selection
// on the card shapes live holds and on the shapes the editor now writes, so a colour never sends another colour's
// pictures and every size of a colour sends the same list.
public sealed class FamilyGalleryPublicationTests
{
    private sealed record Media(Guid? VariantId, Guid AssetId, string Url);

    private static readonly Dictionary<string, Guid> Assets = new();
    private static Guid Asset(string name) { lock (Assets) { if (!Assets.TryGetValue(name, out var id)) Assets[name] = id = Guid.NewGuid(); return id; } }
    private static string Url(string name) => $"https://cdn.example.com/mz051byc/{name}.jpg";

    // A card's rows in SortOrder: the gallery (product level), then each variant's own pictures.
    private static List<Media> Card(string[] gallery, params (Guid Variant, string[] Pictures)[] variants) =>
        [.. gallery.Select(name => new Media(null, Asset(name), Url(name))), .. variants.SelectMany(variant => variant.Pictures.Select(name => new Media(variant.Variant, Asset(name), Url(name))))];

    // The images one variant goes to Trendyol with, as the composer selects them.
    private static string[] Images(List<Media> card, Guid variant, string? chart = null, PublicationColours? colours = null)
    {
        var chartId = chart is null ? (Guid?)null : Asset(chart);
        var pictures = PublicationVariantMedia.For(card, variant, item => item.VariantId, item => item.AssetId, chartId, colours).Select(item => (Id: item.AssetId, Url: (string?)item.Url)).ToList();
        return [.. ProductPublicationComposer.SelectPublicationImageUrls(pictures.Select(item => item.Url), PublicationVariantMedia.SizeChartUrl(pictures, chartId)).Select(url => url[(url.LastIndexOf('/') + 1)..^4])];
    }

    private static readonly string[] Bordo = ["bordo-1", "bordo-2", "bordo-3"];
    private static readonly string[] Haki = ["haki-1", "haki-2", "haki-3"];
    private static readonly string[] Gri = ["gri-1", "gri-2", "gri-3"];
    private static readonly string[] Siyah = ["siyah-1", "siyah-2", "siyah-3"];
    private static readonly string[] Lacivert = ["lacivert-1", "lacivert-2", "lacivert-3"];
    private static readonly string[] NewUploads = ["kahve-1", "kahve-2", "kahve-3", "murdum-1", "murdum-2", "murdum-3"];

    // Live 11.11.2026: the Siyah card's gallery holds every colour's pictures (saves from the Siyah row copied them there)
    // and only its XL size has pictures of its own. Characterization of today's data: XL goes with Siyah's pictures, the
    // other sizes with the gallery's first eight, Bordo's first. This is the wrong content the new editor's save removes.
    [Fact]
    public void Before_TheLiveSiyahCard_SendsOtherColoursPicturesForTheSizesWithoutOwnPictures()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var siyah = Card([.. Bordo, .. Haki, .. Gri, .. Siyah, .. Lacivert, .. NewUploads], (xl, Siyah));

        Assert.Equal(Siyah, Images(siyah, xl));
        Assert.Equal(["bordo-1", "bordo-2", "bordo-3", "haki-1", "haki-2", "haki-3", "gri-1", "gri-2"], Images(siyah, l));
    }

    // An imported colour card (gallery and its first size holding the same three pictures) is already right and the
    // editor leaves it as it is: every size goes with the colour's own three pictures.
    [Fact]
    public void AnImportedColourCard_SendsItsOwnPicturesFromEverySize()
    {
        var (l, xl, xxl) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bordo = Card(Bordo, (xl, Bordo));
        Assert.All(new[] { l, xl, xxl }, size => Assert.Equal(Bordo, Images(bordo, size)));
    }

    // After the owner assigns the six new uploads to Kahverengi and Mürdüm and presses Kaydet (from any colour row): each
    // colour card holds its own pictures and the shared size chart; Siyah keeps only its own. Every size of a colour goes
    // with the same list, the chart last.
    [Fact]
    public void After_EachColourCardHoldsItsOwnPictures_AndTheSharedSizeChartLast()
    {
        const string chart = "beden-tablosu";
        var (siyahL, siyahXl) = (Guid.NewGuid(), Guid.NewGuid());
        var siyah = Card([.. Siyah, chart], (siyahXl, Siyah));
        Assert.Equal([.. Siyah, chart], Images(siyah, siyahL, chart));
        Assert.Equal([.. Siyah, chart], Images(siyah, siyahXl, chart));

        // The new Kahverengi card: its pictures assigned to every size (Seçeneklere Görsel Ata), the chart in its gallery.
        var kahve = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var kahverengi = Card(["kahve-1", "kahve-2", "kahve-3", chart], [.. kahve.Select(size => (size, new[] { "kahve-1", "kahve-2", "kahve-3" }))]);
        Assert.All(kahve, size => Assert.Equal(["kahve-1", "kahve-2", "kahve-3", chart], Images(kahverengi, size, chart)));
    }

    // Before the assignment rule (no model knowledge): a size without its own pictures went with the whole gallery, the
    // other colours' pictures copied onto it included.
    [Fact]
    public void Before_AnUnassignedSizeWentWithTheWholeGallery()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var siyah = Card([.. Siyah, "kombin"], (xl, Siyah));
        Assert.Equal([.. Siyah, "kombin"], Images(siyah, l));
        Assert.Equal(Siyah, Images(siyah, xl));
    }

    // The assignment rule (owner, 11.11.2026: "neye atarsa onda duracak"; "burdan atama yapıyorum onlar yazsın varyantta
    // görsel olarak"): an assigned variant goes with exactly its assigned list (the row icon's count), chart last; one
    // without an assignment goes with its card's gallery minus the pictures assigned only to other colours.
    private static PublicationColours Colours(IEnumerable<(Guid Variant, string Colour)> cardVariants, params (string Picture, string Colour)[] modelAssignments) =>
        new(cardVariants.ToDictionary(item => item.Variant, item => item.Colour.ToLowerInvariant()), [.. modelAssignments.Select(item => (Asset(item.Picture), item.Colour.ToLowerInvariant()))]);

    [Fact]
    public void After_AnAssignedSizeGoesWithExactlyItsList_AnUnassignedOneWithItsColoursAssignment()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var siyah = Card([.. Siyah, "kombin", "beden-tablosu"], (xl, Siyah));
        var colours = Colours([(l, "Siyah"), (xl, "Siyah")], [.. Siyah.Select(picture => (picture, "Siyah"))]);
        Assert.Equal([.. Siyah, "beden-tablosu"], Images(siyah, l, "beden-tablosu", colours));
        Assert.Equal([.. Siyah, "beden-tablosu"], Images(siyah, xl, "beden-tablosu", colours));
        // No size of the colour assigned: the gallery (the unassigned "kombin" included).
        var none = Card([.. Siyah, "kombin", "beden-tablosu"]);
        Assert.Equal([.. Siyah, "kombin", "beden-tablosu"], Images(none, l, "beden-tablosu", colours));
    }

    // A size assignment adds to that size only: Siyah XL assigned Siyah's pictures and a detail shot goes with all four,
    // Siyah L with Siyah's three (this differs inside one Trendyol content; the editor warns before Yayınla).
    [Fact]
    public void After_ASizeAssignmentStaysWithThatSize()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var siyah = Card([.. Siyah, "xl-detay"], (l, Siyah), (xl, [.. Siyah, "xl-detay"]));
        var colours = Colours([(l, "Siyah"), (xl, "Siyah")], [.. Siyah.Select(picture => (picture, "Siyah")), ("xl-detay", "Siyah")]);
        Assert.Equal(Siyah, Images(siyah, l, colours: colours));
        Assert.Equal([.. Siyah, "xl-detay"], Images(siyah, xl, colours: colours));
    }

    // The live Siyah card: the other colours' pictures (assigned on their own cards) no longer go with its unassigned
    // sizes; XL goes with its own three. The six uploads nobody assigned yet go with the unassigned sizes until the owner
    // assigns them to Kahverengi and Mürdüm, then every Siyah size goes with Siyah's three.
    [Fact]
    public void After_TheLiveSiyahCard_NoLongerSendsOtherColoursPictures()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var siyah = Card([.. Bordo, .. Haki, .. Gri, .. Siyah, .. Lacivert, .. NewUploads], (xl, Siyah));
        (string, string)[] otherCards = [.. Bordo.Select(p => (p, "Bordo")), .. Haki.Select(p => (p, "Haki")), .. Gri.Select(p => (p, "Gri")), .. Lacivert.Select(p => (p, "Lacivert")), .. Siyah.Select(p => (p, "Siyah"))];
        var today = Colours([(l, "Siyah"), (xl, "Siyah")], otherCards);
        Assert.Equal(Siyah, Images(siyah, l, colours: today));
        Assert.Equal(Siyah, Images(siyah, xl, colours: today));

        var assigned = Colours([(l, "Siyah"), (xl, "Siyah")], [.. otherCards, .. NewUploads.Take(3).Select(p => (p, "Kahverengi")), .. NewUploads.Skip(3).Select(p => (p, "Mürdüm"))]);
        Assert.Equal(Siyah, Images(siyah, l, colours: assigned));
        Assert.Equal(Siyah, Images(siyah, xl, colours: assigned));
    }

    // One card holding two colours (RYP006 kind): an unassigned size never takes the other colour's assigned pictures.
    [Fact]
    public void After_ACardWithTwoColours_SendsNoSizeTheOtherColoursPictures()
    {
        var (siyahL, siyahXl, bordoL, bordoXl) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var card = Card([.. Siyah, .. Bordo, "beden-tablosu"], (siyahXl, Siyah), (bordoL, Bordo));
        var colours = Colours([(siyahL, "Siyah"), (siyahXl, "Siyah"), (bordoL, "Bordo"), (bordoXl, "Bordo")], [.. Siyah.Select(p => (p, "Siyah")), .. Bordo.Select(p => (p, "Bordo"))]);
        Assert.All(new[] { siyahL, siyahXl }, size => Assert.Equal([.. Siyah, "beden-tablosu"], Images(card, size, "beden-tablosu", colours)));
        Assert.All(new[] { bordoL, bordoXl }, size => Assert.Equal([.. Bordo, "beden-tablosu"], Images(card, size, "beden-tablosu", colours)));
    }
}
