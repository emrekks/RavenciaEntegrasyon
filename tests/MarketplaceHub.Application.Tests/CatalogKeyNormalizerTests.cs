using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// K1/K2: one key and one option signature for the panel, CSV import and marketplace import.
public sealed class CatalogKeyNormalizerTests
{
    [Theory]
    [InlineData("ABC_01", "ABC-01")]
    [InlineData("abc-01", "ABC-01")]
    [InlineData(" ABC 01 ", "ABC-01")]
    [InlineData("Beden/Yaş", "BEDEN-YAS")]
    [InlineData("Gümüş", "GUMUS")]
    [InlineData("GUMUS", "GUMUS")]
    [InlineData("İpek", "IPEK")]
    [InlineData("ipek", "IPEK")]
    [InlineData("IŞIK", "ISIK")]
    [InlineData("ışık", "ISIK")]
    [InlineData("Çok Renkli", "COK-RENKLI")]
    [InlineData("--x--", "X")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Key_FoldsCaseAccentsTurkishIAndSeparators(string? value, string expected) =>
        Assert.Equal(expected, CatalogKeyNormalizer.Key(value));

    [Fact]
    public void Key_IsIdempotentOverTheLegacyPanelForm()
    {
        foreach (var value in new[] { "ABC_01", "Beden/Yaş", "İpek", "ışık", "TOMMY HILFIGER", "Gümüş Kolye 925" })
        {
            Assert.Equal(CatalogKeyNormalizer.Key(value), CatalogKeyNormalizer.Key(CatalogKeyNormalizer.Legacy(value)));
            Assert.Equal(CatalogKeyNormalizer.Key(value), CatalogKeyNormalizer.Key(CatalogKeyNormalizer.Key(value)));
        }
    }

    [Fact]
    public void Key_TruncatesAfterFolding()
    {
        Assert.Equal("A-B-C-D", CatalogKeyNormalizer.Key("a b c d e", 7));
        Assert.Equal(160, CatalogKeyNormalizer.Key(new string('x', 400)).Length);
    }

    [Fact]
    public void LookupKeys_ReturnTheSharedAndTheLegacyForm()
    {
        Assert.Equal(["ABC-01", "ABC_01"], CatalogKeyNormalizer.LookupKeys("abc_01"));
        Assert.Equal(["ABC-01"], CatalogKeyNormalizer.LookupKeys("ABC-01"));
        Assert.Empty(CatalogKeyNormalizer.LookupKeys(" "));
        Assert.Equal(["X-1", "X_1", "Y"], CatalogKeyNormalizer.LookupKeys(["x_1", "X-1", null, "y"]));
        // The marketplace import used to keep the dotless ı.
        Assert.Equal(["KIRMIZI-ELBISE", "KıRMıZı-ELBISE", "KıRMıZı ELBISE"], CatalogKeyNormalizer.LookupKeys("Kırmızı Elbise"));
    }

    [Fact]
    public void SameKey_MatchesStoredKeysOfEitherForm()
    {
        Assert.True(CatalogKeyNormalizer.SameKey("ABC_01", "abc-01"));
        Assert.True(CatalogKeyNormalizer.SameKey("TOMMY HILFIGER", "TOMMY-HILFIGER"));
        Assert.True(CatalogKeyNormalizer.SameKey("İPEK", "ipek"));
        Assert.False(CatalogKeyNormalizer.SameKey("", ""));
        Assert.False(CatalogKeyNormalizer.SameKey(null, "x"));
        Assert.False(CatalogKeyNormalizer.SameKey("ABC-01", "ABC-02"));
    }

    [Fact]
    public void Signature_IsStableForOptionNameCase()
    {
        var first = VariantOptionSignature.Build(new Dictionary<string, string> { ["beden"] = "XL", ["Renk"] = "Mavi" });
        var second = VariantOptionSignature.Build(new Dictionary<string, string> { ["Renk"] = "Mavi", ["beden"] = "XL" });
        Assert.Equal("Renk: Mavi | beden: XL", first);
        Assert.Equal(first, second);
        Assert.Equal("Renk: Kırmızı | Beden: M | Kumaş: Pamuk | Web Color: Kırmızı",
            VariantOptionSignature.Build(new Dictionary<string, string> { ["Web Color"] = "Kırmızı", ["Kumaş"] = "Pamuk", ["Beden"] = "M", ["Renk"] = "\"Kırmızı\"" }));
        Assert.Equal("-", VariantOptionSignature.Build(new Dictionary<string, string>(), empty: "-"));
        Assert.Equal("Renk: Mavi", VariantOptionSignature.Build(new Dictionary<string, string> { ["Renk"] = "Mavi", ["RENK"] = "Siyah", ["Beden"] = " " }));
    }

    [Fact]
    public void Signature_ParsesTheNewAndTheLegacyPanelForm()
    {
        Assert.Equal(new Dictionary<string, string> { ["RENK"] = "MAVI", ["BEDEN"] = "XL" }, VariantOptionSignature.ParseKeyed("RENK=MAVI|BEDEN=XL"));
        Assert.Equal(new Dictionary<string, string> { ["RENK"] = "MAVI", ["BEDEN"] = "XL" }, VariantOptionSignature.ParseKeyed("Renk: Mavi | Beden: XL"));
        Assert.Empty(VariantOptionSignature.Parse("-"));
        // A bare '|' inside a value of the current form is part of the value.
        Assert.Equal(new Dictionary<string, string> { ["RENK"] = "SIYAH-BEYAZ", ["BEDEN"] = "XL" }, VariantOptionSignature.ParseKeyed("Renk: Siyah|Beyaz | Beden: XL"));
        Assert.Equal("Siyah|Beyaz", Assert.Single(VariantOptionSignature.Parse("Renk: Siyah|Beyaz")).Value);
        Assert.Equal("36:38", VariantOptionSignature.Parse("Beden: 36:38").Single().Value);
    }

    // A Shopify Variant ID is kept as its number whether it is typed, pasted as a gid or as an admin link; a Hepsiburada
    // SKU is kept as typed; an empty code means no link.
    [Theory]
    [InlineData("SHOPIFY", "44791234567890", "44791234567890")]
    [InlineData("SHOPIFY", " gid://shopify/ProductVariant/44791234567890 ", "44791234567890")]
    [InlineData("SHOPIFY", "https://admin.shopify.com/store/ravencia/products/8301/variants/44791234567890?x=1", "44791234567890")]
    [InlineData("HEPSIBURADA", " HBCV0000HANLRA ", "HBCV0000HANLRA")]
    [InlineData("HEPSIBURADA", "", null)]
    public void MarketplaceCode_ReadsWhatIsTyped(string platform, string typed, string? expected)
    {
        Assert.Equal(expected, CatalogService.MarketplaceCode(platform, typed, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("SHOPIFY", "QUDE2410307")]
    [InlineData("HEPSIBURADA", "HBCV 0000")]
    public void MarketplaceCode_RefusesWhatCannotBeACode(string platform, string typed)
    {
        Assert.Null(CatalogService.MarketplaceCode(platform, typed, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void SameColorAndSize_ReadsThePanelSignature()
    {
        var remote = new Dictionary<string, string> { ["Renk"] = "Mavi", ["Beden"] = "XL" };
        Assert.True(MarketplaceJobProcessor.SameColorAndSize("RENK=MAVI|BEDEN=XL", remote));
        Assert.True(MarketplaceJobProcessor.SameColorAndSize("Renk: Mavi | Beden: XL", remote));
        Assert.False(MarketplaceJobProcessor.SameColorAndSize("RENK=MAVI|BEDEN=L", remote));
    }

    [Fact]
    public void VariantNamesMatchByTheirValuesAndTheTitleColour()
    {
        // PTT AVM names a model's variants by value only ("S"), with the colour in the product title.
        var size = new Dictionary<string, string> { ["Varyant"] = "S" };
        Assert.True(MarketplaceJobProcessor.HasOptionValues("Renk: Bordo | Beden: S | Boy: Mini", size));
        Assert.False(MarketplaceJobProcessor.HasOptionValues("Renk: Bordo | Beden: M | Boy: Mini", size));
        Assert.True(MarketplaceJobProcessor.HasOptionValues("Renk: Lacivert | Beden: L", new Dictionary<string, string> { ["Varyant"] = "Lacivert / L" }));
        Assert.False(MarketplaceJobProcessor.HasOptionValues(null, size));
        Assert.True(MarketplaceJobProcessor.TitleNamesColor("Bordo Kaşe Mini Etek", "Renk: Bordo | Beden: S"));
        Assert.False(MarketplaceJobProcessor.TitleNamesColor("Bordo Kaşe Mini Etek", "Renk: Haki | Beden: S"));
        Assert.True(MarketplaceJobProcessor.TitleNamesColor("Siyah-Beyaz Kolsuz Bluz", "Renk: Siyah-Beyaz | Beden: S"));
        Assert.False(MarketplaceJobProcessor.TitleNamesColor("Siyahlı Bluz", "Renk: Siyah | Beden: S"));
    }

    [Theory]
    [InlineData("İpek Şal Mavi", "ipek", true)]
    [InlineData("TOMMY HILFIGER Gömlek", "Tommy-Hilfiger", true)]
    [InlineData("Ipekyolu Şal", "İpek", false)]
    [InlineData("Şal İpek", "İpek", false)]
    public void HepsiburadaTitle_StartsWithTheBrandOnTheSharedKey(string title, string brand, bool expected) =>
        Assert.Equal(expected, HepsiburadaProductPublicationComposer.StartsWithBrand(title, brand));

    [Theory]
    [InlineData("Kışlık Yonca Bluz", "Ravencia", "Ravencia Kışlık Yonca Bluz")]
    [InlineData("RAVENCIA Kışlık Yonca Bluz", "Ravencia", "RAVENCIA Kışlık Yonca Bluz")]
    [InlineData("Şal İpek", " İpek ", "İpek Şal İpek")]
    public void HepsiburadaTitle_GetsTheBrandInFrontOnlyWhenItLacksIt(string title, string brand, string expected) =>
        Assert.Equal(expected, HepsiburadaProductPublicationComposer.TitleWithBrand(title, brand));

    [Fact]
    public void VariantAttributes_UseTheVariantsOwnValueOverTheProductLevelOne()
    {
        Guid colour = Guid.NewGuid(), fabric = Guid.NewGuid(), brown = Guid.NewGuid(), viscose = Guid.NewGuid();
        Guid variantA = Guid.NewGuid(), variantB = Guid.NewGuid();
        MarketplaceHub.Domain.ProductAttributeAssignment Row(Guid attributeId, Guid? variantId, Guid valueId) => new() { Id = Guid.NewGuid(), AttributeId = attributeId, VariantId = variantId, ValueId = valueId };
        var assignments = new[] { Row(colour, null, brown), Row(colour, variantA, brown), Row(fabric, null, viscose) };

        // Variant A has its own Renk: one value, not the product's and its own together.
        var forA = VariantAttributeAssignments.ForVariant(assignments, variantA).ToDictionary(group => group.Key, group => group.ToList());
        Assert.Equal(variantA, Assert.Single(forA[colour]).VariantId);
        Assert.Null(Assert.Single(forA[fabric]).VariantId);
        // Variant B has none of its own: the product-level values stand.
        var forB = VariantAttributeAssignments.ForVariant(assignments, variantB).ToDictionary(group => group.Key, group => group.ToList());
        Assert.Null(Assert.Single(forB[colour]).VariantId);
    }
}

// K1 (orders and returns), K23 (catalog search).
public sealed class CatalogLookupTests
{
    [Fact]
    public void OrderLineLookupKeys_IncludeTheSharedAndTheLegacyForms()
    {
        var keys = MarketplaceSalesService.CatalogLookupKeys("abc_01");
        Assert.Contains("ABC-01", keys);
        Assert.Contains("ABC_01", keys);
        Assert.Contains("KIRMIZI-1", MarketplaceSalesService.CatalogLookupKeys("Kırmızı 1"));
        Assert.Contains("ABC", MarketplaceSalesService.CatalogLookupKeys("000ABC"));
    }

    [Theory]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("x\\y", "x\\\\y")]
    [InlineData("plain", "plain")]
    public void SearchText_IsEscapedForLike(string value, string expected) =>
        Assert.Equal(expected, CatalogService.EscapeLike(value));
}

// K1: groundwork for the deferred key recompute (not run anywhere yet): this SQL folds values exactly like
// CatalogKeyNormalizer.Key, so a later migration can recompute the stored columns in the database.
[Collection("postgres-integration")]
public sealed class CatalogKeyRecomputeSqlParityTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    internal const string Functions = """
        CREATE OR REPLACE FUNCTION pg_temp.catalog_key_safe(v text) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
          SELECT v IS NOT NULL AND v ~ '^[ -~çğıöşüÇĞİÖŞÜâîûÂÎÛéèêëÉÈÊËáàäãåÁÀÄÃÅóòôõÓÒÔÕúùÚÙíìïÍÌÏñÑ]*$'
        $$;
        CREATE OR REPLACE FUNCTION pg_temp.catalog_key(v text, maxlen int) RETURNS text LANGUAGE sql IMMUTABLE AS $$
          SELECT CASE WHEN v IS NULL THEN '' ELSE left(btrim(regexp_replace(upper(translate(v,
            'çğıöşüÇĞİÖŞÜâîûÂÎÛéèêëÉÈÊËáàäãåÁÀÄÃÅóòôõÓÒÔÕúùÚÙíìïÍÌÏñÑ',
            'cgiosuCGIOSUaiuAIUeeeeEEEEaaaaaAAAAAooooOOOOuuUUiiiIIInN') COLLATE "C"), '[^A-Z0-9]+', '-', 'g'), '-'), maxlen) END
        $$;
        """;

    [PostgreSqlFact]
    public async Task RecomputeFunction_MatchesTheCSharpKey()
    {
        var samples = new[]
        {
            "ABC_01", "abc-01", " ABC 01 ", "Beden/Yaş", "Gümüş", "İpek", "ipek", "IŞIK", "ışık", "Çok Renkli", "--x--", "",
            "TOMMY HILFIGER", "L'Oréal Paris", "Crème Brûlée", "Ñandú", "Ångström", "Açık Mavi / Lacivert", "ÜRÜN-123_ab.c",
            "çğıöşüÇĞİÖŞÜâîûÂÎÛéèêëÉÈÊËáàäãåÁÀÄÃÅóòôõÓÒÔÕúùÚÙíìïÍÌÏñÑ", new string('ğ', 200) + "x"
        };
        await using var db = fixture.CreateContext();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync(Functions);
        foreach (var sample in samples)
        {
            var safe = await db.Database.SqlQuery<bool>($"SELECT pg_temp.catalog_key_safe({sample}) AS \"Value\"").SingleAsync();
            Assert.True(safe, sample);
            var key = await db.Database.SqlQuery<string>($"SELECT pg_temp.catalog_key({sample}, 160) AS \"Value\"").SingleAsync();
            Assert.Equal(CatalogKeyNormalizer.Key(sample), key);
        }
        foreach (var unsafeSample in new[] { "Straße", "Ørsted", "a\tb", "日本" })
            Assert.False(await db.Database.SqlQuery<bool>($"SELECT pg_temp.catalog_key_safe({unsafeSample}) AS \"Value\"").SingleAsync(), unsafeSample);
    }
}
