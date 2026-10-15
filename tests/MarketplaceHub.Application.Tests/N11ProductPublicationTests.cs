using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11ProductPublicationTests
{
    private static N11SkuDraft Sku(string stockCode, string? barcode = "8690000000001", decimal salePrice = 499m, decimal listPrice = 599.5m) =>
        new("Erkek Siyah Spor Ayakkabı", "<p>Açıklama</p>", 1002123, "BS7429", 3, "Kargo Şablonu 1", stockCode, barcode, 12,
            ["https://cdn.example.com/a.jpg", "https://cdn.example.com/b.jpg"],
            [new(1, null, "Marka"), new(2001, 98765, null)], salePrice, listPrice, 20);

    [Fact]
    public void PayloadFollowsTheN11ProductCreateShape()
    {
        using var json = JsonDocument.Parse(N11ProductPublicationComposer.Payload("Ravencia", [Sku("BS7429-40")]));
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal("Ravencia", payload.GetProperty("integrator").GetString());
        var sku = payload.GetProperty("skus")[0];
        Assert.Equal("BS7429-40", sku.GetProperty("stockCode").GetString());
        Assert.Equal("BS7429", sku.GetProperty("productMainId").GetString());
        Assert.Equal("TL", sku.GetProperty("currencyType").GetString());
        Assert.Equal(1002123, sku.GetProperty("categoryId").GetInt64());
        Assert.Equal(3, sku.GetProperty("preparingDay").GetInt32());
        Assert.Equal("Kargo Şablonu 1", sku.GetProperty("shipmentTemplate").GetString());
        Assert.Equal(20, sku.GetProperty("vatRate").GetInt32());
        Assert.Equal(JsonValueKind.Null, sku.GetProperty("catalogId").ValueKind);
        Assert.Equal("8690000000001", sku.GetProperty("barcode").GetString());
        Assert.Equal(499.00m, sku.GetProperty("salePrice").GetDecimal());
        Assert.Equal(599.50m, sku.GetProperty("listPrice").GetDecimal());
        var images = sku.GetProperty("images");
        Assert.Equal(2, images.GetArrayLength());
        Assert.Equal(1, images[1].GetProperty("order").GetInt32());
    }

    [Fact]
    public void BrandIsFreeTextAndListAttributesCarryTheValueId()
    {
        using var json = JsonDocument.Parse(N11ProductPublicationComposer.Payload("Ravencia", [Sku("BS7429-40")]));
        var attributes = json.RootElement.GetProperty("payload").GetProperty("skus")[0].GetProperty("attributes");
        Assert.Equal(1, attributes[0].GetProperty("id").GetInt64());
        Assert.Equal("Marka", attributes[0].GetProperty("customValue").GetString());
        Assert.Equal(JsonValueKind.Null, attributes[0].GetProperty("valueId").ValueKind);
        Assert.Equal(98765, attributes[1].GetProperty("valueId").GetInt64());
        Assert.Equal(JsonValueKind.Null, attributes[1].GetProperty("customValue").ValueKind);
    }

    [Fact]
    public void MissingBarcodeIsLeftOutOfTheBody()
    {
        using var json = JsonDocument.Parse(N11ProductPublicationComposer.Payload("Ravencia", [Sku("BS7429-41", barcode: null)]));
        Assert.False(json.RootElement.GetProperty("payload").GetProperty("skus")[0].TryGetProperty("barcode", out _));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(20, 20)]
    public void SupportedVatRatesPass(int rate, int expected) =>
        Assert.Equal(expected, N11ProductPublicationComposer.VatRate(rate));

    [Theory]
    [InlineData(8)]
    [InlineData(18)]
    [InlineData(20.5)]
    public void OtherVatRatesAreRefused(double rate) =>
        Assert.Null(N11ProductPublicationComposer.VatRate((decimal)rate));

    [Fact]
    public void ValueChoicePrefersTheNameMatchAndRefusesAmbiguity()
    {
        Assert.Equal(11, N11ProductPublicationComposer.SelectValueId("Siyah", [("11", "Siyah")]));
        Assert.Equal(12, N11ProductPublicationComposer.SelectValueId("siyah", [("11", "Lacivert"), ("12", "Siyah")]));
        Assert.Equal(11, N11ProductPublicationComposer.SelectValueId("Koyu", [("11", "Siyah")]));
        Assert.Null(N11ProductPublicationComposer.SelectValueId("Koyu", [("11", "Siyah"), ("12", "Lacivert")]));
        Assert.Null(N11ProductPublicationComposer.SelectValueId("Siyah", [("abc", "Siyah")]));
    }

    [Fact]
    public void PublishSettingsReadTheConnectionSettings()
    {
        Assert.Equal(("Şablon A", 5), N11ProductPublicationComposer.PublishSettings("""{"N11ShipmentTemplate":" Şablon A ","N11PreparingDay":5}"""));
        Assert.Equal((null, 3), N11ProductPublicationComposer.PublishSettings("""{"ExternalWritesEnabled":true}"""));
        Assert.Equal((null, 3), N11ProductPublicationComposer.PublishSettings("""{"N11ShipmentTemplate":"  "}"""));
        Assert.Equal((null, 3), N11ProductPublicationComposer.PublishSettings("not json"));
    }

    [Fact]
    public void CreatePayloadAddsTheIntegratorAndLimitsTheSkuCount()
    {
        var single = N11HttpClient.CreatePayload("""{"payload":{"integrator":"x","skus":[{"stockCode":"A"}]}}""", "Ravencia");
        using var json = JsonDocument.Parse(single!);
        Assert.Equal("Ravencia", json.RootElement.GetProperty("payload").GetProperty("integrator").GetString());

        Assert.Null(N11HttpClient.CreatePayload("""{"payload":{"skus":[]}}""", "Ravencia"));
        Assert.Null(N11HttpClient.CreatePayload("""{"skus":[{"stockCode":"A"}]}""", "Ravencia"));
        Assert.Null(N11HttpClient.CreatePayload("not json", "Ravencia"));
        var tooMany = "{\"payload\":{\"skus\":[" + string.Join(",", Enumerable.Repeat("{\"stockCode\":\"A\"}", 1001)) + "]}}";
        Assert.Null(N11HttpClient.CreatePayload(tooMany, "Ravencia"));
    }

    [Theory]
    [InlineData("ACTIVE", "APPROVED", null)]
    [InlineData("InApproval", "PENDING_APPROVAL", null)]
    [InlineData("InCatalogApproval", "PENDING_APPROVAL", null)]
    [InlineData("CatalogRejected", "REJECTED", "N11_PRODUCT_CATALOG_REJECTED")]
    [InlineData("Prohibited", "REJECTED", "N11_PRODUCT_PROHIBITED")]
    [InlineData("Suspended", "ARCHIVED", null)]
    public void ProductStatusMapsToTheApprovalVocabulary(string n11Status, string expected, string? rejectionCode)
    {
        using var json = JsonDocument.Parse($$"""{"content":[{"stockCode":"BS7429-40","productMainId":"BS7429","status":"{{n11Status}}"}]}""");
        var status = N11ProductMapper.PublicationStatus(json.RootElement, "BS7429-40");
        Assert.Equal(expected, status.Status);
        Assert.Equal(rejectionCode, status.RejectionCode);
        Assert.Equal("main:BS7429", status.ExternalProductId);
    }

    [Fact]
    public void AnUnlistedStockCodeIsNotFoundYet()
    {
        using var json = JsonDocument.Parse("""{"content":[{"stockCode":"OTHER","status":"ACTIVE"}]}""");
        Assert.Equal("NOT_FOUND", N11ProductMapper.PublicationStatus(json.RootElement, "BS7429-40").Status);
    }

    [Fact]
    public void ProductCreateTaskAnswersAreTurnedIntoReferencesOrRefusals()
    {
        var now = DateTimeOffset.UtcNow;
        using var queued = JsonDocument.Parse("""{"id":3435513999,"type":"PRODUCT_CREATE","status":"IN_QUEUE"}""");
        using var rejected = JsonDocument.Parse("""{"id":1,"type":"PRODUCT_CREATE","status":"REJECT","reasons":["Kargo şablonu bulunamadı."]}""");

        var accepted = N11TaskMapper.Submitted(queued.RootElement, now, "ürün açma", "PRODUCT_CREATE");
        Assert.Equal("3435513999", accepted!.Reference!.ExternalOperationId);
        Assert.Equal("PRODUCT_CREATE", accepted.Reference.Kind);

        var refused = N11TaskMapper.Submitted(rejected.RootElement, now, "ürün açma", "PRODUCT_CREATE");
        Assert.Null(refused!.Reference);
        Assert.Equal("N11_TASK_REJECTED", refused.Error!.Code);
        Assert.Contains("Kargo şablonu bulunamadı", refused.Error.SafeMessage);
    }
}
