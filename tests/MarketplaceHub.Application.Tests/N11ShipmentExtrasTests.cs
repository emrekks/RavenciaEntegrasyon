using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11ShipmentExtrasTests
{
    [Fact]
    public void QuantitySplitKeepsSplitPackagesAndCancelledItems()
    {
        var body = N11PackageActionMapper.Body("SPLIT_BY_QUANTITY", """
            {"cancelledItems":[{"cancelReasonId":61,"orderLineId":389132250,"quantity":1}],
             "splitPackages":[{"packageDetails":[{"orderLineId":389132250,"quantities":2}]},{"packageDetails":[{"orderLineId":389132251,"quantities":1}]}]}
            """);

        using var json = JsonDocument.Parse(body!);
        var cancelled = json.RootElement.GetProperty("cancelledItems")[0];
        Assert.Equal(61, cancelled.GetProperty("cancelReasonId").GetInt32());
        Assert.Equal(389132250, cancelled.GetProperty("orderLineId").GetInt64());
        Assert.Equal(1, cancelled.GetProperty("quantity").GetInt64());
        var packages = json.RootElement.GetProperty("splitPackages");
        Assert.Equal(2, packages.GetArrayLength());
        Assert.Equal(2, packages[0].GetProperty("packageDetails")[0].GetProperty("quantities").GetInt64());
    }

    [Fact]
    public void QuantitySplitWithoutCancelledItemsOmitsThem()
    {
        var body = N11PackageActionMapper.Body("SPLIT_BY_QUANTITY", """{"splitPackages":[{"packageDetails":[{"orderLineId":448627086,"quantities":1}]}]}""");

        using var json = JsonDocument.Parse(body!);
        Assert.False(json.RootElement.TryGetProperty("cancelledItems", out _));
    }

    [Theory]
    [InlineData("""{"cancelledItems":[{"cancelReasonId":61,"orderLineId":1,"quantity":1}]}""")]
    [InlineData("""{"splitPackages":[]}""")]
    [InlineData("""{"splitPackages":[{"packageDetails":[]}]}""")]
    [InlineData("""{"splitPackages":[{"packageDetails":[{"orderLineId":1,"quantities":0}]}]}""")]
    [InlineData("""{"cancelledItems":[{"cancelReasonId":99,"orderLineId":1,"quantity":1}],"splitPackages":[{"packageDetails":[{"orderLineId":1,"quantities":1}]}]}""")]
    [InlineData("""{"cancelledItems":[{"cancelReasonId":61,"orderLineId":1,"quantity":0}],"splitPackages":[{"packageDetails":[{"orderLineId":1,"quantities":1}]}]}""")]
    public void QuantitySplitRefusesCancelWithoutSplitAndBadValues(string payload) =>
        Assert.Throws<JsonException>(() => N11PackageActionMapper.Body("SPLIT_BY_QUANTITY", payload));

    [Fact]
    public void LaborCostIsRoundedToTwoDecimalsAndKeepsTheVatRate()
    {
        var body = N11PackageActionMapper.Body("LABOR_COST", """{"laborCostDetails":[{"orderLineId":434713839,"totalLaborCostExcludingVAT":352.174,"laborVatRate":10},{"orderLineId":434708601,"totalLaborCostExcludingVAT":200}]}""");

        using var json = JsonDocument.Parse(body!);
        var rows = json.RootElement.GetProperty("laborCostDetails");
        Assert.Equal(352.17m, rows[0].GetProperty("totalLaborCostExcludingVAT").GetDecimal());
        Assert.Equal(10, rows[0].GetProperty("laborVatRate").GetInt32());
        Assert.False(rows[1].TryGetProperty("laborVatRate", out _));
    }

    [Theory]
    [InlineData("""{"laborCostDetails":[]}""")]
    [InlineData("""{"laborCostDetails":[{"orderLineId":1,"totalLaborCostExcludingVAT":0}]}""")]
    [InlineData("""{"laborCostDetails":[{"orderLineId":1,"totalLaborCostExcludingVAT":50,"laborVatRate":18}]}""")]
    [InlineData("""{"laborCostDetails":[{"totalLaborCostExcludingVAT":50}]}""")]
    public void LaborCostRefusesBadValues(string payload) =>
        Assert.Throws<JsonException>(() => N11PackageActionMapper.Body("LABOR_COST", payload));

    [Fact]
    public void CollectionRequestUppercasesTheCarrierAndKeepsEveryLine()
    {
        var body = N11PackageActionMapper.Body("COLLECTION_REQUEST", """
            {"collectionRequestDetails":[{"id":"112745617962209","orderLineId":471150189,"boxQuantity":1,"desi":100,"shipmentCompany":"hlz"},
                                         {"id":"112745617962209","orderLineId":471150188,"boxQuantity":1,"desi":60,"shipmentCompany":"HLZ"}]}
            """);

        using var json = JsonDocument.Parse(body!);
        var rows = json.RootElement.GetProperty("collectionRequestDetails");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("HLZ", rows[0].GetProperty("shipmentCompany").GetString());
        Assert.Equal(100, rows[0].GetProperty("desi").GetDecimal());
    }

    [Theory]
    [InlineData("""{"collectionRequestDetails":[{"id":"1","orderLineId":1,"boxQuantity":1,"desi":10,"shipmentCompany":"MNG"}]}""")]
    [InlineData("""{"collectionRequestDetails":[{"id":"1","orderLineId":1,"boxQuantity":0,"desi":10,"shipmentCompany":"HLZ"}]}""")]
    [InlineData("""{"collectionRequestDetails":[{"id":"1","orderLineId":1,"boxQuantity":1,"shipmentCompany":"HLZ"}]}""")]
    [InlineData("""{"collectionRequestDetails":[]}""")]
    public void CollectionRequestRefusesUnsupportedCarriersAndMissingFields(string payload) =>
        Assert.Throws<JsonException>(() => N11PackageActionMapper.Body("COLLECTION_REQUEST", payload));

    [Theory]
    [InlineData("PICKING", "PUT", "rest/order/v1/update")]
    [InlineData("SPLIT", "POST", "rest/delivery/v1/splitCombinePackage")]
    [InlineData("MULTI_SPLIT", "POST", "rest/delivery/v1/splitCombinePackage")]
    [InlineData("SPLIT_BY_QUANTITY", "POST", "rest/delivery/v1/splitPackageByQuantity")]
    [InlineData("LABOR_COST", "PUT", "rest/order/v1/labor-costs")]
    [InlineData("COLLECTION_REQUEST", "PUT", "rest/delivery/v1/collectionRequest")]
    public void EachActionGoesToItsOwnN11Endpoint(string action, string method, string path)
    {
        var endpoint = N11PackageActionMapper.Endpoint(action);
        Assert.Equal(method, endpoint.Method.Method);
        Assert.Equal(path, endpoint.Path);
    }

    [Fact]
    public void LaborCostAnswersAreReadPerLine()
    {
        using var mixed = JsonDocument.Parse("""{"content":[{"lineId":1,"status":"SUCCESS","reasons":"İşçilik Ekleme Başarıyla Tamamlandı."},{"lineId":2,"status":"FAIL","reasons":"Kategori uygun değil."}]}""");

        Assert.Equal("2 satırı: Kategori uygun değil.", N11PackageActionMapper.RefusedLines(mixed.RootElement));
    }

    [Fact]
    public void EveryN11PackageActionIsOfferedOnceShipmentWriteIsProven()
    {
        Assert.Equal(["PICKING", "SPLIT", "MULTI_SPLIT", "SPLIT_BY_QUANTITY", "LABOR_COST", "COLLECTION_REQUEST"], MarketplaceJobProcessor.N11ShipmentActions);
    }

    [Fact]
    public void ATooFrequentQuestionListIsARateLimitNotAHardRejection()
    {
        const string body = """
            <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/"><SOAP-ENV:Body>
              <ns3:GetProductQuestionListResponse xmlns:ns3="http://www.n11.com/ws/schemas">
                <result><status>failure</status><errorCode>SELLER_API.9</errorCode><errorMessage>Ürün soruları 1 dakikada bir kez listelenebilmektedir.</errorMessage></result>
              </ns3:GetProductQuestionListResponse>
            </SOAP-ENV:Body></SOAP-ENV:Envelope>
            """;

        var error = N11HttpClient.SoapResponse(body, "GetProductQuestionList", requireResult: false).Error;

        Assert.NotNull(error);
        Assert.Equal(AdapterErrorClass.RateLimit, error.Class);
        Assert.Equal("N11_RATE_LIMITED", error.Code);
        Assert.Equal(TimeSpan.FromMinutes(3), error.RetryAfter);
    }

    [Fact]
    public void OtherSoapRejectionsStayHardRejections()
    {
        const string body = """
            <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/"><SOAP-ENV:Body>
              <ns3:GetProductQuestionListResponse xmlns:ns3="http://www.n11.com/ws/schemas">
                <result><status>failure</status><errorCode>SELLER_API.1</errorCode><errorMessage>Geçersiz tarih biçimi.</errorMessage></result>
              </ns3:GetProductQuestionListResponse>
            </SOAP-ENV:Body></SOAP-ENV:Envelope>
            """;

        var error = N11HttpClient.SoapResponse(body, "GetProductQuestionList", requireResult: false).Error;

        Assert.Equal("N11_REQUEST_REJECTED", error!.Code);
    }

    [Fact]
    public void TinyQuotasKeepAQuarterMoreSpacingThanTheQuota()
    {
        // 1 call/min is spaced 75 s apart; larger quotas stay sliding windows.
        Assert.Equal(1d / 75, N11RateLimitHandler.BucketShape(1).TokensPerSecond, 6);
        Assert.Equal(1d, N11RateLimitHandler.BucketShape(1).Capacity);
        Assert.Equal(480d / 60, N11RateLimitHandler.BucketShape(600).TokensPerSecond, 6);
    }
}
