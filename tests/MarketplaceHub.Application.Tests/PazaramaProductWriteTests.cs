using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaProductWriteTests
{
    private const string Brand = "0a097abf-3c2e-42c9-fd40-08dbf9534e72";
    private const string Category = "9a0415a7-3048-4085-afb8-ada7b6871092";
    private const string AttributeId = "89d4e886-bc14-4961-b6c0-442fbf81ff7e";
    private const string ValueId = "5b7722d5-825a-4bb9-a103-e088b76e189f";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static PazaramaProductDraft Draft(string code = "B1", string group = "GRP1", int desi = 2, int stock = 5, int vat = 20, decimal list = 120m, decimal sale = 99.9m, string? image = "https://img.example.com/a.jpg", string? attributeValue = ValueId) =>
        new("Ürün adı", "Ürün adı", "Açıklama", Brand, Category, group, code, "SKU-" + code, desi, stock, vat, list, sale,
            image is null ? [] : [image], attributeValue is null ? [] : [new PazaramaAttributeDraft(AttributeId, attributeValue)]);

    private static string Body(params PazaramaProductDraft[] drafts) => PazaramaProductPublicationComposer.Payload(drafts);

    // ---- payload ----

    [Fact]
    public void Payload_uses_the_documented_field_names_and_try()
    {
        var product = JsonNode.Parse(Body(Draft()))!["products"]![0]!.AsObject();

        Assert.Equal("B1", (string?)product["code"]);
        Assert.Equal("GRP1", (string?)product["groupCode"]);
        Assert.Equal("SKU-B1", (string?)product["stockCode"]);
        Assert.Equal(Brand, (string?)product["brandId"]);
        Assert.Equal(Category, (string?)product["categoryId"]);
        Assert.Equal("TRY", (string?)product["currencyType"]);
        Assert.Equal(99.9m, (decimal?)product["salePrice"]);
        Assert.Equal("https://img.example.com/a.jpg", (string?)product["images"]![0]!["imageurl"]);
        Assert.Equal(AttributeId, (string?)product["attributes"]![0]!["attributeId"]);
        Assert.Equal(ValueId, (string?)product["attributes"]![0]!["attributeValueId"]);
        Assert.Empty(product["deliveries"]!.AsArray());
    }

    [Fact]
    public void A_built_payload_passes_the_adapter_rules_and_returns_the_barcodes()
    {
        var codes = PazaramaProductPayloadRules.Validate(Body(Draft("B1"), Draft("B2")), out var error);

        Assert.Null(error);
        Assert.Equal(["B1", "B2"], codes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"products":[]}""")]
    [InlineData("""{"products":["x"]}""")]
    public void Unusable_bodies_are_refused(string payload) => Assert.Null(PazaramaProductPayloadRules.Validate(payload, out _));

    [Fact]
    public void Barcodes_must_be_unique_in_one_request()
    {
        Assert.Null(PazaramaProductPayloadRules.Validate(Body(Draft("B1"), Draft("b1")), out var error));
        Assert.Contains("özgün", error);
    }

    [Fact]
    public void A_request_holds_at_most_five_hundred_products()
    {
        var many = Enumerable.Range(0, PazaramaProductPayloadRules.MaxProductsPerRequest + 1).Select(i => Draft($"B{i}")).ToArray();

        Assert.NotNull(PazaramaProductPayloadRules.Validate(Body(many.Take(PazaramaProductPayloadRules.MaxProductsPerRequest).ToArray()), out _));
        Assert.Null(PazaramaProductPayloadRules.Validate(Body(many), out _));
    }

    [Theory]
    [InlineData("desi")]
    [InlineData("price")]
    [InlineData("image")]
    [InlineData("stock")]
    [InlineData("brand")]
    [InlineData("name")]
    public void Each_required_field_is_checked(string broken)
    {
        var product = JsonNode.Parse(Body(Draft()))!.AsObject();
        var item = product["products"]![0]!.AsObject();
        switch (broken)
        {
            case "desi": item["desi"] = 0; break;
            case "price": item["listPrice"] = 10; item["salePrice"] = 20; break;
            case "image": item["images"] = new JsonArray(); break;
            case "stock": item["stockCount"] = -1; break;
            case "brand": item["brandId"] = "not-a-guid"; break;
            case "name": item["name"] = new string('a', 101); break;
        }

        Assert.Null(PazaramaProductPayloadRules.Validate(product.ToJsonString(), out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Attributes_must_be_guid_pairs()
    {
        var product = JsonNode.Parse(Body(Draft()))!.AsObject();
        product["products"]![0]!["attributes"]![0]!["attributeValueId"] = "red";

        Assert.Null(PazaramaProductPayloadRules.Validate(product.ToJsonString(), out _));
    }

    // ---- group code and value choice ----

    [Theory]
    [InlineData("MZ019CD52", "MZ019CD52")]
    [InlineData("  AB  ", "AB")]
    public void A_short_model_code_is_the_group_code(string model, string expected) => Assert.Equal(expected, PazaramaProductPublicationComposer.GroupCode(model));

    [Fact]
    public void A_long_model_code_becomes_a_stable_ten_character_code()
    {
        var code = PazaramaProductPublicationComposer.GroupCode("MZ019CD52B-LONG-MODEL-CODE");

        Assert.Equal(10, code.Length);
        Assert.StartsWith("RV", code);
        Assert.Equal(code, PazaramaProductPublicationComposer.GroupCode("MZ019CD52B-LONG-MODEL-CODE"));
        Assert.NotEqual(code, PazaramaProductPublicationComposer.GroupCode("MZ019CD52B-LONG-MODEL-CODF"));
    }

    [Fact]
    public void A_value_is_chosen_by_the_only_candidate_or_by_name()
    {
        var one = Guid.NewGuid().ToString();
        var two = Guid.NewGuid().ToString();

        Assert.Equal(one, PazaramaProductPublicationComposer.SelectValueId("Siyah", [(one, "Black")]));
        Assert.Equal(two, PazaramaProductPublicationComposer.SelectValueId("siyah", [(one, "Lacivert"), (two, "Siyah")]));
        Assert.Null(PazaramaProductPublicationComposer.SelectValueId("Kırmızı", [(one, "Siyah"), (two, "Beyaz")]));
        Assert.Null(PazaramaProductPublicationComposer.SelectValueId("Siyah", [("not-a-guid", "Siyah")]));
    }

    // ---- create answer ----

    [Fact]
    public void A_batch_answer_carries_the_batch_id()
    {
        var answer = PazaramaProductWriteMapper.Created(Parse("""{"data":{"batchRequestId":"8c3e7e0b-9239-4af1-a921-db94371a9db1","error":{"errors":[],"message":null}},"success":true}"""));

        Assert.Equal("8c3e7e0b-9239-4af1-a921-db94371a9db1", answer.BatchId);
        Assert.Null(answer.Error);
    }

    [Fact]
    public void A_single_product_answer_has_no_batch()
    {
        var answer = PazaramaProductWriteMapper.Created(Parse("""{"data":{"batchRequestId":"00000000-0000-0000-0000-000000000000","error":{"errors":[]}},"success":true}"""));

        Assert.Null(answer.BatchId);
        Assert.Null(answer.Error);
    }

    [Fact]
    public void Errors_in_the_answer_are_reported()
    {
        var answer = PazaramaProductWriteMapper.Created(Parse("""{"data":{"batchRequestId":"00000000-0000-0000-0000-000000000000","error":{"errors":["Kategori bulunamadı","Marka geçersiz"],"message":"Hata"}}}"""));

        Assert.Contains("Kategori bulunamadı", answer.Error);
        Assert.Contains("Marka geçersiz", answer.Error);
    }

    // ---- batch result ----

    private static string Batch(int status, string failed = "", string results = "", int total = 2) =>
        $$"""{"data":{"status":{{status}},"batchRequestId":"b","batchResult":[{{results}}],"totalCount":{{total}},"failedProducts":[{{failed}}]},"success":true}""";

    [Fact]
    public void A_running_batch_is_in_progress() =>
        Assert.Equal("IN_PROGRESS", PazaramaProductWriteMapper.BatchStatus(Parse(Batch(1)), "b:x:2", ["B1", "B2"]).Status);

    [Fact]
    public void A_done_batch_reports_failures_and_the_remembered_rest_as_accepted()
    {
        var status = PazaramaProductWriteMapper.BatchStatus(Parse(Batch(2, """{"productName":"x","productCode":"B2","errorReason":"Özellik eksik"}""")), "b:x:2", ["B1", "B2"]);

        Assert.Equal("COMPLETED", status.Status);
        var b1 = status.Lines.Single(x => x.ExternalKey == "B1");
        var b2 = status.Lines.Single(x => x.ExternalKey == "B2");
        Assert.True(b1.Succeeded);
        Assert.False(b2.Succeeded);
        Assert.Equal("PAZARAMA_PRODUCT_REJECTED", b2.ErrorCode);
        Assert.Contains("Özellik eksik", b2.Status);
    }

    [Fact]
    public void Several_reasons_for_one_barcode_are_joined()
    {
        var failed = """{"productCode":"B1","errorReason":"A"},{"productCode":"B1","errorReason":"B"}""";

        var status = PazaramaProductWriteMapper.BatchStatus(Parse(Batch(2, failed, total: 1)), "b:x:1", ["B1"]);

        Assert.Equal("A B", status.Lines.Single().Status);
    }

    [Fact]
    public void Listed_successes_are_used_when_pazarama_gives_them()
    {
        var status = PazaramaProductWriteMapper.BatchStatus(Parse(Batch(2, results: """{"productCode":"B1"},{"code":"B2"}""")), "b:x:2", null);

        Assert.Equal(["B1", "B2"], status.Lines.Select(x => x.ExternalKey).OrderBy(x => x));
        Assert.All(status.Lines, line => Assert.True(line.Succeeded));
    }

    [Fact]
    public void An_error_batch_without_lines_applied_nothing() =>
        Assert.Equal("REJECTED", PazaramaProductWriteMapper.BatchStatus(Parse(Batch(3)), "b:x:2", null).Status);

    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"data":{"status":9}}""")]
    public void Unknown_shapes_are_contract_errors(string json) =>
        Assert.Throws<JsonException>(() => PazaramaProductWriteMapper.BatchStatus(Parse(json), "b:x:1", null));

    // ---- publication state ----

    [Fact]
    public void An_approved_open_product_is_approved_and_keyed_like_the_import()
    {
        var item = Parse("""{"code":"B1","groupCode":"GRP1","productStatus":1,"stockCode":"S1"}""");

        var status = PazaramaProductWriteMapper.Publication(item, "B1", unapproved: false);

        Assert.Equal("APPROVED", status.Status);
        Assert.Equal("group:GRP1", status.ExternalProductId);
        Assert.Equal("B1", status.ExternalVariantId);
    }

    [Fact]
    public void A_product_closed_for_sale_is_archived() =>
        Assert.Equal("ARCHIVED", PazaramaProductWriteMapper.Publication(Parse("""{"code":"B1","productStatus":10}"""), "B1", false).Status);

    [Fact]
    public void A_product_without_group_code_is_keyed_by_barcode() =>
        Assert.Equal("code:B1", PazaramaProductWriteMapper.Publication(Parse("""{"code":"B1"}"""), "B1", false).ExternalProductId);

    [Theory]
    [InlineData("""{"code":"B1","status":null,"waitingApproveExp":null}""", "PENDING_APPROVAL", null)]
    [InlineData("""{"code":"B1","status":"Onay bekliyor","waitingApproveExp":"İnceleniyor"}""", "PENDING_APPROVAL", null)]
    [InlineData("""{"code":"B1","status":"Reddedildi","waitingApproveExp":"Görsel uygun değil"}""", "REJECTED", "PAZARAMA_PRODUCT_REJECTED")]
    public void Unapproved_products_wait_or_were_turned_down(string json, string status, string? code)
    {
        var result = PazaramaProductWriteMapper.Publication(Parse(json), "B1", unapproved: true);

        Assert.Equal(status, result.Status);
        Assert.Equal(code, result.RejectionCode);
    }

    // ---- sale status ----

    [Fact]
    public void Sale_changes_name_each_barcode_once()
    {
        var changes = PazaramaProductWriteMapper.SaleChanges("""{"items":[{"barcode":"B1","archived":true},{"barcode":" B2 ","archived":false}]}""");

        Assert.Equal([("B1", true), ("B2", false)], changes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"items":[{"barcode":"","archived":true}]}""")]
    [InlineData("""{"items":[{"barcode":"B1"}]}""")]
    [InlineData("""{"items":[{"barcode":"B1","archived":true},{"barcode":"b1","archived":false}]}""")]
    public void Unusable_sale_changes_are_refused(string payload) => Assert.Null(PazaramaProductWriteMapper.SaleChanges(payload));

    [Fact]
    public void Sale_changes_are_limited_to_one_hundred_barcodes()
    {
        var items = string.Join(',', Enumerable.Range(0, 101).Select(i => $$"""{"barcode":"B{{i}}","archived":true}"""));

        Assert.Null(PazaramaProductWriteMapper.SaleChanges($$"""{"items":[{{items}}]}"""));
    }

    [Fact]
    public void The_answer_of_a_sale_change_is_read_per_barcode()
    {
        var ok = PazaramaProductWriteMapper.SaleResult(Parse("""{"data":{"result":[{"success":true,"message":null,"code":"B1","productStatus":10}]},"success":true}"""), "B1");
        var failed = PazaramaProductWriteMapper.SaleResult(Parse("""{"data":{"result":[{"success":false,"message":"Ürün bulunamadı","code":"B2","productStatus":10}]}}"""), "B2");
        var missing = PazaramaProductWriteMapper.SaleResult(Parse("""{"data":{"result":[]}}"""), "B3");

        Assert.True(ok.Succeeded);
        Assert.False(failed.Succeeded);
        Assert.Equal("Ürün bulunamadı", failed.Status);
        Assert.Equal("PAZARAMA_SALE_STATUS_REJECTED", failed.ErrorCode);
        Assert.False(missing.Succeeded);
        Assert.Equal("PAZARAMA_SALE_STATUS_UNCONFIRMED", missing.ErrorCode);
    }

    [Fact]
    public void The_status_numbers_are_the_documented_ones()
    {
        Assert.Equal(1, PazaramaProductWriteMapper.OpenStatus);
        Assert.Equal(10, PazaramaProductWriteMapper.ClosedStatus);
        Assert.Equal("product/external-status", PazaramaHttpClient.SaleStatus);
        Assert.Equal("product/create", PazaramaHttpClient.ProductCreate);
        Assert.Equal("product/getProductBatchResult", PazaramaHttpClient.ProductBatchResult);
    }
}
