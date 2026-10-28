using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A model family (the colour cards of one model code) is one Shopify product with the cards' options merged.
public sealed class ShopifyFamilyPublicationTests
{
    [Fact]
    public void RichTextDescriptionGoesAsHtmlAndPlainTextAsAParagraph()
    {
        var html = ShopifyProductPublicationComposer.DescriptionHtml("<div><h2>Bluz</h2><p>Kalın <strong>viskon</strong></p><script>alert(1)</script></div>");
        Assert.Contains("<h2>Bluz</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>viskon</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("<p>Kalın &amp; tok<br />İkinci satır</p>", ShopifyProductPublicationComposer.DescriptionHtml("Kalın & tok\r\nİkinci satır"));
        Assert.Equal("<p>5 &lt; 6</p>", ShopifyProductPublicationComposer.DescriptionHtml("5 < 6"));
    }

    [Fact]
    public void ColourCardsMergeIntoOneProductWithColourAndSizeOptions()
    {
        var black = Card(0, "Siyah", ("MZ-S-L", "L"), ("MZ-S-XL", "XL"));
        var brown = Card(1, "Kahverengi", ("MZ-K-L", "L"), ("MZ-K-XL", "XL"));

        var result = ShopifyProductPublicationComposer.PlanOptions([.. black, .. brown]);

        Assert.Null(result.Error);
        var plan = result.Plan!;
        Assert.Equal(["Beden", "Renk"], plan.Options.Select(option => option.Name));
        Assert.Equal(["L", "XL"], plan.Options[0].Values);
        Assert.Equal(["Siyah", "Kahverengi"], plan.Options[1].Values);
        Assert.Equal([("Beden", "XL"), ("Renk", "Kahverengi")], plan.VariantValues[brown[1].VariantId]);
    }

    [Fact]
    public void ColourCardsWithOnlyAnOptionSignatureStillMergeIntoColourAndSizeOptions()
    {
        // Live 2026-10-28 MZ054YNC: imported cards had no option value rows, only "Renk: Bordo | Beden: L".
        var rows = new[]
        {
            ("MZ054YNC-04", 0, "Renk: Bordo | Beden: L", 0),
            ("MZ054YNC-05", 0, "Renk: Bordo | Beden: XL", 1),
            ("MZ054YNC-06", 0, "Renk: Bordo | Beden: 2XL", 2),
            ("MZ054YNC-01", 1, "Renk: Açık Kahverengi | Beden: L | Web Color: Kahverengi", 0),
            ("MZ054YNC-02", 1, "Renk: Açık Kahverengi | Beden: XL", 1)
        }.Select(row => new ShopifyVariantOptionRow(Guid.NewGuid(), row.Item1, row.Item2, ShopifyProductPublicationComposer.SignatureOptionValues(row.Item3, row.Item4))).ToArray();

        var result = ShopifyProductPublicationComposer.PlanOptions(rows);

        Assert.Null(result.Error);
        var plan = result.Plan!;
        Assert.Equal(["Renk", "Beden"], plan.Options.Select(option => option.Name));
        Assert.Equal(["Bordo", "Açık Kahverengi"], plan.Options[0].Values);
        Assert.Equal(["L", "XL", "2XL"], plan.Options[1].Values);
        Assert.Equal([("Renk", "Açık Kahverengi"), ("Beden", "L")], plan.VariantValues[rows[3].VariantId]);
    }

    [Fact]
    public void ACardWithoutTheFamilysColourOptionStopsThePublication()
    {
        var black = Card(0, "Siyah", ("MZ-S-L", "L"));
        var plain = new ShopifyVariantOptionRow(Guid.NewGuid(), "MZ-P-L", 1, [new("Beden", 0, "L", 0)]);

        var result = ShopifyProductPublicationComposer.PlanOptions([.. black, plain]);

        Assert.Equal("SHOPIFY_VARIANT_OPTIONS_REQUIRED", result.Error?.Code);
        Assert.Contains("MZ-P-L", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoCardsWithTheSameColourAndSizeCannotBothBeVariants()
    {
        var first = Card(0, "Siyah", ("MZ-1-L", "L"));
        var second = Card(1, "siyah", ("MZ-2-L", "l"));

        var result = ShopifyProductPublicationComposer.PlanOptions([.. first, .. second]);

        Assert.Equal("SHOPIFY_VARIANT_OPTIONS_DUPLICATE", result.Error?.Code);
    }

    [Fact]
    public void ASingleVariantWithoutOptionsKeepsShopifysDefaultTitle()
    {
        var variantId = Guid.NewGuid();

        var result = ShopifyProductPublicationComposer.PlanOptions([new(variantId, "TEK-1", 0, [])]);

        Assert.Equal([("Title", "Default Title")], result.Plan!.VariantValues[variantId]);
        Assert.Equal(["Default Title"], Assert.Single(result.Plan.Options).Values);
    }

    [Fact]
    public void MoreThanThreeOptionsAcrossTheFamilyAreRefused()
    {
        var row = new ShopifyVariantOptionRow(Guid.NewGuid(), "X-1", 0, [new("Renk", 0, "Siyah", 0), new("Beden", 1, "L", 0), new("Boy", 2, "Uzun", 0), new("Kol", 3, "Kısa", 0)]);

        Assert.Equal("SHOPIFY_OPTION_LIMIT_EXCEEDED", ShopifyProductPublicationComposer.PlanOptions([row]).Error?.Code);
    }

    [Fact]
    public void AModelFamilyHasOneHandleForAllItsCardsAndAProductWithoutModelCodeKeepsItsOwn()
    {
        var brandId = Guid.NewGuid();
        var black = Guid.NewGuid();
        var brown = Guid.NewGuid();

        var blackHandle = ShopifyProductPublicationComposer.ShopifyHandle(black, ProductFamilyKeyPolicy.FamilyKey(black, brandId, "MZ059YNC"));
        var brownHandle = ShopifyProductPublicationComposer.ShopifyHandle(brown, ProductFamilyKeyPolicy.FamilyKey(brown, brandId, "mz059ync"));
        var otherBrand = ShopifyProductPublicationComposer.ShopifyHandle(black, ProductFamilyKeyPolicy.FamilyKey(black, Guid.NewGuid(), "MZ059YNC"));
        var single = ShopifyProductPublicationComposer.ShopifyHandle(black, ProductFamilyKeyPolicy.FamilyKey(black, brandId, null));

        Assert.Equal(blackHandle, brownHandle);
        Assert.StartsWith("ravencia-model-", blackHandle, StringComparison.Ordinal);
        Assert.NotEqual(blackHandle, otherBrand);
        Assert.Equal($"ravencia-{black:N}", single);
    }

    [Fact]
    public void AnExistingProductWithTheSamePayloadIsNotWrittenAgain()
    {
        using var existing = JsonDocument.Parse("""
        { "id": "gid://shopify/Product/1", "status": "ACTIVE", "payloadHash": { "value": "ABC" },
          "variants": { "nodes": [ { "id": "gid://shopify/ProductVariant/1", "sku": "MZ-S-L" }, { "id": "gid://shopify/ProductVariant/2", "sku": "MZ-K-L" } ] } }
        """);

        var unchanged = ShopifyHttpClient.UnchangedPublicationLines(existing.RootElement, "abc", ["MZ-S-L", "MZ-K-L"]);

        Assert.NotNull(unchanged);
        Assert.Equal(["MZ-S-L", "MZ-K-L"], unchanged!.Select(line => line.ExternalKey));
        Assert.All(unchanged, line => Assert.True(line.Succeeded));
        Assert.Null(ShopifyHttpClient.UnchangedPublicationLines(existing.RootElement, "DEF", ["MZ-S-L", "MZ-K-L"]));
        Assert.Null(ShopifyHttpClient.UnchangedPublicationLines(existing.RootElement, "ABC", ["MZ-S-L", "MZ-K-L", "MZ-B-L"]));
        Assert.Null(ShopifyHttpClient.UnchangedPublicationLines(existing.RootElement, null, ["MZ-S-L", "MZ-K-L"]));
    }

    [Fact]
    public void ThePublicationNamesItsOwnerAndPayloadHashByMetafieldKey()
    {
        var input = JsonNode.Parse("""
        { "metafields": [
          { "namespace": "marketplacehub", "key": "ravencia_product_id", "value": "p-1" },
          { "namespace": "marketplacehub", "key": "ravencia_family_key", "value": "model:b:MZ" },
          { "namespace": "marketplacehub", "key": "payload_hash", "value": "H" } ] }
        """)!.AsObject();

        Assert.Equal("model:b:MZ", ShopifyHttpClient.RequestedMetafield(input, "ravencia_family_key"));
        Assert.Equal("H", ShopifyHttpClient.RequestedMetafield(input, "payload_hash"));
        Assert.Null(ShopifyHttpClient.RequestedMetafield(input, "missing"));
    }

    [Fact]
    public void AQueuedFamilyPublicationIsStaleOnlyWhenItsVariantListOrHandleChanged()
    {
        const string queued = """{"handle":"ravencia-model-a","variants":[{"sku":"MZ-S-L","price":"100.00"},{"sku":"MZ-K-L","price":"100.00"}]}""";

        Assert.True(ShopifyProductPublicationComposer.SameVariantSkus(queued, """{"handle":"ravencia-model-a","variants":[{"sku":"mz-k-l","price":"90.00"},{"sku":"MZ-S-L","price":"95.00"}]}"""));
        Assert.False(ShopifyProductPublicationComposer.SameVariantSkus(queued, """{"handle":"ravencia-model-a","variants":[{"sku":"MZ-S-L"},{"sku":"MZ-K-L"},{"sku":"MZ-B-L"}]}"""));
        Assert.False(ShopifyProductPublicationComposer.SameVariantSkus(queued, """{"handle":"ravencia-model-b","variants":[{"sku":"MZ-S-L"},{"sku":"MZ-K-L"}]}"""));
        Assert.False(ShopifyProductPublicationComposer.SameVariantSkus(queued, "not json"));
    }

    [Fact]
    public void ASkuReadsAsMissingLiveOrPendingOnlyByAnExactMatch()
    {
        using var none = JsonDocument.Parse("""{ "nodes": [ { "id": "v-9", "sku": "MZ-S-L2", "product": { "id": "p-9", "status": "ACTIVE" } } ] }""");
        using var live = JsonDocument.Parse("""{ "nodes": [ { "id": "v-1", "sku": "mz-s-l", "product": { "id": "p-1", "status": "ACTIVE" } } ] }""");
        using var draft = JsonDocument.Parse("""{ "nodes": [ { "id": "v-2", "sku": "MZ-S-L", "product": { "id": "p-2", "status": "DRAFT" } } ] }""");

        Assert.Equal("NOT_FOUND", ShopifyHttpClient.MapSkuPublicationStatus("MZ-S-L", none.RootElement).Status);
        var approved = ShopifyHttpClient.MapSkuPublicationStatus("MZ-S-L", live.RootElement);
        Assert.Equal(("APPROVED", "p-1", "v-1"), (approved.Status, approved.ExternalProductId, approved.ExternalVariantId));
        Assert.Equal("PENDING", ShopifyHttpClient.MapSkuPublicationStatus("MZ-S-L", draft.RootElement).Status);
    }

    [Fact]
    public void AnUncertainCreateKeepsTheMarketplacesOwnCodeAndMessage()
    {
        var summary = MarketplaceJobProcessor.AmbiguousCreateSummary(new(AdapterErrorClass.ContractViolation, "SHOPIFY_PRODUCT_CREATED_BUT_PUBLISH_FAILED", "Online Store kanalına yayınlayamadı: erişim yok", 502, null, null));

        Assert.Contains("SHOPIFY_PRODUCT_CREATED_BUT_PUBLISH_FAILED", summary, StringComparison.Ordinal);
        Assert.Contains("erişim yok", summary, StringComparison.Ordinal);
    }

    private static ShopifyVariantOptionRow[] Card(int memberIndex, string colour, params (string Sku, string Size)[] sizes) =>
        sizes.Select((size, index) => new ShopifyVariantOptionRow(Guid.NewGuid(), size.Sku, memberIndex, [new("Beden", 0, size.Size, index), new("Renk", 1, colour, 0)])).ToArray();
}
