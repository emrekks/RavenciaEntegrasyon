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
        using var none = JsonDocument.Parse("""{ "nodes": [ { "id": "v-9", "sku": "MZ-S-L2", "product": { "id": "p-9", "status": "ACTIVE", "publishedAt": "2026-10-28T18:00:00Z" } } ] }""");
        using var live = JsonDocument.Parse("""{ "nodes": [ { "id": "v-1", "sku": "mz-s-l", "product": { "id": "p-1", "status": "ACTIVE", "publishedAt": "2026-10-28T18:00:00Z" } } ] }""");
        using var draft = JsonDocument.Parse("""{ "nodes": [ { "id": "v-2", "sku": "MZ-S-L", "product": { "id": "p-2", "status": "DRAFT", "publishedAt": null } } ] }""");

        Assert.Equal("NOT_FOUND", ShopifyHttpClient.MapSkuPublicationStatus("MZ-S-L", none.RootElement).Status);
        var approved = ShopifyHttpClient.MapSkuPublicationStatus("MZ-S-L", live.RootElement);
        Assert.Equal(("APPROVED", "p-1", "v-1"), (approved.Status, approved.ExternalProductId, approved.ExternalVariantId));
        Assert.Equal("PENDING", ShopifyHttpClient.MapSkuPublicationStatus("MZ-S-L", draft.RootElement).Status);
    }

    // Live 2026-10-28: every Shopify product written before the publish fix was active but not on the Online Store, and
    // its cards read LIVE, so Yayınla skipped them. The status check finds such a product and publishes it.
    [Fact]
    public void AnActiveProductTheOnlineStoreDoesNotShowIsFoundForPublishing()
    {
        using var unpublished = JsonDocument.Parse("""{ "nodes": [ { "id": "v-1", "sku": "mz-s-l", "product": { "id": "gid://shopify/Product/9", "handle": "ravencia-model-ce7f", "status": "ACTIVE", "publishedOnPublication": false } } ] }""");
        using var published = JsonDocument.Parse("""{ "nodes": [ { "id": "v-1", "sku": "MZ-S-L", "product": { "id": "gid://shopify/Product/9", "status": "ACTIVE", "publishedOnPublication": true } } ] }""");
        using var draft = JsonDocument.Parse("""{ "nodes": [ { "id": "v-1", "sku": "MZ-S-L", "product": { "id": "gid://shopify/Product/9", "status": "DRAFT", "publishedOnPublication": false } } ] }""");
        using var missing = JsonDocument.Parse("""{ "nodes": [ { "id": "v-1", "sku": "MZ-S-L2", "product": { "id": "gid://shopify/Product/9", "status": "ACTIVE", "publishedOnPublication": false } } ] }""");
        using var unknown = JsonDocument.Parse("""{ "nodes": [ { "id": "v-1", "sku": "MZ-S-L", "product": { "id": "gid://shopify/Product/9", "status": "ACTIVE" } } ] }""");

        Assert.Equal(("gid://shopify/Product/9", "ravencia-model-ce7f"), ShopifyHttpClient.OnlineStoreUnpublishedProduct("MZ-S-L", unpublished.RootElement));
        Assert.Equal((null, null), ShopifyHttpClient.OnlineStoreUnpublishedProduct("MZ-S-L", published.RootElement));
        // A draft is not published by a status check, and nothing is ever created for a SKU Shopify does not hold.
        Assert.Equal((null, null), ShopifyHttpClient.OnlineStoreUnpublishedProduct("MZ-S-L", draft.RootElement));
        Assert.Equal((null, null), ShopifyHttpClient.OnlineStoreUnpublishedProduct("MZ-S-L", missing.RootElement));
        // A missing answer is broken, never "unpublished".
        Assert.ThrowsAny<Exception>(() => ShopifyHttpClient.OnlineStoreUnpublishedProduct("MZ-S-L", unknown.RootElement));
        Assert.Contains("publishedOnPublication(publicationId:$publicationId)", ShopifyHttpClient.SkuStatusQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void ASkuHeldBySeveralProductsNamesEachOfThem()
    {
        using var twice = JsonDocument.Parse("""
        { "nodes": [
          { "id": "v-1", "sku": "MZ-S-L", "product": { "id": "gid://shopify/Product/1", "handle": "ravencia-01a178cc", "status": "ACTIVE", "publishedAt": null } },
          { "id": "v-2", "sku": "mz-s-l", "product": { "id": "gid://shopify/Product/2", "handle": "ravencia-model-abc", "status": "ACTIVE", "publishedAt": null } },
          { "id": "v-3", "sku": "MZ-S-L2", "product": { "id": "gid://shopify/Product/3", "handle": "other", "status": "ACTIVE", "publishedAt": null } } ] }
        """);

        var holders = ShopifyHttpClient.SkuProducts("MZ-S-L", twice.RootElement);

        Assert.Equal(["gid://shopify/Product/1", "gid://shopify/Product/2"], holders.Select(holder => holder.Id));
        Assert.Equal(["ravencia-01a178cc", "ravencia-model-abc"], holders.Select(holder => holder.Handle));
    }

    // publishable is the Publishable interface, which has no id: publishable{id} failed GraphQL validation, so no
    // Shopify publication ever reached the Online Store (live 2026-10-27/28, 24 jobs).
    [Fact]
    public void ThePublicationReadsTheProductIdThroughTheProductType()
    {
        Assert.Contains("publishable{... on Product{id}}", ShopifyHttpClient.PublishMutation, StringComparison.Ordinal);
        Assert.DoesNotContain("publishable{id}", ShopifyHttpClient.PublishMutation, StringComparison.Ordinal);
    }

    [Fact]
    public void APublishThatFailsAfterTheProductWasWrittenIsNotAmbiguous()
    {
        const string product = "gid://shopify/Product/77";
        using var published = JsonDocument.Parse("""{ "publishablePublish": { "publishable": { "id": "gid://shopify/Product/77" }, "userErrors": [] } }""");
        using var refused = JsonDocument.Parse("""{ "publishablePublish": { "publishable": null, "userErrors": [ { "field": ["input"], "message": "Publication does not exist" } ] } }""");
        using var otherResource = JsonDocument.Parse("""{ "publishablePublish": { "publishable": { "id": "gid://shopify/Product/78" }, "userErrors": [] } }""");

        Assert.Null(ShopifyHttpClient.PublishFailure(AdapterResult<JsonDocument>.Success(published), product, "ravencia-model-x"));

        var rejected = ShopifyHttpClient.PublishFailure(AdapterResult<JsonDocument>.Success(refused), product, "ravencia-model-x")!;
        Assert.Equal((AdapterErrorClass.Validation, ShopifyHttpClient.PublishFailedCode), (rejected.Class, rejected.Code));
        Assert.Contains(product, rejected.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("ravencia-model-x", rejected.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("Publication does not exist", rejected.SafeMessage, StringComparison.Ordinal);

        var denied = ShopifyHttpClient.PublishFailure(AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.Validation, "SHOPIFY_GRAPHQL_ERROR", "Field 'id' doesn't exist on type 'Publishable'", 200, null, "req-1")), product, "ravencia-model-x")!;
        Assert.Equal((AdapterErrorClass.Validation, ShopifyHttpClient.PublishFailedCode, "req-1"), (denied.Class, denied.Code, denied.RemoteRequestId));
        Assert.Contains("Field 'id' doesn't exist", denied.SafeMessage, StringComparison.Ordinal);

        var timeout = ShopifyHttpClient.PublishFailure(AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "SHOPIFY_TIMEOUT", "zaman aşımı", null, null, null)), product, "ravencia-model-x")!;
        Assert.Equal((AdapterErrorClass.TransientNetwork, ShopifyHttpClient.PublishRetryCode), (timeout.Class, timeout.Code));
        Assert.Equal(ShopifyHttpClient.PublishRetryCode, ShopifyHttpClient.PublishFailure(AdapterResult<JsonDocument>.Success(otherResource), product, "ravencia-model-x")!.Code);

        Assert.True(ShopifyFamilySend.IsPublishOnlyFailure(rejected.Code));
        Assert.True(ShopifyFamilySend.IsPublishOnlyFailure(timeout.Code));
        Assert.False(ShopifyFamilySend.IsPublishOnlyFailure("SHOPIFY_PRODUCT_CREATE_CONTRACT_INVALID"));
    }

    [Fact]
    public void ACardWhoseFamilyASiblingAlreadySentFollowsThatSend()
    {
        var now = new DateTimeOffset(2026, 10, 28, 17, 52, 40, TimeSpan.Zero);
        var own = FamilyJob("SUBMIT", "HASH-A", null, null);
        var sibling = FamilyJob("POLL", "hash-a", "shopify-completed:abc", now.AddSeconds(-8));

        Assert.Same(sibling, ShopifyFamilySend.Covering(own, [sibling], now));
        // Another content, a send not answered yet, another kind of operation or one past the window is not followed.
        Assert.Null(ShopifyFamilySend.Covering(own, [FamilyJob("POLL", "HASH-B", "shopify-completed:abc", now.AddSeconds(-8))], now));
        Assert.Null(ShopifyFamilySend.Covering(own, [FamilyJob("SUBMIT", "HASH-A", null, null)], now));
        Assert.Null(ShopifyFamilySend.Covering(own, [FamilyJob("POLL", "HASH-A", "t:trk-1", now.AddSeconds(-8))], now));
        Assert.Null(ShopifyFamilySend.Covering(own, [FamilyJob("POLL", "HASH-A", "shopify-completed:abc", now.AddHours(-5))], now));
        // The latest send wins.
        var later = FamilyJob("POLL", "HASH-A", "shopify-completed:def", now.AddSeconds(-2));
        Assert.Same(later, ShopifyFamilySend.Covering(own, [sibling, later], now));
    }

    private static ProductPublicationJobPayload FamilyJob(string phase, string hash, string? operation, DateTimeOffset? submittedAt) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), phase, hash, """{"handle":"ravencia-model-a"}""", operation, submittedAt);

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
