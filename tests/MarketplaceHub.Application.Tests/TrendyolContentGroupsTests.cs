using System.Text.Json.Nodes;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Live 6.11.2026, RYP006: one panel card, six colours x three sizes, which Trendyol keeps as six contents (one per
// colour). "Görsel Güncelle" was refused as a content split; it now goes once per content, each with its own colour's
// images, and never writes one content's data onto another.
public sealed class TrendyolContentGroupsTests
{
    private static readonly string[] Colours = ["Haki", "Kahverengi", "Kiremit", "Lacivert", "Siyah", "Vizon"];

    // The category's attribute flags as getCategoryAttributes gives them: 293 the size (varianter), 47 the colour (slicer).
    private static readonly TrendyolAttributeRoles Roles = TrendyolAttributeRoles.FromCategoryAttributes([
        """{"attribute":{"id":293,"name":"Beden"},"varianter":true,"slicer":false}""",
        """{"attribute":{"id":47,"name":"Renk"},"varianter":false,"slicer":true}"""]);

    private static ServiceResult<string> Resolve(string payload, IReadOnlyDictionary<string, TrendyolContentObservation> contents) =>
        TrendyolContentGroups.Resolve(payload, contents, Roles);

    [Fact]
    public void ASixColourCard_GoesOncePerContent_WithItsOwnColoursImages()
    {
        var payload = CardPayload();
        Assert.True(TrendyolContentGroups.NeedsResolution(payload));
        Assert.False(TrendyolContentGroups.IsResolved(payload));
        var contents = Colours.Select((colour, index) => new TrendyolContentObservation($"{1228880360 + index}", Sizes(index))).ToList();

        var resolved = Resolve(payload, ByBarcode(contents));

        Assert.True(resolved.Succeeded, resolved.Error?.Message);
        Assert.True(TrendyolContentGroups.IsResolved(resolved.Value!));
        Assert.False(TrendyolContentGroups.NeedsResolution(resolved.Value!));
        var items = Items(resolved.Value!);
        Assert.Equal(6, items.Count);
        for (var index = 0; index < 6; index++)
        {
            var item = items[index]!.AsObject();
            Assert.Equal(1228880360L + index, item["contentId"]!.GetValue<long>());
            Assert.False(item.ContainsKey("barcode"));
            Assert.Equal($"https://img/{Colours[index]}/1.jpg", item["images"]![0]!["url"]!.GetValue<string>());
            Assert.Equal("Kazak", item["title"]!.GetValue<string>());
            // The colour (slicer) goes as the content's; the size (varianter 293) is no content's attribute and stays out.
            Assert.Equal([47], item["attributes"]!.AsArray().Select(attribute => attribute!["attributeId"]!.GetValue<int>()));
            Assert.Equal(Colours[index], item["attributes"]![0]!["customAttributeValue"]!.GetValue<string>());
        }
    }

    [Fact]
    public void ASingleContentCard_GoesAsOneItem()
    {
        var payload = Payload(("B1", "red"), ("B2", "red"));
        var resolved = Resolve(payload, ByBarcode([new("777", ["B1", "B2"])]));
        Assert.True(resolved.Succeeded, resolved.Error?.Message);
        var item = Assert.Single(Items(resolved.Value!))!.AsObject();
        Assert.Equal(777L, item["contentId"]!.GetValue<long>());
    }

    [Fact]
    public void AContentHoldingAnotherCardsBarcode_IsRefused()
    {
        var payload = Payload(("B1", "red"), ("B2", "red"));
        var resolved = Resolve(payload, ByBarcode([new("777", ["B1", "B2", "OTHER-1"])]));
        Assert.Equal("PRODUCT_CONTENT_SHARED", resolved.Error?.Code);
        Assert.Contains("OTHER-1", resolved.Error!.Message);
    }

    [Fact]
    public void TwoPanelColoursInOneContent_AreRefused()
    {
        // Trendyol keeps B1 and B2 in one content but the panel gives them different pictures: neither may overwrite it.
        var payload = Payload(("B1", "red"), ("B2", "blue"));
        var resolved = Resolve(payload, ByBarcode([new("777", ["B1", "B2"])]));
        Assert.Equal("PRODUCT_CONTENT_GROUP_MISMATCH", resolved.Error?.Code);
    }

    // Live 6.11.2026, RYP006 Vizon (content 1228880360): -18's own picture rows were renumbered with the size chart first.
    // The composer takes each size's pictures in the panel's order, so the three sizes agree and go as one content
    // whose first picture is the strip's front one. A size with a different picture set still stops the update.
    [Fact]
    public void ThreeSizesOneRenumbered_GoAsOneContent_InThePanelsOrder()
    {
        var (s16, s17, s18) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var media = new (Guid? VariantId, string Url)[]
        {
            (s18, "chart"), (s18, "1"), (s18, "2"), (s18, "3"),
            (null, "1"), (s16, "1"), (s17, "1"),
            (null, "2"), (s16, "2"), (s17, "2"),
            (null, "3"), (s16, "3"), (s17, "3"),
            (null, "chart"), (s16, "chart"), (s17, "chart")
        };
        var assets = media.Select(item => item.Url).Distinct().ToDictionary(url => url, _ => Guid.NewGuid());
        string[] Images(Guid size) => [.. MarketplaceHub.Infrastructure.Persistence.PublicationVariantMedia
            .For(media, size, item => item.VariantId, item => assets[item.Url]).Select(item => $"https://img/vizon/{item.Url}.jpg")];
        var sizes = new[] { ("RYP006-16", Images(s16)), ("RYP006-17", Images(s17)), ("RYP006-18", Images(s18)) };

        var resolved = Resolve(Payload(sizes), ByBarcode([new("1228880360", ["RYP006-16", "RYP006-17", "RYP006-18"])]));

        Assert.True(resolved.Succeeded, resolved.Error?.Message);
        var item = Assert.Single(Items(resolved.Value!))!.AsObject();
        Assert.Equal(1228880360L, item["contentId"]!.GetValue<long>());
        Assert.Equal(["https://img/vizon/1.jpg", "https://img/vizon/2.jpg", "https://img/vizon/3.jpg", "https://img/vizon/chart.jpg"],
            item["images"]!.AsArray().Select(image => image!["url"]!.GetValue<string>()));

        // A real difference (-18 without the size chart) is still a mismatch.
        var differentSet = new[] { sizes[0], sizes[1], ("RYP006-18", sizes[2].Item2[..3]) };
        var refused = Resolve(Payload(differentSet), ByBarcode([new("1228880360", ["RYP006-16", "RYP006-17", "RYP006-18"])]));
        Assert.Equal("PRODUCT_CONTENT_GROUP_MISMATCH", refused.Error?.Code);
    }

    [Fact]
    public void OneColourUnderTwoContents_GoesToEachWithTheSameImages()
    {
        // A duplicate listing: the same colour's sizes sit in two contents. Each gets the colour's own data.
        var payload = Payload(("B1", "red"), ("B2", "red"));
        var resolved = Resolve(payload, ByBarcode([new("777", ["B1"]), new("778", ["B2"])]));
        Assert.True(resolved.Succeeded, resolved.Error?.Message);
        Assert.Equal([777L, 778L], Items(resolved.Value!).Select(item => item!["contentId"]!.GetValue<long>()));
    }

    [Fact]
    public void ABarcodeNotApproved_IsLeftOut_AndNoApprovedBarcodeIsRefused()
    {
        var payload = Payload(("B1", "red"), ("B2", "blue"));
        var partial = Resolve(payload, ByBarcode([new("777", ["B1"])]));
        Assert.True(partial.Succeeded, partial.Error?.Message);
        Assert.Equal(777L, Assert.Single(Items(partial.Value!))!["contentId"]!.GetValue<long>());

        Assert.Equal("PRODUCT_CONTENT_NOT_APPROVED", Resolve(payload, ByBarcode([])).Error?.Code);
    }

    [Fact]
    public void Observe_TakesTheContentHoldingTheExactBarcode_AndLeavesArchivedSiblingsOut()
    {
        var holder = Content("777", ("B1", false), ("B2", false), ("OLD", true));
        var other = Content("900", ("B10", false));
        var observed = TrendyolContentGroups.Observe("b1", [other, holder]);
        Assert.True(observed.Succeeded, observed.Error?.Message);
        Assert.Equal("777", observed.Value!.ContentId);
        Assert.Equal(["B1", "B2"], observed.Value.Barcodes.Order(StringComparer.Ordinal));

        Assert.Null(TrendyolContentGroups.Observe("B9", [other]).Value);
        Assert.Equal("PRODUCT_CONTENT_AMBIGUOUS", TrendyolContentGroups.Observe("B1", [holder, Content("778", ("B1", false))]).Error?.Code);
    }

    [Fact]
    public void AContentIdThatIsNotANumber_IsRefused()
    {
        var resolved = Resolve(Payload(("B1", "red")), ByBarcode([new("RYP006", ["B1"])]));
        Assert.Equal("REMOTE_CONTENT_ID_INVALID", resolved.Error?.Code);
    }

    [Fact]
    public void AnOldJobsContentRequest_IsAlreadyResolved()
    {
        const string old = """{"items":[{"contentId":123,"title":"Ad","images":[{"url":"https://x/1.jpg"}]}]}""";
        Assert.False(TrendyolContentGroups.NeedsResolution(old));
        Assert.True(TrendyolContentGroups.IsResolved(old));
        Assert.False(TrendyolContentGroups.NeedsResolution("""{"items":[]}"""));
    }

    private static string[] Sizes(int colour) => [.. Enumerable.Range(1, 3).Select(size => $"RYP006-{colour * 3 + size:00}")];

    private static string CardPayload() => Payload([.. Colours.SelectMany((colour, index) => Sizes(index).Select(barcode => (barcode, colour)))]);

    private static string Payload(params (string Barcode, string Colour)[] variants) => new JsonObject
    {
        ["items"] = new JsonArray([.. variants.Select(variant => (JsonNode)new JsonObject
        {
            ["barcode"] = variant.Barcode,
            ["title"] = "Kazak",
            ["description"] = "Açıklama",
            ["images"] = new JsonArray(new JsonObject { ["url"] = $"https://img/{variant.Colour}/1.jpg" }),
            ["attributes"] = new JsonArray(new JsonObject { ["attributeId"] = 47, ["customAttributeValue"] = variant.Colour }, new JsonObject { ["attributeId"] = 293, ["customAttributeValue"] = variant.Barcode })
        })])
    }.ToJsonString();

    private static string Payload(params (string Barcode, string[] Images)[] variants) => new JsonObject
    {
        ["items"] = new JsonArray([.. variants.Select(variant => (JsonNode)new JsonObject
        {
            ["barcode"] = variant.Barcode,
            ["title"] = "Kazak",
            ["description"] = "Açıklama",
            ["images"] = new JsonArray([.. variant.Images.Select(url => (JsonNode)new JsonObject { ["url"] = url })]),
            ["attributes"] = new JsonArray(new JsonObject { ["attributeId"] = 47, ["customAttributeValue"] = "Vizon" }, new JsonObject { ["attributeId"] = 293, ["customAttributeValue"] = variant.Barcode })
        })])
    }.ToJsonString();

    private static Dictionary<string, TrendyolContentObservation> ByBarcode(IEnumerable<TrendyolContentObservation> contents) =>
        contents.SelectMany(content => content.Barcodes.Select(barcode => (barcode, content))).ToDictionary(entry => entry.barcode, entry => entry.content, StringComparer.OrdinalIgnoreCase);

    private static RemoteCatalogProduct Content(string contentId, params (string Barcode, bool Archived)[] variants) => new(
        contentId, "RYP006", "Kazak", "", null, null, null, null, [],
        [.. variants.Select(variant => new RemoteCatalogVariant(variant.Barcode, variant.Barcode, variant.Barcode, "RYP006", new Dictionary<string, string>(), variant.Archived, null, null, null, null, null, "{}"))],
        "{}");

    private static JsonArray Items(string payloadJson) => JsonNode.Parse(payloadJson)!["items"]!.AsArray();
}
