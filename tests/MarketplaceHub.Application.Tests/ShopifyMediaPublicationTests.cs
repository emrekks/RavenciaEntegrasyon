using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Live 2026-11-01 (RYP006/RYP007): one card with six colours, 18 gallery pictures and three colour pictures per colour
// reached Shopify with 8 pictures out of order and no variant picture.
public sealed class ShopifyMediaPublicationTests
{
    private static readonly Guid Card = Guid.Parse("01a18ddd-78fb-780d-8a48-251fac20ed83");

    [Fact]
    public void EveryGalleryPictureGoesInTheEditorsOrderAndEachVariantGetsItsColoursFirstPicture()
    {
        var colours = new[] { "haki", "kahve", "kiremit", "lacivert", "siyah", "vizon" };
        var variants = colours.SelectMany(colour => new[] { "L", "XL", "2XL" }.Select(size => (Id: Guid.NewGuid(), Colour: colour, Size: size))).ToList();
        var rows = new List<ShopifyMediaRow>();
        // The gallery holds the colours in the editor's order: lacivert first, then the rest.
        var gallery = new[] { "lacivert" }.Concat(colours.Where(colour => colour != "lacivert"))
            .SelectMany(colour => Enumerable.Range(0, 3).Select(index => Url(colour, index))).ToList();
        rows.AddRange(gallery.Select((url, index) => new ShopifyMediaRow(Card, null, index, url)));
        // Every variant carries its colour's three pictures at sort 0, 1, 2, the rows that used to jumble the gallery.
        rows.AddRange(variants.SelectMany(variant => Enumerable.Range(0, 3).Select(index => new ShopifyMediaRow(Card, variant.Id, index, Url(variant.Colour, index)))));

        var plan = ShopifyProductPublicationComposer.PlanMedia(rows, [Card], variants.Select(variant => (variant.Id, Card)).ToList());

        Assert.Equal(gallery, plan.ProductUrls);
        Assert.Equal(18, plan.VariantUrls.Count);
        foreach (var variant in variants) Assert.Equal(Url(variant.Colour, 0), plan.VariantUrls[variant.Id]);
    }

    [Fact]
    public void APictureOnlyAVariantCarriesFollowsTheGallery()
    {
        var variant = Guid.NewGuid();
        var rows = new List<ShopifyMediaRow>
        {
            new(Card, variant, 0, Url("siyah", 0)),
            new(Card, null, 1, Url("main", 1)),
            new(Card, null, 0, Url("main", 0))
        };

        var plan = ShopifyProductPublicationComposer.PlanMedia(rows, [Card], [(variant, Card)]);

        Assert.Equal([Url("main", 0), Url("main", 1), Url("siyah", 0)], plan.ProductUrls);
        Assert.Equal(Url("siyah", 0), plan.VariantUrls[variant]);
    }

    [Fact]
    public void AVariantWithoutItsOwnPictureShowsItsCardsFirstOnlyInAFamily()
    {
        var other = Guid.NewGuid();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var rows = new List<ShopifyMediaRow> { new(Card, null, 0, Url("card", 0)), new(other, null, 0, Url("other", 0)) };

        var family = ShopifyProductPublicationComposer.PlanMedia(rows, [Card, other], [(first, Card), (second, other)]);
        var single = ShopifyProductPublicationComposer.PlanMedia(rows.Take(1).ToList(), [Card], [(first, Card)]);

        Assert.Equal(Url("card", 0), family.VariantUrls[first]);
        Assert.Equal(Url("other", 0), family.VariantUrls[second]);
        Assert.Empty(single.VariantUrls);
    }

    [Theory]
    [InlineData("""{"media":{"nodes":[{"status":"UPLOADED","mediaErrors":[]},{"status":"READY","mediaErrors":[]}]}}""", 2, null)]
    [InlineData("""{"media":{"nodes":[{"status":"UPLOADED","mediaErrors":[]}]}}""", 2, "2 görselden 1 tanesi eklendi")]
    [InlineData("""{"media":{"nodes":[{"status":"FAILED","mediaErrors":[{"message":"Image could not be downloaded"}]},{"status":"READY","mediaErrors":[]}]}}""", 2, "1 görsel işlenemedi, Shopify: Image could not be downloaded")]
    [InlineData("""{"id":"gid://shopify/Product/1"}""", 3, "Shopify 3 görselin hiçbirini bildirmedi")]
    public void APictureThatDidNotArriveIsAShortfall(string product, int sent, string? expected)
    {
        using var json = JsonDocument.Parse(product);

        Assert.Equal(expected, ShopifyHttpClient.MediaShortfall(json.RootElement, sent));
    }

    [Fact]
    public async Task TheMediaIssueRidesOnTheCompletedOperationAndAnOlderIdStillReads()
    {
        var client = new ShopifyHttpClient(null!, null!, Options.Create(new ShopifyOptions()), TimeProvider.System, NullLogger<ShopifyHttpClient>.Instance, null!, new ConfigurationBuilder().Build());
        var context = new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), "correlation", "effect", DateTimeOffset.UtcNow.AddMinutes(1));
        RemoteOperationLine[] lines = [new("RYP006-L", true, "gid://shopify/ProductVariant/1", null, false, "COMPLETED")];

        var withIssue = await client.GetOperationAsync(context, ShopifyHttpClient.EncodeCompletedOperation(lines, "görseller eksik"), CancellationToken.None);
        var plain = await client.GetOperationAsync(context, ShopifyHttpClient.EncodeCompletedOperation(lines), CancellationToken.None);

        Assert.Equal(ShopifyHttpClient.MediaIncompleteCode, withIssue.Value!.IssueCode);
        Assert.Equal("görseller eksik", withIssue.Value.IssueSummary);
        Assert.Equal("RYP006-L", Assert.Single(withIssue.Value.Lines).ExternalKey);
        Assert.Null(plain.Value!.IssueCode);
        Assert.Single(plain.Value.Lines);
    }

    private static string Url(string colour, int index) => $"https://app.example.com/media/{colour}-{index}/content";
}
