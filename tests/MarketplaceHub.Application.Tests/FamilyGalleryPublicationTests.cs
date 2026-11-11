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
    private static string[] Images(List<Media> card, Guid variant, string? chart = null)
    {
        var chartId = chart is null ? (Guid?)null : Asset(chart);
        var pictures = PublicationVariantMedia.For(card, variant, item => item.VariantId, item => item.AssetId, chartId).Select(item => (Id: item.AssetId, Url: (string?)item.Url)).ToList();
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

    // A picture without a colour (shared) that the editor adds to a card's gallery reaches the sizes without pictures of
    // their own; a size with its own assignment keeps exactly that assignment (plus the chart): the editor writes the
    // owner's assignment per variant and never widens it.
    [Fact]
    public void ASharedPictureInTheGallery_ReachesOnlyTheSizesWithoutTheirOwnAssignment()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var siyah = Card([.. Siyah, "kombin"], (xl, Siyah));
        Assert.Equal([.. Siyah, "kombin"], Images(siyah, l));
        Assert.Equal(Siyah, Images(siyah, xl));
    }
}
