using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Regression (owner, 12.11.2026: "Trendyol'dan ürün çekimlerinde de doğru eşleşme ile çekileceğinden ürünlerin sorunsuz
// çekileceğini de test et"): the Trendyol product import (PRODUCT_SYNC through TrendyolJsonMapper.CatalogProducts and
// MarketplaceJobProcessor.SyncProducts / UpsertCatalogProduct) of an MZ051BYC-shaped model after the family gallery
// (a6692297b), the model fields written to every colour card (91dadde7d, 1bdfac872) and the per-variant picture lists
// (f680abdd1, 1bdfac872). Trendyol lists one content (contentId) per colour, all under the model's productMainId; the
// import opens one panel card per content, every variant carrying the productMainId as its model code (the family).
[Collection("postgres-integration")]
public sealed class TrendyolImportRegressionPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string ModelCode = "MZ051BYC";
    private const string BrandExternalId = "456";
    private const string CategoryExternalId = "123";
    private const string Chart = "beden-tablosu";
    private static readonly string[] Sizes = ["S", "M", "L", "XL", "XXL"];
    private static readonly (string Colour, string Key, int Pictures, decimal Desi)[] Colours =
    [
        ("Bordo", "bordo", 3, 1.5m), ("Haki", "haki", 3, 1.5m), ("Gri", "gri", 9, 1.5m), ("Siyah", "siyah", 3, 1.5m),
        ("Lacivert", "lacivert", 3, 2m), ("Kahverengi", "kahve", 3, 1.5m), ("Mürdüm", "murdum", 3, 1.5m)
    ];

    private static string Url(string name) => $"https://cdn.dsmcdn.com/mz051byc/{name}.jpg";
    private static string[] OwnPictures(string key, int count) => [.. Enumerable.Range(1, count).Select(index => Url($"{key}-{index}"))];
    // What Trendyol shows on a colour's content: its own pictures, the size chart last.
    private static string[] ContentPictures(string key, int count) => [.. OwnPictures(key, count), Url(Chart)];
    private static int ColourIndex(string colour) => Array.FindIndex(Colours, entry => entry.Colour == colour);
    private static string ContentId(string colour) => (1_500_000 + ColourIndex(colour)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    // Barcode and stock code differ on purpose, so a match by one is not a match by the other.
    private static string Barcode(int colour, int size) => $"86905100{colour:00}{size:00}";
    private static string StockCode(int colour, int size) => $"{ModelCode}-{colour * 10 + size:00}";
    private static string VariantId(int colour, int size) => (900_000 + colour * 100 + size).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Title(string colour) => $"Kadın {colour} Triko Bluz";

    private sealed record ContentSpec(string Colour, string[] Sizes, string? Title = null, string[]? Pictures = null, int Stock = 4, string? Description = null, string? StockCodePrefix = null);

    private static ContentSpec Spec(string colour) => new(colour, Sizes);

    private static string Page(IEnumerable<ContentSpec> contents) => JsonSerializer.Serialize(new
    {
        content = contents.Select(Content).ToList(),
        page = 0,
        size = 100,
        totalPages = 1,
        totalElements = contents.Count()
    });

    private static object Content(ContentSpec spec)
    {
        var (colour, key, count, desi) = Colours.Single(entry => entry.Colour == spec.Colour);
        var colourIndex = ColourIndex(colour);
        return new
        {
            contentId = long.Parse(ContentId(colour), System.Globalization.CultureInfo.InvariantCulture),
            productMainId = ModelCode,
            title = spec.Title ?? Title(colour),
            description = spec.Description ?? "Yumuşak dokulu triko bluz.",
            brand = new { id = long.Parse(BrandExternalId, System.Globalization.CultureInfo.InvariantCulture), name = "Ravencia" },
            category = new { id = long.Parse(CategoryExternalId, System.Globalization.CultureInfo.InvariantCulture), name = "Bluz" },
            images = (spec.Pictures ?? ContentPictures(key, count)).Select(url => new { url }).ToList(),
            attributes = new[] { new { attributeName = "Renk", attributeValue = colour } },
            variants = spec.Sizes.Select(size =>
            {
                var sizeIndex = Array.IndexOf(SizeOrder, size);
                return new
                {
                    variantId = long.Parse(VariantId(colourIndex, sizeIndex), System.Globalization.CultureInfo.InvariantCulture),
                    barcode = Barcode(colourIndex, sizeIndex),
                    stockCode = spec.StockCodePrefix is null ? StockCode(colourIndex, sizeIndex) : $"{spec.StockCodePrefix}-{colourIndex * 10 + sizeIndex:00}",
                    attributes = new[] { new { attributeName = "Beden", attributeValue = size } },
                    salePrice = 630m,
                    listPrice = 900m,
                    quantity = spec.Stock,
                    vatRate = 10,
                    dimensionalWeight = desi,
                    onSale = true,
                    archived = false
                };
            }).ToList()
        };
    }

    // XS is a size Trendyol adds later; its index keeps the earlier sizes' codes stable.
    private static readonly string[] SizeOrder = ["S", "M", "L", "XL", "XXL", "XS"];

    private static IReadOnlyList<RemoteCatalogProduct> Read(IEnumerable<ContentSpec> contents) => TrendyolJsonMapper.CatalogProducts(Page(contents)).Items;

    // 1. First import: one card per colour content, every barcode on its colour+size, nothing twice, the model fields.
    [PostgreSqlFact]
    public async Task FirstImport_PutsEachColourOnItsOwnCard_EachBarcodeOnItsColourAndSize_WithTheModelFields()
    {
        var seed = await SeedAsync();
        var port = new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour))));
        await RunAsync(seed, port, "{\"full\":true}");

        await using var db = fixture.CreateContext();
        var cards = await CardsAsync(db, seed);
        Assert.Equal(Colours.Length, cards.Count);
        foreach (var (colour, key, count, desi) in Colours)
        {
            var card = cards[ContentId(colour)];
            var colourIndex = ColourIndex(colour);
            Assert.Equal(Title(colour), card.Product.Title);
            Assert.Equal("Yumuşak dokulu triko bluz.", card.Product.Description);
            Assert.Equal(seed.BrandId, card.Product.BrandId);
            Assert.Equal(seed.CategoryId, card.Product.CategoryId);
            Assert.Equal(10m, card.Product.VatRate);
            Assert.Equal(Sizes.Length, card.Variants.Count);
            foreach (var (size, sizeIndex) in Sizes.Select((size, index) => (size, index)))
            {
                var variant = Assert.Single(card.Variants, row => row.Barcode == Barcode(colourIndex, sizeIndex));
                Assert.Equal(StockCode(colourIndex, sizeIndex), variant.Sku);
                Assert.Equal(ModelCode, variant.ModelCode);
                Assert.Contains($"Renk: {colour}", variant.OptionSignature, StringComparison.Ordinal);
                Assert.Contains($"Beden: {size}", variant.OptionSignature, StringComparison.Ordinal);
                Assert.Equal(desi, variant.Desi);
                var link = Assert.Single(seed.VariantLinks(db), row => row.VariantId == variant.Id);
                Assert.Equal((VariantId(colourIndex, sizeIndex), Barcode(colourIndex, sizeIndex), StockCode(colourIndex, sizeIndex)), (link.ExternalId, link.ExternalBarcode, link.ExternalSku));
            }

            // Pictures: the card's gallery is the content's pictures with the chart last; the colour's first size holds
            // the same list (Trendyol's content pictures), the other sizes hold none of their own.
            Assert.Equal(ContentPictures(key, count), await MediaUrlsAsync(db, card.Product.Id, null));
            var first = card.Variants.Single(row => row.Barcode == Barcode(colourIndex, 0));
            Assert.Equal(ContentPictures(key, count), await MediaUrlsAsync(db, card.Product.Id, first.Id));
            foreach (var other in card.Variants.Where(row => row.Id != first.Id))
                Assert.Empty(await MediaUrlsAsync(db, card.Product.Id, other.Id));
        }

        // No duplicates: 35 variants, 35 variant links, 7 product links, every barcode once in the tenant.
        var variants = await db.ProductVariants.AsNoTracking().Where(row => row.TenantId == seed.TenantId).ToListAsync();
        Assert.Equal(Colours.Length * Sizes.Length, variants.Count);
        Assert.Equal(variants.Count, variants.Select(row => row.BarcodeNormalized).Distinct().Count());
        Assert.Equal(variants.Count, await db.MarketplaceVariantLinks.CountAsync(row => row.TenantId == seed.TenantId));
        Assert.Equal(Colours.Length, await db.MarketplaceProductLinks.CountAsync(row => row.TenantId == seed.TenantId));
        Assert.All(variants, row => Assert.Equal(ModelCode, row.ModelCode));

        // The same read again changes nothing and adds nothing.
        var before = await StateAsync(seed);
        await RunAsync(seed, port, "{\"full\":true}");
        Assert.Equal(before, await StateAsync(seed));
    }

    // 2. Re-import of a family the owner edited: matched by the listing link, nothing doubled or moved, and the owner's
    // per-variant pictures and gallery order kept although Trendyol now sends other pictures and another title.
    [PostgreSqlFact]
    public async Task ReImport_MatchesByLink_MovesNothing_AndKeepsTheOwnersPictureAssignmentsAndGalleryOrder()
    {
        var seed = await SeedAsync();
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour)))), "{\"full\":true}");
        var siyah = await OwnerEditsSiyahAsync(seed);
        var placementBefore = await PlacementAsync(seed);
        var untouchedMediaBefore = await MediaRowsAsync(seed, exceptProductId: siyah.ProductId);

        // Trendyol now sends Siyah with its pictures reversed and a new title, every colour with other stock.
        var changed = Colours.Select(entry => entry.Colour == "Siyah"
            ? Spec("Siyah") with { Title = "Trendyol Siyah Başlık", Pictures = [.. ContentPictures("siyah", 3).Reverse()], Stock = 9 }
            : Spec(entry.Colour) with { Stock = 9 });
        await RunAsync(seed, new TrendyolCatalogPort(Read(changed)), "{\"full\":true}");

        Assert.Equal(placementBefore, await PlacementAsync(seed));
        Assert.Equal(siyah.Media, await MediaRowsAsync(seed, onlyProductId: siyah.ProductId));
        // The cards the owner did not touch keep their pictures row for row (same pictures, same order).
        Assert.Equal(untouchedMediaBefore, await MediaRowsAsync(seed, exceptProductId: siyah.ProductId));
        await using (var db = fixture.CreateContext())
        {
            // The owner's edit holds: the full import preserves an edited card (LOCAL_CHANGES_PENDING), title included.
            Assert.Equal("Sahibin Siyah Başlığı", (await db.Products.AsNoTracking().SingleAsync(row => row.Id == siyah.ProductId)).Title);
            var link = await db.MarketplaceProductLinks.AsNoTracking().SingleAsync(row => row.ProductId == siyah.ProductId);
            Assert.Equal("LOCAL_CHANGES_PENDING", link.SyncStatus);
        }

        // "Tekil ürün çekimi" with "Mevcut Ürün Bilgilerini Güncelle" takes Trendyol's title over the owner's edit
        // (ProductImportMergePolicy.OverwriteLocalChanges, owner decision 7 Nov), and still keeps the owner's pictures.
        await RunAsync(seed, new TrendyolCatalogPort(Read(changed)), $"{{\"productLookup\":\"{ModelCode}\",\"updateExistingProducts\":true}}");
        Assert.Equal(placementBefore, await PlacementAsync(seed));
        Assert.Equal(siyah.Media, await MediaRowsAsync(seed, onlyProductId: siyah.ProductId));
        await using (var db = fixture.CreateContext())
            Assert.Equal("Trendyol Siyah Başlık", (await db.Products.AsNoTracking().SingleAsync(row => row.Id == siyah.ProductId)).Title);

        // The lookup cleared the card's marks; a later changed full import still keeps the pictures, because the card
        // gallery holds the editor's ORDERED_ rows (MediaIsUserManagedAsync).
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour) with { Stock = 11 }))), "{\"full\":true}");
        Assert.Equal(placementBefore, await PlacementAsync(seed));
        Assert.Equal(siyah.Media, await MediaRowsAsync(seed, onlyProductId: siyah.ProductId));
    }

    // 2b. A card whose pictures are still the import's own follows Trendyol's new picture order (it is not the owner's).
    [PostgreSqlFact]
    public async Task ReImport_OfAnUneditedCard_TakesTrendyolsNewPictureOrder()
    {
        var seed = await SeedAsync();
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour)))), "{\"full\":true}");
        var reversed = ContentPictures("haki", 3).Reverse().ToArray();
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => entry.Colour == "Haki" ? Spec("Haki") with { Pictures = reversed } : Spec(entry.Colour)))), "{\"full\":true}");

        await using var db = fixture.CreateContext();
        var haki = (await CardsAsync(db, seed))[ContentId("Haki")];
        Assert.Equal(reversed, await MediaUrlsAsync(db, haki.Product.Id, null));
        Assert.Equal(reversed, await MediaUrlsAsync(db, haki.Product.Id, haki.Variants.Single(row => row.Barcode == Barcode(ColourIndex("Haki"), 0)).Id));
    }

    // 2c. A family whose listing links are gone (published from the panel, links lost) is found by its stock codes:
    // nothing is opened twice and no variant changes card.
    [PostgreSqlFact]
    public async Task ReImport_WithoutLinks_FindsTheCardsByStockCode_AndOpensNothingTwice()
    {
        var seed = await SeedAsync();
        var port = new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour))));
        await RunAsync(seed, port, "{\"full\":true}");
        var placementBefore = await PlacementAsync(seed);
        await using (var db = fixture.CreateContext())
        {
            await db.MarketplaceVariantLinks.Where(row => row.TenantId == seed.TenantId).ExecuteDeleteAsync();
            await db.MarketplaceProductLinks.Where(row => row.TenantId == seed.TenantId).ExecuteDeleteAsync();
        }

        await RunAsync(seed, port, "{\"full\":true}");
        Assert.Equal(placementBefore, await PlacementAsync(seed));
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(Colours.Length, await db.Products.CountAsync(row => row.TenantId == seed.TenantId));
            // The import alone leaves the listing unlinked (it only brings stock and prices to the owner's card);
            // "Ürün Eşleme" links it.
            Assert.Equal(0, await db.MarketplaceVariantLinks.CountAsync(row => row.TenantId == seed.TenantId));
        }

        await RunAsync(seed, port, "{\"mappingOnly\":true}");
        Assert.Equal(placementBefore, await PlacementAsync(seed));
        await using (var db = fixture.CreateContext())
        {
            var links = await (from link in db.MarketplaceVariantLinks.AsNoTracking()
                               join variant in db.ProductVariants.AsNoTracking() on link.VariantId equals variant.Id
                               where link.TenantId == seed.TenantId
                               select new { link.ExternalId, variant.Barcode }).ToListAsync();
            Assert.Equal(Colours.Length * Sizes.Length, links.Count);
            foreach (var (colour, _, _, _) in Colours)
                foreach (var sizeIndex in Enumerable.Range(0, Sizes.Length))
                    Assert.Equal(Barcode(ColourIndex(colour), sizeIndex), links.Single(link => link.ExternalId == VariantId(ColourIndex(colour), sizeIndex)).Barcode);
        }
    }

    // 3. Trendyol adds a size to an existing colour and a new colour later.
    [PostgreSqlFact]
    public async Task VariantsAddedOnTrendyolLater_LandOnTheirColoursCard()
    {
        var seed = await SeedAsync();
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Where(entry => entry.Colour != "Mürdüm").Select(entry => Spec(entry.Colour)))), "{\"full\":true}");
        var siyah = await OwnerEditsSiyahAsync(seed);

        // XS joins Bordo (untouched card) and Siyah (the owner's card); Mürdüm appears as a new content.
        var later = Colours.Select(entry => entry.Colour is "Bordo" or "Siyah" ? Spec(entry.Colour) with { Sizes = [.. Sizes, "XS"] } : Spec(entry.Colour)).ToList();
        var port = new TrendyolCatalogPort(Read(later));
        await RunAsync(seed, port, "{\"full\":true}");

        await using (var db = fixture.CreateContext())
        {
            var cards = await CardsAsync(db, seed);
            Assert.Equal(Colours.Length, cards.Count);
            var bordo = cards[ContentId("Bordo")];
            var bordoXs = Assert.Single(bordo.Variants, row => row.Barcode == Barcode(ColourIndex("Bordo"), 5));
            Assert.Contains("Renk: Bordo", bordoXs.OptionSignature, StringComparison.Ordinal);
            Assert.Contains("Beden: XS", bordoXs.OptionSignature, StringComparison.Ordinal);
            Assert.Equal(ModelCode, bordoXs.ModelCode);
            // The new size is not its colour's first size on Trendyol: it gets no picture list of its own (the card's
            // gallery stands for it), the Bordo gallery is unchanged.
            Assert.Empty(await MediaUrlsAsync(db, bordo.Product.Id, bordoXs.Id));
            Assert.Equal(ContentPictures("bordo", 3), await MediaUrlsAsync(db, bordo.Product.Id, null));

            var murdum = cards[ContentId("Mürdüm")];
            Assert.Equal(Sizes.Length, murdum.Variants.Count);
            Assert.All(murdum.Variants, row => Assert.Contains("Renk: Mürdüm", row.OptionSignature, StringComparison.Ordinal));
            Assert.All(murdum.Variants, row => Assert.Equal(ModelCode, row.ModelCode));
            Assert.Equal(ContentPictures("murdum", 3), await MediaUrlsAsync(db, murdum.Product.Id, null));
            Assert.Equal(ContentPictures("murdum", 3), await MediaUrlsAsync(db, murdum.Product.Id, murdum.Variants.Single(row => row.Barcode == Barcode(ColourIndex("Mürdüm"), 0)).Id));

            // The owner's Siyah card is preserved by the full import, so its new XS is not added by it.
            Assert.DoesNotContain(cards[ContentId("Siyah")].Variants, row => row.Barcode == Barcode(ColourIndex("Siyah"), 5));
        }

        // "Ekli Olmayan Ürünler" (newOnly) adds the new size to the owner's card, without pictures of its own and without
        // touching the owner's picture rows.
        await RunAsync(seed, port, "{\"newOnly\":true}");
        await using (var db = fixture.CreateContext())
        {
            var cards = await CardsAsync(db, seed);
            var siyahCard = cards[ContentId("Siyah")];
            var siyahXs = Assert.Single(siyahCard.Variants, row => row.Barcode == Barcode(ColourIndex("Siyah"), 5));
            Assert.Contains("Renk: Siyah", siyahXs.OptionSignature, StringComparison.Ordinal);
            Assert.Empty(await MediaUrlsAsync(db, siyahCard.Product.Id, siyahXs.Id));
            Assert.Equal(Colours.Length * Sizes.Length + 2, await db.ProductVariants.CountAsync(row => row.TenantId == seed.TenantId));
        }
        Assert.Equal(siyah.Media, await MediaRowsAsync(seed, onlyProductId: siyah.ProductId));
    }

    // 4. Model fields: Trendyol brings another title and description for one colour only.
    [PostgreSqlFact]
    public async Task ChangedTitleOfOneColour_ChangesOnlyThatColoursCard_AndAnOwnersFamilyEditHoldsOnEveryCard()
    {
        var seed = await SeedAsync();
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour)))), "{\"full\":true}");
        var bordoChanged = Colours.Select(entry => entry.Colour == "Bordo"
            ? Spec("Bordo") with { Title = "Kadın Bordo Triko Bluz Yeni", Description = "Yeni açıklama." }
            : Spec(entry.Colour)).ToList();
        await RunAsync(seed, new TrendyolCatalogPort(Read(bordoChanged)), "{\"full\":true}");

        await using (var db = fixture.CreateContext())
        {
            var cards = await CardsAsync(db, seed);
            // Each colour card follows its own Trendyol content: the cards are left differing (Trendyol itself keeps
            // one title per content); the import never copies one content's title to the other colours.
            Assert.Equal(("Kadın Bordo Triko Bluz Yeni", "Yeni açıklama."), (cards[ContentId("Bordo")].Product.Title, cards[ContentId("Bordo")].Product.Description));
            foreach (var (colour, _, _, _) in Colours.Where(entry => entry.Colour != "Bordo"))
                Assert.Equal((Title(colour), "Yumuşak dokulu triko bluz."), (cards[ContentId(colour)].Product.Title, cards[ContentId(colour)].Product.Description));
        }

        // The owner writes the description to the whole family (the editor's model field marks every colour card's
        // link dirty and bumps every card). A later full import with yet another Trendyol text keeps the owner's on all.
        await using (var db = fixture.CreateContext())
        {
            var products = await db.Products.Where(row => row.TenantId == seed.TenantId).ToListAsync();
            foreach (var product in products) { product.Description = "Sahibin model açıklaması."; product.Version++; }
            await db.SaveChangesAsync();
            await CatalogLocalEdits.MarkLinksDirtyAsync(db, seed.TenantId, [.. products.Select(row => row.Id)], CatalogLocalEdits.ProductField, CancellationToken.None);
            await db.SaveChangesAsync();
        }
        var bordoAgain = Colours.Select(entry => entry.Colour == "Bordo"
            ? Spec("Bordo") with { Title = "Bordo Üçüncü", Description = "Trendyol üçüncü açıklama." }
            : Spec(entry.Colour) with { Stock = 7 }).ToList();
        await RunAsync(seed, new TrendyolCatalogPort(Read(bordoAgain)), "{\"full\":true}");
        await using (var db = fixture.CreateContext())
        {
            var products = await db.Products.AsNoTracking().Where(row => row.TenantId == seed.TenantId).ToListAsync();
            Assert.All(products, row => Assert.Equal("Sahibin model açıklaması.", row.Description));
            Assert.Equal("Kadın Bordo Triko Bluz Yeni", products.Single(row => row.Title.StartsWith("Kadın Bordo", StringComparison.Ordinal)).Title);
        }
    }

    // 2d. Variant links lost (the card links kept) and Bordo's stock codes renamed on Trendyol (live: MZ052BOD-16 →
    // MZ051BYC-16): every variant is found again by its stock code, else by its barcode inside its card; nothing is
    // opened twice, nothing changes colour, the renamed stock codes are taken and the links come back.
    [PostgreSqlFact]
    public async Task ReImport_WithoutVariantLinks_FindsVariantsByStockCodeThenBarcode_AndTakesARenamedStockCode()
    {
        var seed = await SeedAsync();
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour)))), "{\"full\":true}");
        Dictionary<string, Guid> idsByBarcode;
        await using (var db = fixture.CreateContext())
        {
            idsByBarcode = await db.ProductVariants.AsNoTracking().Where(row => row.TenantId == seed.TenantId).ToDictionaryAsync(row => row.Barcode!, row => row.Id);
            await db.MarketplaceVariantLinks.Where(row => row.TenantId == seed.TenantId).ExecuteDeleteAsync();
        }

        var renamed = Colours.Select(entry => entry.Colour == "Bordo" ? Spec("Bordo") with { StockCodePrefix = "MZ052BOD" } : Spec(entry.Colour));
        await RunAsync(seed, new TrendyolCatalogPort(Read(renamed)), "{\"full\":true}");

        await using (var db = fixture.CreateContext())
        {
            var cards = await CardsAsync(db, seed);
            Assert.Equal(Colours.Length, await db.Products.CountAsync(row => row.TenantId == seed.TenantId));
            var variants = await db.ProductVariants.AsNoTracking().Where(row => row.TenantId == seed.TenantId).ToListAsync();
            Assert.Equal(Colours.Length * Sizes.Length, variants.Count);
            Assert.All(variants, row => Assert.Equal(idsByBarcode[row.Barcode!], row.Id));
            foreach (var (colour, _, _, _) in Colours)
                Assert.All(cards[ContentId(colour)].Variants, row => Assert.Contains($"Renk: {colour}", row.OptionSignature, StringComparison.Ordinal));
            var bordo = ColourIndex("Bordo");
            Assert.All(cards[ContentId("Bordo")].Variants, row => Assert.StartsWith("MZ052BOD-", row.Sku, StringComparison.Ordinal));
            var links = await db.MarketplaceVariantLinks.AsNoTracking().Where(row => row.TenantId == seed.TenantId).ToListAsync();
            Assert.Equal(variants.Count, links.Count);
            Assert.All(links, link => Assert.Equal(variants.Single(row => row.Id == link.VariantId).Barcode, link.ExternalBarcode));
            Assert.Equal(StockCode(bordo, 0).Replace(ModelCode, "MZ052BOD", StringComparison.Ordinal), links.Single(link => link.ExternalId == VariantId(bordo, 0)).ExternalSku);
        }
    }

    // FINDING: the full and the incremental import ("Tam Katalog", "Yeni ve Değişen") keep an owner-edited card whole
    // (LOCAL_CHANGES_PENDING) and on that path only match the card's existing variants for stock and prices
    // (MarketplaceJobProcessor.cs:8499, SyncCatalogInventoryForPreservedProduct): a size Trendyol adds to that colour later
    // is not opened. A family edit marks every colour card (the model fields, the family gallery), so after one Kaydet no
    // new Trendyol size of the model reaches the panel through these modes; only "Ekli Olmayan Ürünler" (newOnly) or a
    // single lookup with "Mevcut Ürün Bilgilerini Güncelle" adds it (the second also takes Trendyol's texts over the
    // owner's). Its orders then match no panel variant until then.
    [PostgreSqlFact(Skip = "FINDING: full/incremental Trendyol import does not open a new Trendyol size on an owner-edited colour card (MarketplaceJobProcessor.cs:8499, preserved path matches existing variants only); only newOnly adds it.")]
    public async Task FullImport_OpensANewTrendyolSize_OnAnOwnerEditedColourCard()
    {
        var seed = await SeedAsync();
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour)))), "{\"full\":true}");
        var siyah = await OwnerEditsSiyahAsync(seed);
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => entry.Colour == "Siyah" ? Spec("Siyah") with { Sizes = [.. Sizes, "XS"] } : Spec(entry.Colour)))), "{\"full\":true}");

        await using var db = fixture.CreateContext();
        var card = (await CardsAsync(db, seed))[ContentId("Siyah")];
        Assert.Single(card.Variants, row => row.Barcode == Barcode(ColourIndex("Siyah"), 5));
        Assert.Equal(siyah.Media, await MediaRowsAsync(seed, onlyProductId: siyah.ProductId));
    }

    // The owner assigns pictures to one size only (the editor writes a size's own list with PRIMARY/GALLERY roles and
    // marks the card's links "media"); the card gallery keeps the import's roles. A lookup with "Mevcut Ürün Bilgilerini
    // Güncelle" clears that mark when it finishes, and a later full import that sees any change on Trendyol (stock) must
    // still leave the size's own list alone.
    // FINDING: it does not. The lookup keeps the list during its own run (the "media" mark is still on the link), then
    // resets the link (MarketplaceJobProcessor.cs:8516-8518: LastImportedProductVersion = Version, DirtyFieldsJson = null).
    // The next import that sees a change takes the card as unedited; MediaIsUserManagedAsync (:9555) finds no ORDERED_
    // row, no uploaded picture and no mark, and UpsertCatalogVariant (:8945) writes Trendyol's list for that size (empty
    // for every size after a colour's first), so the owner's two rows of size M end ARCHIVED.
    [PostgreSqlFact(Skip = "FINDING: after a lookup with 'Mevcut Ürün Bilgilerini Güncelle' clears the media mark (MarketplaceJobProcessor.cs:8516-8518), the next changed import archives a size's own picture list when the card gallery has no ORDERED_ row (:9555, :8945).")]
    public async Task OwnersSizeOnlyAssignment_SurvivesAnOverwritingLookup_AndALaterImport()
    {
        var seed = await SeedAsync();
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour)))), "{\"full\":true}");
        Guid productId;
        await using (var db = fixture.CreateContext())
        {
            var card = (await CardsAsync(db, seed))[ContentId("Haki")];
            productId = card.Product.Id;
            var gallery = await db.ProductMedia.AsNoTracking().Where(row => row.ProductId == productId && row.VariantId == null && row.Status == "ACTIVE").OrderBy(row => row.SortOrder).ToListAsync();
            var sizeM = card.Variants.Single(row => row.Barcode == Barcode(ColourIndex("Haki"), 1));
            db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ProductId = productId, VariantId = sizeM.Id, FileAssetId = gallery[1].FileAssetId, MediaRole = "PRIMARY", SortOrder = 0, Status = "ACTIVE" });
            db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ProductId = productId, VariantId = sizeM.Id, FileAssetId = gallery[0].FileAssetId, MediaRole = "GALLERY", SortOrder = 1, Status = "ACTIVE" });
            await db.SaveChangesAsync();
            await CatalogLocalEdits.MarkMediaEditedAsync(db, seed.TenantId, [productId], DateTimeOffset.UtcNow, CancellationToken.None);
            await db.SaveChangesAsync();
        }
        var assigned = await MediaRowsAsync(seed, onlyProductId: productId);

        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour) with { Stock = 5 }))), $"{{\"productLookup\":\"{ModelCode}\",\"updateExistingProducts\":true}}");
        Assert.Equal(assigned, await MediaRowsAsync(seed, onlyProductId: productId));
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour) with { Stock = 6 }))), "{\"full\":true}");
        Assert.Equal(assigned, await MediaRowsAsync(seed, onlyProductId: productId));
    }

    // 5. Order lines still find their variant by barcode after the import.
    [PostgreSqlFact]
    public async Task OrderLines_ResolveByBarcode_ToTheRightColourCard()
    {
        var seed = await SeedAsync();
        await RunAsync(seed, new TrendyolCatalogPort(Read(Colours.Select(entry => Spec(entry.Colour)))), "{\"full\":true}");

        await using var db = fixture.CreateContext();
        var lines = Colours.SelectMany(entry => Enumerable.Range(0, Sizes.Length).Select(sizeIndex => new OrderLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.TenantId,
            OrderId = Guid.CreateVersion7(),
            ExternalLineId = $"line-{entry.Key}-{sizeIndex}",
            // A Trendyol line whose merchant sku the panel does not know: only the barcode can find it.
            Sku = $"UNKNOWN-{entry.Key}-{sizeIndex}",
            Barcode = Barcode(ColourIndex(entry.Colour), sizeIndex),
            TitleSnapshot = Title(entry.Colour),
            RawStatus = "Created"
        })).ToList();
        var catalog = await OrderLineCatalog.LoadAsync(db, seed.TenantId, lines, _ => seed.ConnectionId, CancellationToken.None);
        var cards = await CardsAsync(db, seed);
        foreach (var line in lines)
        {
            var variant = catalog.Variant(line);
            Assert.NotNull(variant);
            Assert.Equal(line.Barcode, variant.Barcode);
            var colour = Colours.Single(entry => line.ExternalLineId.StartsWith($"line-{entry.Key}-", StringComparison.Ordinal)).Colour;
            Assert.Equal(cards[ContentId(colour)].Product.Id, variant.ProductId);
        }
    }

    // --- helpers ---

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid BrandId, Guid CategoryId)
    {
        public List<MarketplaceVariantLink> VariantLinks(AppDbContext db) =>
            [.. db.MarketplaceVariantLinks.AsNoTracking().Where(row => row.TenantId == TenantId && row.ConnectionId == ConnectionId)];
    }

    private sealed record Card(Product Product, List<ProductVariant> Variants);

    private sealed record SiyahEdit(Guid ProductId, string Media);

    private async Task<Seed> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"tyimport-{Guid.NewGuid():N}", DisplayName = "tyimport", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol içe aktarma", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        // Fresh reference snapshots, so the import reads no reference from Trendyol (the port is a product port only).
        ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
        ReferenceItem Item(Guid snapshotId, string type, string externalId, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{type}-{externalId}" };
        var categories = Snapshot("CATEGORIES", "");
        var brands = Snapshot("BRANDS", "");
        var attributes = Snapshot("CATEGORY_ATTRIBUTES", CategoryExternalId);
        db.ReferenceSnapshots.AddRange(categories, brands, attributes);
        db.ReferenceItems.AddRange(Item(categories.Id, "CATEGORIES", CategoryExternalId, "Bluz"), Item(brands.Id, "BRANDS", BrandExternalId, "Ravencia"));
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = CategoryExternalId, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = BrandExternalId, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, brandId, categoryId);
    }

    private async Task RunAsync(Seed seed, TrendyolCatalogPort port, string payload)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(seed.TenantId, seed.ConnectionId, MarketplaceJobTypes.ProductSync, payload, $"trendyol-import-regression-{Guid.NewGuid():N}", CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorSummary);
        Assert.Equal(0, port.WriteAttempts);
        Assert.False(await db.OperationalIssues.AnyAsync(row => row.TenantId == seed.TenantId && row.Status == IssueStatus.Open
            && (row.Code == "PRODUCT_IMPORT_FAILED" || row.Code == "PRODUCT_VARIANT_CONFLICT" || row.Code == "PRODUCT_VARIANT_LINK_CONFLICT" || row.Code == "PRODUCT_LINK_CONFLICT" || row.Code == "PRODUCT_BARCODE_CONFLICT")),
            string.Join(" | ", await db.OperationalIssues.AsNoTracking().Where(row => row.TenantId == seed.TenantId).Select(row => row.Code + ": " + row.Summary).ToListAsync()));
    }

    // Cards by their Trendyol content id.
    private static async Task<Dictionary<string, Card>> CardsAsync(AppDbContext db, Seed seed)
    {
        var links = await db.MarketplaceProductLinks.AsNoTracking().Where(row => row.TenantId == seed.TenantId && row.ConnectionId == seed.ConnectionId).ToListAsync();
        var products = await db.Products.AsNoTracking().Where(row => row.TenantId == seed.TenantId).ToListAsync();
        var variants = await db.ProductVariants.AsNoTracking().Where(row => row.TenantId == seed.TenantId).ToListAsync();
        return links.ToDictionary(link => link.ExternalId, link => new Card(products.Single(row => row.Id == link.ProductId), [.. variants.Where(row => row.ProductId == link.ProductId)]));
    }

    private static async Task<string[]> MediaUrlsAsync(AppDbContext db, Guid productId, Guid? variantId) =>
        [.. await (from media in db.ProductMedia.AsNoTracking()
                   join asset in db.FileAssets.AsNoTracking() on media.FileAssetId equals asset.Id
                   where media.ProductId == productId && media.VariantId == variantId && media.Status == "ACTIVE"
                   orderby media.SortOrder
                   select asset.RelativePath).ToListAsync()];

    // Which card and which colour/size every barcode sits on, as one comparable text.
    private async Task<string> PlacementAsync(Seed seed)
    {
        await using var db = fixture.CreateContext();
        var rows = await db.ProductVariants.AsNoTracking().Where(row => row.TenantId == seed.TenantId)
            .OrderBy(row => row.Barcode)
            .Select(row => row.Barcode + "@" + row.ProductId + "#" + row.Id + "|" + row.OptionSignature + "|" + row.ModelCode)
            .ToListAsync();
        var products = await db.Products.CountAsync(row => row.TenantId == seed.TenantId);
        return $"{products} cards\n{string.Join("\n", rows)}";
    }

    private async Task<string> StateAsync(Seed seed)
    {
        await using var db = fixture.CreateContext();
        var media = await MediaRowsAsync(seed);
        var links = await db.MarketplaceVariantLinks.AsNoTracking().Where(row => row.TenantId == seed.TenantId).OrderBy(row => row.ExternalId)
            .Select(row => row.ExternalId + ">" + row.VariantId + ">" + row.ExternalBarcode).ToListAsync();
        var productLinks = await db.MarketplaceProductLinks.CountAsync(row => row.TenantId == seed.TenantId);
        var titles = await db.Products.AsNoTracking().Where(row => row.TenantId == seed.TenantId).OrderBy(row => row.Id).Select(row => row.Id + ":" + row.Title + ":" + row.BrandId + ":" + row.CategoryId + ":" + row.VatRate).ToListAsync();
        return $"{await PlacementAsync(seed)}\n{media}\n{string.Join("\n", links)}\n{productLinks}\n{string.Join("\n", titles)}";
    }

    private async Task<string> MediaRowsAsync(Seed seed, Guid? onlyProductId = null, Guid? exceptProductId = null)
    {
        await using var db = fixture.CreateContext();
        var rows = await (from media in db.ProductMedia.AsNoTracking()
                          join asset in db.FileAssets.AsNoTracking() on media.FileAssetId equals asset.Id
                          where media.TenantId == seed.TenantId
                              && (onlyProductId == null || media.ProductId == onlyProductId)
                              && (exceptProductId == null || media.ProductId != exceptProductId)
                          orderby media.ProductId, media.VariantId, media.SortOrder, media.Id
                          select media.Id + "|" + media.ProductId + "|" + media.VariantId + "|" + asset.RelativePath + "|" + media.MediaRole + "|" + media.SortOrder + "|" + media.Status).ToListAsync();
        return string.Join("\n", rows);
    }

    // The owner opens Siyah in the editor and saves: the gallery in another order (ORDERED_ roles), the size M assigned
    // two pictures of its own, the title changed; the card's link is marked dirty and its version bumped.
    private async Task<SiyahEdit> OwnerEditsSiyahAsync(Seed seed)
    {
        Guid productId;
        await using (var db = fixture.CreateContext())
        {
            var card = (await CardsAsync(db, seed))[ContentId("Siyah")];
            productId = card.Product.Id;
            var rows = await db.ProductMedia.Where(row => row.ProductId == productId && row.Status == "ACTIVE").ToListAsync();
            var gallery = rows.Where(row => row.VariantId == null).OrderByDescending(row => row.SortOrder).ToList();
            foreach (var (row, index) in gallery.Select((row, index) => (row, index)))
            {
                row.SortOrder = index * 10;
                row.MediaRole = index == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY";
            }
            foreach (var row in rows.Where(row => row.VariantId != null)) row.MediaRole = row.SortOrder == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY";
            var sizeM = card.Variants.Single(row => row.Barcode == Barcode(ColourIndex("Siyah"), 1));
            foreach (var (row, index) in gallery.Take(2).Select((row, index) => (row, index)))
                db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ProductId = productId, VariantId = sizeM.Id, FileAssetId = row.FileAssetId, MediaRole = index == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY", SortOrder = index * 10, Status = "ACTIVE" });
            var product = await db.Products.SingleAsync(row => row.Id == productId);
            product.Title = "Sahibin Siyah Başlığı";
            product.Version++;
            await db.SaveChangesAsync();
            await CatalogLocalEdits.MarkLinksDirtyAsync(db, seed.TenantId, [productId], CatalogLocalEdits.ProductField, CancellationToken.None);
            await CatalogLocalEdits.MarkLinksDirtyAsync(db, seed.TenantId, [productId], CatalogLocalEdits.MediaField, CancellationToken.None);
            await db.SaveChangesAsync();
        }
        return new(productId, await MediaRowsAsync(seed, onlyProductId: productId));
    }

    private sealed class TrendyolCatalogPort(IReadOnlyList<RemoteCatalogProduct> snapshots) : IProductPort
    {
        public int WriteAttempts { get; private set; }

        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
        {
            // A single lookup by model code reads that model's contents only (Trendyol's productMainId filter).
            var items = string.IsNullOrWhiteSpace(filter.ProductMainId) ? snapshots : [.. snapshots.Where(row => row.ProductMainId == filter.ProductMainId)];
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(items, null, false, items.Count)));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => Reject<RemoteOperationRef>();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Reject<RemoteOperationRef>();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Reject<RemoteOperationRef>();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Reject<RemoteOperationRef>();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Reject<RemoteOperationRef>();
        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => Reject<RemoteOperationRef>();

        private Task<AdapterResult<T>> Reject<T>()
        {
            WriteAttempts++;
            throw new InvalidOperationException("A product import never writes to Trendyol.");
        }
    }
}
