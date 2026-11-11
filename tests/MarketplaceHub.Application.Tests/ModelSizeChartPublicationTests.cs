using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.SizeCharts;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The model's size chart goes last with every variant of every colour card (owner, 12.11.2026, MZ051BYC: "51bcy beden
// tablosu seçilmemiş neden?"), whether or not the card holds a row of it: a colour card without the chart's row (live
// RYP003T: six of seven colour cards) takes it from the model (PublicationVariantMedia.For's chartRow). A card that
// holds its chart goes exactly as before, picture by picture.
public sealed class ModelSizeChartPublicationTests
{
    private sealed record Media(Guid? VariantId, Guid AssetId, string Url);

    private static readonly Dictionary<string, Guid> Assets = [];
    private static Guid Asset(string name) { lock (Assets) { if (!Assets.TryGetValue(name, out var id)) Assets[name] = id = Guid.NewGuid(); return id; } }
    private static Media Row(string name, Guid? variant = null) => new(variant, Asset(name), $"https://cdn.example.com/model/{name}.jpg");

    private static List<Media> Card(string[] gallery, params (Guid Variant, string[] Pictures)[] variants) =>
        [.. gallery.Select(name => Row(name)), .. variants.SelectMany(variant => variant.Pictures.Select(name => Row(name, variant.Variant)))];

    private static PublicationColours Colours(params (Guid Variant, string Colour)[] variants) => new(variants.ToDictionary(item => item.Variant, item => item.Colour), []);

    private const string Chart = "beden-tablosu";

    // The images one variant goes with, as the composers select them (limit: Trendyol/N11/Pazarama/PttAVM 8, HB 5).
    private static string[] Images(List<Media> card, Guid variant, PublicationColours colours, Media? chartRow, int limit = 8)
    {
        var pictures = PublicationVariantMedia.For(card, variant, item => item.VariantId, item => item.AssetId, Asset(Chart), colours, chartRow).Select(item => (Id: item.AssetId, Url: (string?)item.Url)).ToList();
        return [.. ProductPublicationComposer.SelectPublicationImageUrls(pictures.Select(item => item.Url), PublicationVariantMedia.SizeChartUrl(pictures, Asset(Chart)), limit).Select(url => url[(url.LastIndexOf('/') + 1)..^4])];
    }

    private static readonly string[] Siyah = ["siyah-1", "siyah-2", "siyah-3"];

    // MZ051BYC Siyah shape without the chart's row: the gallery holds Siyah's three pictures, only XL has its own.
    [Fact]
    public void ACardWithoutTheChartsRow_SendsTheModelsChartLast_FromEverySize()
    {
        var (l, xl, xxl) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var siyah = Card(Siyah, (xl, Siyah));
        var colours = Colours((l, "siyah"), (xl, "siyah"), (xxl, "siyah"));

        // Before: the card's rows alone, no chart.
        Assert.All(new[] { l, xl, xxl }, size => Assert.Equal(Siyah, Images(siyah, size, colours, chartRow: null)));
        // After: the model's chart last, from every size, on every marketplace's limit.
        Assert.All(new[] { l, xl, xxl }, size => Assert.Equal([.. Siyah, Chart], Images(siyah, size, colours, Row(Chart))));
        Assert.All(new[] { l, xl, xxl }, size => Assert.Equal([.. Siyah, Chart], Images(siyah, size, colours, Row(Chart), limit: 5)));
    }

    // A long list keeps the chart as the last picture sent (the existing rule): it is never the one cut off.
    [Fact]
    public void ALongList_KeepsTheModelsChartLast_WithinEachLimit()
    {
        var l = Guid.NewGuid();
        string[] nine = [.. Enumerable.Range(1, 9).Select(index => $"foto-{index}")];
        var card = Card(nine);
        Assert.Equal([.. nine.Take(7), Chart], Images(card, l, Colours((l, "siyah")), Row(Chart)));
        Assert.Equal([.. nine.Take(4), Chart], Images(card, l, Colours((l, "siyah")), Row(Chart), limit: 5));
    }

    // Every shape that holds the chart's row goes byte for byte as before, the model's row passed or not.
    [Theory]
    [InlineData("gallery")]
    [InlineData("every-size")]
    [InlineData("first-size")]
    public void ACardHoldingItsChart_IsUnchanged(string shape)
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var card = shape switch
        {
            "gallery" => Card([.. Siyah, Chart], (xl, Siyah)),
            "every-size" => Card([.. Siyah, Chart], (l, [.. Siyah, Chart]), (xl, [Chart, .. Siyah])),
            _ => Card([], (l, [.. Siyah, Chart])),
        };
        var colours = Colours((l, "siyah"), (xl, "siyah"));
        foreach (var size in new[] { l, xl })
            Assert.Equal(Images(card, size, colours, chartRow: null), Images(card, size, colours, Row(Chart)));
    }

    // The chart's row never counts as one of the card's pictures: a card whose pictures sit on its first size only
    // (imported) still sends them from its other sizes, the chart after them.
    [Fact]
    public void TheModelsChartRow_DoesNotChangeWhichPicturesTheCardSends()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var card = Card([], (l, Siyah));
        Assert.Equal([.. Siyah, Chart], Images(card, xl, Colours((l, "siyah"), (xl, "siyah")), Row(Chart)));
        Assert.Equal([.. Siyah], Images(card, xl, Colours((l, "siyah"), (xl, "siyah")), chartRow: null));
    }

    // PTT AVM's one product per model: a colour card without the chart's row adds the model's chart last too.
    [Fact]
    public void ForCard_AddsTheModelsChartLast()
    {
        var (l, xl) = (Guid.NewGuid(), Guid.NewGuid());
        var card = Card(Siyah, (xl, Siyah));
        var pictures = PublicationVariantMedia.ForCard(card, [l, xl], item => item.VariantId, item => item.AssetId, Asset(Chart), Colours((l, "siyah"), (xl, "siyah")), Row(Chart));
        Assert.Equal([.. Siyah.Select(Asset), Asset(Chart)], pictures.Select(item => item.AssetId));
    }

    [Fact]
    public void TakeKeepingChart_StillSendsTheChartLast() =>
        Assert.Equal(["a", "b", "chart"], SizeChartPlacement.TakeKeepingLast(["a", "b", "c", "chart"], 3, lastIsSizeChart: true));
}
