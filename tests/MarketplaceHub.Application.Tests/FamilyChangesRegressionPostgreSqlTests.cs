using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Regression (owner, 12.11.2026: "Gönderimlerde vs yada başka bir yerde sorun yaratacak mı, onların da testlerini yap"):
// the family gallery (a6692297b), the model fields written to every colour card (91dadde7d) and the picture assignment
// rule (f680abdd1) pinned on the paths beside Trendyol: the Hepsiburada, N11, Pazarama and PttAVM payloads of an
// MZ051BYC-shaped model (7 colours x 5 sizes, each colour its own card, a shared size chart), an unchanged single-colour
// card, the price/stock send, the product list's front picture and the products Excel round trip.
[Collection("postgres-integration")]
public sealed class FamilyChangesRegressionPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string ModelCode = "MZ051BYC";
    private const string Chart = "beden-tablosu";
    private static readonly string[] Sizes = ["S", "M", "L", "XL", "XXL"];
    private static readonly (string Colour, string Key, int Pictures, decimal Desi)[] Colours =
    [
        ("Bordo", "bordo", 3, 1.5m), ("Haki", "haki", 3, 1.5m), ("Gri", "gri", 9, 1.5m), ("Siyah", "siyah", 3, 1.5m),
        ("Lacivert", "lacivert", 3, 2m), ("Kahverengi", "kahve", 3, 1.5m), ("Mürdüm", "murdum", 3, 1.5m)
    ];
    private static readonly string[] Platforms = ["HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM"];

    private static string Url(string name) => $"https://cdn.example.com/mz051byc/{name}.jpg";
    private static string[] Pictures(string key, int count) => [.. Enumerable.Range(1, count).Select(index => $"{key}-{index}")];

    // What a colour's variants go with: its own pictures and the chart last, cut to the marketplace's limit keeping the chart.
    private static string[] Expected(string key, int count, int limit)
    {
        var own = Pictures(key, count);
        return own.Length + 1 <= limit ? [.. own.Select(Url), Url(Chart)] : [.. own.Take(limit - 1).Select(Url), Url(Chart)];
    }

    [PostgreSqlFact]
    public async Task EveryMarketplacePayload_OfEachColourCard_CarriesThatColoursPictures_Brand_Desi_AndTitle()
    {
        var seed = await SeedAsync(live: false);
        await using var db = fixture.CreateContext();
        var composer = new ProductPublicationComposer(db, new ConfigurationBuilder().Build());
        foreach (var (colour, key, count, desi) in Colours)
        {
            var card = seed.Cards[colour];
            var barcodes = seed.Sizes[colour].Select(variant => variant.Barcode!).ToArray();
            var title = $"Kadın {colour} Triko Bluz";

            var hb = await BuildAsync(composer, seed, "HEPSIBURADA", card.Id);
            var hbItems = JsonNode.Parse(hb)!.AsArray().Select(item => item!["attributes"]!.AsObject()).ToList();
            Assert.Equal(barcodes.Order(StringComparer.Ordinal), hbItems.Select(item => item["Barcode"]!.GetValue<string>()).Order(StringComparer.Ordinal));
            Assert.All(hbItems, item =>
            {
                // Hepsiburada: Image1..Image5, the chart last; the brand before the name; desi as kg.
                Assert.Equal(Expected(key, count, 5), Enumerable.Range(1, 5).Select(index => item[$"Image{index}"]?.GetValue<string>()).OfType<string>());
                Assert.Equal("Ravencia", item["Marka"]!.GetValue<string>());
                Assert.Equal($"Ravencia {title}", item["UrunAdi"]!.GetValue<string>());
                Assert.Equal(ModelCode, item["VaryantGroupID"]!.GetValue<string>());
                Assert.Equal(desi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), item["kg"]!.GetValue<string>());
            });

            var n11 = JsonNode.Parse(await BuildAsync(composer, seed, "N11", card.Id))!["payload"]!["skus"]!.AsArray();
            Assert.Equal(barcodes.Order(StringComparer.Ordinal), n11.Select(item => item!["barcode"]!.GetValue<string>()).Order(StringComparer.Ordinal));
            Assert.All(n11, item =>
            {
                Assert.Equal(Expected(key, count, 8), item!["images"]!.AsArray().Select(image => image!["url"]!.GetValue<string>()));
                Assert.Equal(title, item["title"]!.GetValue<string>());
                Assert.Equal(ModelCode, item["productMainId"]!.GetValue<string>());
                var brand = item["attributes"]!.AsArray().Single(attribute => attribute!["id"]!.GetValue<long>() == 1);
                Assert.Equal("Ravencia", brand!["customValue"]!.GetValue<string>());
            });

            var pazarama = JsonNode.Parse(await BuildAsync(composer, seed, "PAZARAMA", card.Id))!["products"]!.AsArray();
            Assert.Equal(barcodes.Order(StringComparer.Ordinal), pazarama.Select(item => item!["code"]!.GetValue<string>()).Order(StringComparer.Ordinal));
            Assert.All(pazarama, item =>
            {
                Assert.Equal(Expected(key, count, 8), item!["images"]!.AsArray().Select(image => image!["imageurl"]!.GetValue<string>()));
                Assert.Equal(title, item["name"]!.GetValue<string>());
                Assert.Equal(seed.PazaramaBrandExternalId, item["brandId"]!.GetValue<string>());
                Assert.Equal((int)Math.Ceiling(desi), item["desi"]!.GetValue<int>());
                Assert.Equal(ModelCode, item["groupCode"]!.GetValue<string>());
            });
        }

        // PttAVM keeps one product for the model: every colour card's variants under the model code, each colour's first
        // picture first, the brand as text, the largest desi.
        foreach (var opened in new[] { "Bordo", "Siyah" })
        {
            var ptt = JsonNode.Parse(await BuildAsync(composer, seed, "PTTAVM", seed.Cards[opened].Id))!.AsObject();
            Assert.Equal(ModelCode, ptt["barcode"]!.GetValue<string>());
            Assert.Equal("Ravencia", ptt["brand"]!.GetValue<string>());
            Assert.Equal(2m, ptt["desi"]!.GetValue<decimal>());
            Assert.Equal("Kadın Bordo Triko Bluz", ptt["name"]!.GetValue<string>());
            var variants = ptt["variants"]!.AsArray();
            Assert.Equal(Colours.Length * Sizes.Length, variants.Count);
            Assert.Equal(seed.Sizes.Values.SelectMany(sizes => sizes).Select(variant => variant.Barcode!).Order(StringComparer.Ordinal), variants.Select(variant => variant!["barcode"]!.GetValue<string>()).Order(StringComparer.Ordinal));
            var images = ptt["images"]!.AsArray().Select(image => image!.GetValue<string>()).ToList();
            // Each card's first picture first. The Siyah card's gallery still holds Bordo's pictures in front (live, before
            // its first Kaydet): PttAVM's family images are the pictures each card's variants go with (assignment rule),
            // so Siyah's lead is siyah-1, as on the variant-level marketplaces.
            string[] leads = ["bordo-1", "haki-1", "gri-1", "siyah-1", "lacivert-1", "kahve-1", "murdum-1"];
            Assert.All(leads, lead => Assert.Contains(Url(lead), images.Take(Colours.Length)));
            Assert.All(Pictures("siyah", 3), picture => Assert.Contains(Url(picture), images));
            Assert.Equal(images.Count, images.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.True(images.Count <= 8 * Colours.Length);
            Assert.Contains(Url(Chart), images);
        }
    }

    // A card the changes are not about (one colour, imported: gallery and its first size holding the same pictures, no
    // model knowledge to apply) goes to every marketplace with the pictures the rule before the assignment rule gave.
    [PostgreSqlFact]
    public async Task AnUnchangedSingleColourCard_GoesWithThePicturesItWentWithBefore()
    {
        var seed = await SeedAsync(live: false);
        await using var db = fixture.CreateContext();
        var composer = new ProductPublicationComposer(db, new ConfigurationBuilder().Build());
        var rows = seed.SingleMedia;
        foreach (var variant in seed.SingleSizes)
        {
            var before = PublicationVariantMedia.For(rows, variant.Id, item => item.VariantId, item => item.AssetId).Select(item => item.Url).ToArray();
            Assert.Equal(Pictures("ryp-siyah", 4).Select(Url), before);
        }
        var before8 = Pictures("ryp-siyah", 4).Select(Url).ToArray();

        var hb = JsonNode.Parse(await BuildAsync(composer, seed, "HEPSIBURADA", seed.Single.Id))!.AsArray();
        Assert.All(hb, item => Assert.Equal(before8, Enumerable.Range(1, 5).Select(index => item!["attributes"]![$"Image{index}"]?.GetValue<string>()).OfType<string>()));
        var n11 = JsonNode.Parse(await BuildAsync(composer, seed, "N11", seed.Single.Id))!["payload"]!["skus"]!.AsArray();
        Assert.All(n11, item => Assert.Equal(before8, item!["images"]!.AsArray().Select(image => image!["url"]!.GetValue<string>())));
        var pazarama = JsonNode.Parse(await BuildAsync(composer, seed, "PAZARAMA", seed.Single.Id))!["products"]!.AsArray();
        Assert.All(pazarama, item => Assert.Equal(before8, item!["images"]!.AsArray().Select(image => image!["imageurl"]!.GetValue<string>())));
        var ptt = JsonNode.Parse(await BuildAsync(composer, seed, "PTTAVM", seed.Single.Id))!.AsObject();
        Assert.Equal(before8, ptt["images"]!.AsArray().Select(image => image!.GetValue<string>()));
        Assert.Equal("RYP001X", ptt["barcode"]!.GetValue<string>());
        Assert.Equal(3, ptt["variants"]!.AsArray().Count);
    }

    // The owner changes the model's brand and name from the Siyah row: every colour card takes them, so each card's
    // payload goes with the new brand (Pazarama by its brand mapping), PttAVM still finds the whole family under the new
    // brand, and no card drops out of the model.
    [PostgreSqlFact]
    public async Task ABrandChangedFromOneColourRow_ReachesEveryCardsPayload_AndKeepsTheFamilyWhole()
    {
        var seed = await SeedAsync(live: false);
        await using var db = fixture.CreateContext();
        var siyah = seed.Cards["Siyah"];
        var updated = await Catalog(db).UpdateProductAsync(seed.Tenant.Id, siyah.Id, siyah.Version,
            new UpdateProductCommand("Kadın Siyah Yumuşak Triko Bluz", siyah.Description, seed.OtherBrandId, siyah.CategoryId,
                FamilySharedFields: [ProductFamilySharedFields.Title, ProductFamilySharedFields.Brand]), CancellationToken.None);
        Assert.True(updated.Succeeded, updated.Error?.Message);

        await using var check = fixture.CreateContext();
        var composer = new ProductPublicationComposer(check, new ConfigurationBuilder().Build());
        foreach (var (colour, key, count, _) in Colours)
        {
            var card = seed.Cards[colour];
            var hb = JsonNode.Parse(await BuildAsync(composer, seed, "HEPSIBURADA", card.Id))!.AsArray();
            Assert.All(hb, item =>
            {
                Assert.Equal("Moda Evi", item!["attributes"]!["Marka"]!.GetValue<string>());
                Assert.Equal($"Moda Evi Kadın {colour} Yumuşak Triko Bluz", item["attributes"]!["UrunAdi"]!.GetValue<string>());
                Assert.Equal(Expected(key, count, 5), Enumerable.Range(1, 5).Select(index => item["attributes"]![$"Image{index}"]?.GetValue<string>()).OfType<string>());
            });
            var n11 = JsonNode.Parse(await BuildAsync(composer, seed, "N11", card.Id))!["payload"]!["skus"]!.AsArray();
            Assert.All(n11, item => Assert.Equal("Moda Evi", item!["attributes"]!.AsArray().Single(attribute => attribute!["id"]!.GetValue<long>() == 1)!["customValue"]!.GetValue<string>()));
            var pazarama = JsonNode.Parse(await BuildAsync(composer, seed, "PAZARAMA", card.Id))!["products"]!.AsArray();
            Assert.All(pazarama, item => Assert.Equal(seed.PazaramaOtherBrandExternalId, item!["brandId"]!.GetValue<string>()));
        }
        var ptt = JsonNode.Parse(await BuildAsync(composer, seed, "PTTAVM", seed.Cards["Gri"].Id))!.AsObject();
        Assert.Equal("Moda Evi", ptt["brand"]!.GetValue<string>());
        Assert.Equal(Colours.Length * Sizes.Length, ptt["variants"]!.AsArray().Count);
    }

    // "Platformlarda Güncelle" for the model: the price/stock request per barcode is the same before and after the owner
    // changes the model's name and brand from one colour row and assigns pictures (pictures, names and brands are no
    // part of it, and the family the send covers stays the same cards).
    [PostgreSqlTheory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    public async Task ThePriceStockSend_IsTheSameBeforeAndAfterTheModelFieldsAndPicturesChange(string platform)
    {
        var seed = await SeedAsync(live: true);
        var connection = seed.Connections[platform];
        var siyah = seed.Cards["Siyah"];
        string before;
        await using (var db = fixture.CreateContext())
        {
            var build = await new PriceInventoryComposer(db).BuildAsync(seed.Tenant.Id, connection.Id, CancellationToken.None, productId: siyah.Id);
            Assert.True(build.Succeeded, build.Error?.Message);
            Assert.Equal(Colours.Length * Sizes.Length, build.Value!.Lines.Count);
            before = build.Value.PayloadJson;
        }
        await using (var db = fixture.CreateContext())
        {
            var updated = await Catalog(db).UpdateProductAsync(seed.Tenant.Id, siyah.Id, siyah.Version,
                new UpdateProductCommand("Kadın Siyah Yumuşak Triko Bluz", "Model açıklaması", seed.OtherBrandId, siyah.CategoryId,
                    FamilySharedFields: [ProductFamilySharedFields.Title, ProductFamilySharedFields.Description, ProductFamilySharedFields.Brand]), CancellationToken.None);
            Assert.True(updated.Succeeded, updated.Error?.Message);
            // Seçeneklere Görsel Ata: Siyah's pictures on every Siyah size.
            var siyahPictures = await db.FileAssets.Where(asset => asset.TenantId == seed.Tenant.Id && asset.RelativePath.Contains("/siyah-")).ToListAsync();
            foreach (var variant in seed.Sizes["Siyah"].Where(variant => variant.Sku != seed.Sizes["Siyah"][3].Sku))
                db.ProductMedia.AddRange(siyahPictures.Select((asset, index) => new ProductMedia { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = siyah.Id, VariantId = variant.Id, FileAssetId = asset.Id, MediaRole = index == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY", SortOrder = index * 10, Status = "ACTIVE" }));
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var build = await new PriceInventoryComposer(db).BuildAsync(seed.Tenant.Id, connection.Id, CancellationToken.None, productId: siyah.Id);
            Assert.True(build.Succeeded, build.Error?.Message);
            Assert.Equal(before, build.Value!.PayloadJson);
        }
    }

    // The product list's front picture of each colour card is that colour's first picture, never the size chart.
    [PostgreSqlFact]
    public async Task TheProductList_ShowsEachColourCardWithItsOwnFirstPicture()
    {
        var seed = await SeedAsync(live: false, siyahGalleryHoldsBordo: false);
        await using var db = fixture.CreateContext();
        var page = await Catalog(db).ListProductsAsync(seed.Tenant.Id, 100, null, null, null, null, null, null, CancellationToken.None);
        foreach (var (colour, key, _, _) in Colours)
            Assert.Equal(Url($"{key}-1"), page.Items.Single(item => item.Id == seed.Cards[colour].Id).PrimaryImageUrl);
        Assert.Equal(Url("ryp-siyah-1"), page.Items.Single(item => item.Id == seed.Single.Id).PrimaryImageUrl);
    }

    // The live Siyah shape before its first Kaydet (its gallery holds Bordo's pictures first): the payloads leave Bordo's
    // pictures out (assignment rule), and so does the list's front picture (the first picture the card's variants go with).
    [PostgreSqlFact]
    public async Task TheProductList_ShowsSiyahsOwnPicture_EvenWhileItsGalleryHoldsAnotherColoursPictures()
    {
        var seed = await SeedAsync(live: false);
        await using var db = fixture.CreateContext();
        var page = await Catalog(db).ListProductsAsync(seed.Tenant.Id, 100, null, null, null, null, null, null, CancellationToken.None);
        Assert.Equal(Url("siyah-1"), page.Items.Single(item => item.Id == seed.Cards["Siyah"].Id).PrimaryImageUrl);
    }

    // Owner, 12.11.2026 ("51 de 1. görsel yeşil ama kahve duruyor?"): live MZ051BYC's family gallery starts with Haki
    // (a family reorder put Haki's pictures first) while its newest card, the one the list's model row stands for, is
    // Kahverengi. The model row's picture is the gallery's first picture as the editor shows it, on every card of the
    // family; each colour card keeps its own front picture.
    [PostgreSqlFact]
    public async Task TheProductList_ModelRowShowsTheFamilyGallerysFirstPicture_NotTheNewestCardsOwn()
    {
        var seed = await SeedAsync(live: false, siyahGalleryHoldsBordo: false);
        await using (var db = fixture.CreateContext())
        {
            var familyIds = seed.Cards.Values.Select(card => card.Id).ToArray();
            var rows = await db.ProductMedia.Where(x => x.TenantId == seed.Tenant.Id && familyIds.Contains(x.ProductId) && x.VariantId == null).ToListAsync();
            var haki = seed.Cards["Haki"].Id;
            var cardRank = Colours.Select((colour, index) => (seed.Cards[colour.Colour].Id, index)).ToDictionary(x => x.Id, x => x.index);
            var rank = 0;
            foreach (var row in rows.OrderBy(x => x.ProductId == haki ? 0 : 1).ThenBy(x => cardRank[x.ProductId]).ThenBy(x => x.SortOrder))
                row.SortOrder = rank++ * 83;
            await db.SaveChangesAsync();
            var kahve = seed.Cards["Kahverengi"].Id;
            await db.Products.Where(x => x.Id == kahve).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddHours(1)));
        }
        await using var read = fixture.CreateContext();
        var catalog = Catalog(read);
        var page = await catalog.ListProductsAsync(seed.Tenant.Id, 100, null, null, null, null, null, null, CancellationToken.None);
        var family = page.Items.Where(item => seed.Cards.Values.Any(card => card.Id == item.Id)).ToList();
        Assert.Equal(seed.Cards["Kahverengi"].Id, family[0].Id);
        Assert.All(family, card => Assert.Equal(Url("haki-1"), card.FamilyImageUrl));
        foreach (var (colour, key, _, _) in Colours)
            Assert.Equal(Url($"{key}-1"), family.Single(item => item.Id == seed.Cards[colour].Id).PrimaryImageUrl);
        // The editor's gallery starts with the same picture, whichever colour row opens it.
        var editor = await catalog.GetProductAsync(seed.Tenant.Id, seed.Cards["Kahverengi"].Id, CancellationToken.None);
        Assert.Equal(Url("haki-1"), (editor.Value!.FamilyOrderedMediaUrls ?? editor.Value.FamilyMediaUrls!)[0]);
        // The single-colour card's model picture is its own first picture.
        Assert.Equal(Url("ryp-siyah-1"), page.Items.Single(item => item.Id == seed.Single.Id).FamilyImageUrl);
        // The Düşük Stok window reads the same list: a filtered page still shows the whole family's gallery front.
        var filtered = await catalog.ListProductsAsync(seed.Tenant.Id, 100, null, null, "Kahverengi", null, null, null, CancellationToken.None);
        var filteredFamily = filtered.Items.Where(item => seed.Cards.Values.Any(card => card.Id == item.Id)).ToList();
        Assert.NotEmpty(filteredFamily);
        Assert.All(filteredFamily, card => Assert.Equal(Url("haki-1"), card.FamilyImageUrl));
    }

    // Live MZ051BYC today (read-only dry run, 12.11.2026): the Siyah card's gallery holds every colour's pictures in
    // front of its own. Hepsiburada, N11 and Pazarama send Siyah's sizes Siyah's pictures; PttAVM's one family product
    // takes each card's pictures through the same rule, so Siyah's pictures go there too.
    [PostgreSqlFact]
    public async Task PttAvmFamilyImages_CarrySiyahsPictures_EvenWhileItsGalleryHoldsEveryColour()
    {
        var seed = await SeedAsync(live: false, siyahGalleryHoldsEveryColour: true);
        await using var db = fixture.CreateContext();
        var composer = new ProductPublicationComposer(db, new ConfigurationBuilder().Build());
        var ptt = JsonNode.Parse(await BuildAsync(composer, seed, "PTTAVM", seed.Cards["Siyah"].Id))!.AsObject();
        var images = ptt["images"]!.AsArray().Select(image => image!.GetValue<string>()).ToList();
        Assert.All(Pictures("siyah", 3), picture => Assert.Contains(Url(picture), images));
        // The variant-level marketplaces are right on the same card.
        var hb = JsonNode.Parse(await BuildAsync(composer, seed, "HEPSIBURADA", seed.Cards["Siyah"].Id))!.AsArray();
        Assert.All(hb, item => Assert.Equal(Expected("siyah", 3, 5), Enumerable.Range(1, 5).Select(index => item!["attributes"]![$"Image{index}"]?.GetValue<string>()).OfType<string>()));
    }

    // Ürünleri Dışa Aktar / İçe Aktar for the model: every colour card's row carries its own name, the brand and each
    // size's desi; read straight back with every field picked it proposes nothing, also after the model name and
    // brand were changed from one colour row.
    [PostgreSqlFact]
    public async Task TheProductsExcel_RoundTripsAFamilysNames_Brand_AndDesi()
    {
        var seed = await SeedAsync(live: false);
        await using (var db = fixture.CreateContext())
            await AssertExportAsync(db, seed, colour => $"Kadın {colour} Triko Bluz", "Ravencia");
        await using (var db = fixture.CreateContext())
        {
            var siyah = seed.Cards["Siyah"];
            var updated = await Catalog(db).UpdateProductAsync(seed.Tenant.Id, siyah.Id, siyah.Version,
                new UpdateProductCommand("Kadın Siyah Yumuşak Triko Bluz", siyah.Description, seed.OtherBrandId, siyah.CategoryId,
                    FamilySharedFields: [ProductFamilySharedFields.Title, ProductFamilySharedFields.Brand]), CancellationToken.None);
            Assert.True(updated.Succeeded, updated.Error?.Message);
        }
        await using (var db = fixture.CreateContext())
            await AssertExportAsync(db, seed, colour => $"Kadın {colour} Yumuşak Triko Bluz", "Moda Evi");
    }

    private async Task AssertExportAsync(AppDbContext db, Seed seed, Func<string, string> title, string brand)
    {
        var service = new ProductTransferService(db, fixture.TimeProvider);
        var export = await service.ExportAsync(seed.Tenant.Id, ProductTransferExportFilter.All, null, null, CancellationToken.None);
        Assert.True(export.Succeeded, export.Error?.Message);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(export.Value!), 64 * 1024 * 1024);
        int Column(string header) => table.Headers.ToList().IndexOf(header);
        foreach (var (colour, _, _, desi) in Colours)
            foreach (var variant in seed.Sizes[colour])
            {
                var row = table.Rows.Single(row => row.Cells[Column("Stok Kodu")] == variant.Sku);
                Assert.Equal(title(colour), row.Cells[Column("Ürün Adı")]);
                Assert.Equal(brand, row.Cells[Column("Marka")]);
                Assert.Equal(ModelCode, row.Cells[Column("Model Kodu")]);
                Assert.Equal(desi, decimal.Parse(row.Cells[Column("Desi")]!, System.Globalization.CultureInfo.InvariantCulture));
            }
        var roundTrip = await service.PreviewAsync(seed.Tenant.Id, "urunler.xlsx", new MemoryStream(export.Value!), [ProductTransferFields.Title, ProductTransferFields.Barcode, ProductTransferFields.ModelCode, ProductTransferFields.Vat, ProductTransferFields.Desi], CancellationToken.None);
        Assert.True(roundTrip.Succeeded, roundTrip.Error?.Message);
        Assert.Equal((0, 0), (roundTrip.Value!.Changes.Count, roundTrip.Value.ProductChanges?.Count ?? 0));
    }

    private CatalogService Catalog(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static async Task<string> BuildAsync(ProductPublicationComposer composer, Seed seed, string platform, Guid productId)
    {
        var draft = await composer.BuildAsync(seed.Tenant.Id, productId, seed.Connections[platform].Id, CancellationToken.None);
        Assert.True(draft.Succeeded, $"{platform}: {draft.Error?.Code} {draft.Error?.Message}");
        return draft.Value!.PayloadJson;
    }

    internal sealed record MediaRow(Guid? VariantId, Guid AssetId, string Url);

    private sealed record Seed(Tenant Tenant, Dictionary<string, PlatformConnection> Connections, Dictionary<string, Product> Cards, Dictionary<string, List<ProductVariant>> Sizes,
        Product Single, List<ProductVariant> SingleSizes, List<MediaRow> SingleMedia, Guid OtherBrandId, string PazaramaBrandExternalId, string PazaramaOtherBrandExternalId);

    // live: every variant is on every marketplace (variant link, LIVE listing), the shape the price/stock send reads; the
    // publication tests use cards nothing was sent of yet. siyahGalleryHoldsBordo: the live MZ051BYC Siyah card before
    // its first Kaydet (Bordo's pictures copied in front of its gallery, only its XL assigned).
    private async Task<Seed> SeedAsync(bool live, bool siyahGalleryHoldsBordo = true, bool siyahGalleryHoldsEveryColour = false)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"famreg-{Guid.NewGuid():N}", DisplayName = "famreg", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        var otherBrandId = Guid.CreateVersion7();
        var pazaramaCategory = Guid.NewGuid().ToString();
        var pazaramaBrand = Guid.NewGuid().ToString();
        var pazaramaOtherBrand = Guid.NewGuid().ToString();
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = otherBrandId, TenantId = tenant.Id, Name = "Moda Evi", NormalizedName = "MODA EVI", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });

        var connections = new Dictionary<string, PlatformConnection>();
        foreach (var platform in Platforms.Append("TRENDYOL"))
        {
            var settings = platform == "N11" ? "{\"N11ShipmentTemplate\":\"Standart\",\"ExternalWritesEnabled\":true}" : "{\"ExternalWritesEnabled\":true}";
            var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = $"{platform} aile", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = settings, Version = 1 };
            connections[platform] = connection;
            db.PlatformConnections.Add(connection);
            var category = platform == "PAZARAMA" ? pazaramaCategory : "123";
            ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
            ReferenceItem Item(Guid snapshotId, string type, string externalId, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{type}-{externalId}" };
            var categories = Snapshot("CATEGORIES", "");
            var brands = Snapshot("BRANDS", "");
            var attributes = Snapshot("CATEGORY_ATTRIBUTES", category);
            db.ReferenceSnapshots.AddRange(categories, brands, attributes);
            var (brandExternal, otherBrandExternal) = platform == "PAZARAMA" ? (pazaramaBrand, pazaramaOtherBrand) : ("456", "457");
            db.ReferenceItems.AddRange(Item(categories.Id, "CATEGORIES", category, "Bluz"), Item(brands.Id, "BRANDS", brandExternal, "Ravencia"), Item(brands.Id, "BRANDS", otherBrandExternal, "Moda Evi"));
            db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = category, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
            db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = brandExternal, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
            db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = otherBrandId, ExternalId = otherBrandExternal, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        }

        var assets = new Dictionary<string, FileAsset>();
        FileAsset Asset(string name)
        {
            if (assets.TryGetValue(name, out var existing)) return existing;
            return assets[name] = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = Url(name), MimeType = "image/remote", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
        }
        var singleMedia = new List<MediaRow>();
        void AddPictures(Product card, ProductVariant? variant, IEnumerable<string> names, int start = 0)
        {
            foreach (var (name, index) in names.Select((name, index) => (name, index)))
            {
                db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, VariantId = variant?.Id, FileAssetId = Asset(name).Id, MediaRole = index == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY", SortOrder = (start + index) * 10, Status = "ACTIVE" });
                if (card.Title.StartsWith("Erkek", StringComparison.Ordinal)) singleMedia.Add(new(variant?.Id, Asset(name).Id, Url(name)));
            }
        }

        var cards = new Dictionary<string, Product>();
        var sizesByColour = new Dictionary<string, List<ProductVariant>>();
        var number = 0;
        Product NewCard(string title, DateTimeOffset createdAt) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = title, Description = "Yumuşak dokulu triko bluz.", CategoryId = categoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", WarrantyMonths = 0, DeliveryDays = 2, Status = ProductStatus.Active, CreatedAt = createdAt, UpdatedAt = createdAt, Version = 1 };
        ProductVariant NewVariant(Product card, string sku, string modelCode, string colour, string size, decimal desi, int sortOrder) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku, Barcode = sku, BarcodeNormalized = sku, ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = desi, SortOrder = sortOrder, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };

        void Sellable(Product card, ProductVariant variant)
        {
            db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 4m, Available = 4m, ProjectionVersion = 1, Version = 1 });
            foreach (var connection in connections.Values)
            {
                db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = 630m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 2, LastPriceHash = PriceInventoryComposer.StandardPriceHash(600m, 600m, 1), FollowsPanelPrice = true, Version = 2 });
                if (!live) continue;
                var externalId = connection.PlatformCode == "HEPSIBURADA" ? $"HBV{variant.Sku.Replace("-", "")}" : variant.Sku;
                db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = externalId, ExternalBarcode = variant.Sku, ExternalSku = variant.Sku });
                db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = "LIVE" });
            }
        }

        foreach (var (colour, key, count, desi) in Colours)
        {
            var card = NewCard($"Kadın {colour} Triko Bluz", now.AddMinutes(cards.Count));
            cards[colour] = card;
            db.Products.Add(card);
            var sizes = Sizes.Select((size, index) => NewVariant(card, $"{ModelCode}-{++number:00}", ModelCode, colour, size, desi, index)).ToList();
            sizesByColour[colour] = sizes;
            db.ProductVariants.AddRange(sizes);
            foreach (var variant in sizes) Sellable(card, variant);
            var own = Pictures(key, count);
            if (colour == "Siyah" && siyahGalleryHoldsEveryColour)
            {
                AddPictures(card, null, [.. Colours.Where(other => other.Colour != "Siyah").SelectMany(other => Pictures(other.Key, other.Pictures)), .. own, Chart]);
                AddPictures(card, sizes[3], own, start: Colours.Where(other => other.Colour != "Siyah").Sum(other => other.Pictures));
            }
            else if (colour == "Siyah" && siyahGalleryHoldsBordo)
            {
                AddPictures(card, null, [.. Pictures("bordo", 3), .. own, Chart]);
                AddPictures(card, sizes[3], own, start: 3);
            }
            else if (colour == "Gri")
            {
                AddPictures(card, null, [.. own, Chart]);
                foreach (var variant in sizes) AddPictures(card, variant, own);
            }
            else
            {
                AddPictures(card, null, [.. own, Chart]);
                AddPictures(card, sizes[0], own);
            }
            db.ProductSizeCharts.Add(new ProductSizeChart { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, FileAssetId = Asset(Chart).Id, UpdatedAt = now });
            foreach (var connection in connections.Values) Profile(connection, card, sizes);
        }

        // An unchanged single-colour card of another model (imported: gallery and first size hold the same four pictures).
        var single = NewCard("Erkek Siyah Basic Tişört", now.AddMinutes(30));
        db.Products.Add(single);
        var singleSizes = new[] { "M", "L", "XL" }.Select((size, index) => NewVariant(single, $"RYP001X-{index + 1:00}", "RYP001X", "Siyah", size, 1m, index)).ToList();
        db.ProductVariants.AddRange(singleSizes);
        foreach (var variant in singleSizes) Sellable(single, variant);
        AddPictures(single, null, Pictures("ryp-siyah", 4));
        AddPictures(single, singleSizes[0], Pictures("ryp-siyah", 4));
        foreach (var connection in connections.Values) Profile(connection, single, singleSizes);

        db.FileAssets.AddRange(assets.Values);
        await db.SaveChangesAsync();
        return new(tenant, connections, cards, sizesByColour, single, singleSizes, singleMedia, otherBrandId, pazaramaBrand, pazaramaOtherBrand);

        void Profile(PlatformConnection connection, Product card, IEnumerable<ProductVariant> sizes)
        {
            var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = live ? "LIVE" : "DRAFT", Source = live ? ChannelListingProfileSource.MarketplaceImport : null, Version = 1 };
            db.ChannelListingProfiles.Add(profile);
            if (!live) return;
            foreach (var variant in sizes)
                db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = variant.Id, ExternalBarcode = variant.Sku, ExternalSku = variant.Sku, DesiredStatus = "LIVE", ActualStatus = "LIVE" });
        }
    }
}
