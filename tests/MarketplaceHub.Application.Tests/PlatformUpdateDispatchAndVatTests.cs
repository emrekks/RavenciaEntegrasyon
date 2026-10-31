using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// "Platformlarda Güncelle": Hepsiburada's dispatch time (listing shipping-info upload) and Pazarama's VAT rate
// (product/vatRate/bulk).
public sealed class PlatformUpdateDispatchAndVatTests
{
    [Fact]
    public void HepsiburadaDispatchTime_CarriesOnlyTheSkusAndTheDays()
    {
        var payload = HepsiburadaProductUpdateComposer.DispatchTimePayload("M1", [("HBV1", "SKU-1"), ("HBV2", "SKU-2")], 3);
        Assert.NotNull(payload);
        Assert.True(HepsiburadaHttpClient.ValidShippingInfoPayload(payload!, "M1"));
        var body = JsonNode.Parse(HepsiburadaHttpClient.BuildShippingInfoUploadJson(payload!))!.AsArray();
        Assert.Equal(2, body.Count);
        var first = body[0]!.AsObject();
        Assert.Equal(["dispatchTime", "hepsiburadaSku", "merchantSku"], first.Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal("HBV1", first["hepsiburadaSku"]!.GetValue<string>());
        Assert.Equal("SKU-1", first["merchantSku"]!.GetValue<string>());
        Assert.Equal(3, first["dispatchTime"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void HepsiburadaDispatchTime_WithoutDays_SendsNothing(int? days) =>
        Assert.Null(HepsiburadaProductUpdateComposer.DispatchTimePayload("M1", [("HBV1", "SKU-1")], days));

    [Theory]
    [InlineData("""{"merchantId":"M2","items":[{"hepsiburadaSku":"HBV1","merchantSku":"S1","dispatchTime":2}]}""")]
    [InlineData("""{"merchantId":"M1","items":[]}""")]
    [InlineData("""{"merchantId":"M1","items":[{"hepsiburadaSku":"HBV1","merchantSku":"S1"}]}""")]
    [InlineData("""{"merchantId":"M1","items":[{"hepsiburadaSku":"HBV1","merchantSku":"S1","dispatchTime":null}]}""")]
    [InlineData("""{"merchantId":"M1","items":[{"hepsiburadaSku":"HBV1","merchantSku":"S1","dispatchTime":1.5}]}""")]
    [InlineData("""{"merchantId":"M1","items":[{"hepsiburadaSku":"HBV1","merchantSku":"S1","dispatchTime":2,"cargoCompany1":null}]}""")]
    [InlineData("""{"merchantId":"M1","items":[{"hepsiburadaSku":"HBV1","merchantSku":"S1","dispatchTime":2},{"hepsiburadaSku":"HBV2","merchantSku":"S1","dispatchTime":2}]}""")]
    public void HepsiburadaDispatchTime_RefusesAnyOtherShape(string payload) =>
        Assert.False(HepsiburadaHttpClient.ValidShippingInfoPayload(payload, "M1"));

    [Fact]
    public void HepsiburadaShippingInfoStatus_IsReadLikeAnInventoryUpload()
    {
        using var done = JsonDocument.Parse("""{"id":"U1","status":"Done","errors":[{"merchantSku":"SKU-2","errors":["InvalidDispatchTime"]}]}""");
        var status = HepsiburadaJsonMapper.InventoryUploadStatus(done.RootElement, "U1", HepsiburadaHttpClient.ShippingInfoUploadPrefix);
        Assert.Equal("LISTING_SHIPPING:U1", status.ExternalOperationId);
        Assert.Equal("COMPLETED", status.Status);
        var line = Assert.Single(status.Lines);
        Assert.Equal("SKU-2", line.ExternalKey);
        Assert.Equal("InvalidDispatchTime", line.ErrorCode);
        Assert.Equal("LISTING_INVENTORY:U1", HepsiburadaJsonMapper.InventoryUploadStatus(done.RootElement, "U1").ExternalOperationId);
    }

    [Fact]
    public void HepsiburadaUpdate_GoesOnToTheDispatchPhase_AndKeepsTheProductTrackingId()
    {
        var payload = new ProductUpdateJobPayload(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "POLL_UNAPPROVED", "UNAPPROVED", "H", "{\"items\":[1]}", "{\"items\":[]}", "{\"items\":[]}", "{\"items\":[1]}", "PRODUCT_UPDATE:T1", DateTimeOffset.UnixEpoch);
        var next = MarketplaceJobProcessor.AdvanceUpdate(payload, "POLL_UNAPPROVED")!;
        Assert.Equal("SUBMIT_DELIVERY", next.Phase);
        Assert.Null(next.ExternalOperationId);
        Assert.Equal("PRODUCT_UPDATE:T1", next.ProductOperationId);
        // The dispatch upload's own id does not replace it.
        var afterDispatch = next with { Phase = "POLL_DELIVERY", ExternalOperationId = "LISTING_SHIPPING:U1" };
        Assert.Null(MarketplaceJobProcessor.AdvanceUpdate(afterDispatch, "POLL_DELIVERY"));
        Assert.Equal("PRODUCT_UPDATE:T1", afterDispatch.ProductOperationId);
    }

    [Fact]
    public void ProductUpdatePayload_FromBeforeTheTrackingIdField_StillReads()
    {
        const string old = """{"JobId":"0192c0de-0000-7000-8000-000000000001","ProductId":"0192c0de-0000-7000-8000-000000000002","ProfileId":"0192c0de-0000-7000-8000-000000000003","Phase":"SUBMIT_CONTENT","Mode":"APPROVED","PayloadHash":"H","UnapprovedPayloadJson":"{}","ApprovedContentPayloadJson":"{}","ApprovedVariantPayloadJson":"{}","ApprovedDeliveryPayloadJson":"{}","ExternalOperationId":null,"SubmittedAt":null}""";
        var payload = JsonSerializer.Deserialize<ProductUpdateJobPayload>(old)!;
        Assert.Equal("SUBMIT_CONTENT", payload.Phase);
        Assert.Null(payload.ProductOperationId);
    }

    [Theory]
    [InlineData(new[] { "LIVE", "LIVE" }, "LIVE")]
    [InlineData(new[] { "LIVE", "UPDATE_REJECTED" }, "UPDATE_PARTIAL_FAILURE")]
    [InlineData(new[] { "UPDATE_REJECTED" }, "UPDATE_REJECTED")]
    [InlineData(new[] { "LIVE", "APPROVAL_PENDING" }, "PARTIAL_LIVE")]
    [InlineData(new[] { "APPROVAL_PENDING", "APPROVAL_PENDING" }, "APPROVAL_PENDING")]
    public void InPlaceUpdate_LeavesTheCardAsItsListingsAre(string[] listings, string expected) =>
        Assert.Equal(expected, MarketplaceJobProcessor.ProductProfileStatusAfterInPlaceUpdate(listings));

    [Fact]
    public void PazaramaVatRate_BodyIsTheDocumentedListingVatRates()
    {
        var payload = PazaramaVatRateRules.Payload([("8680000000001", 10), ("8680000000002", 20)]);
        var rates = PazaramaVatRateRules.Read(payload);
        Assert.NotNull(rates);
        var body = JsonNode.Parse(JsonSerializer.Serialize(PazaramaVatRateRules.Body(rates!)))!.AsObject();
        var list = body["listingVatRates"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Equal("8680000000001", list[0]!["productCode"]!.GetValue<string>());
        Assert.Equal(10, list[0]!["vatRate"]!.GetValue<int>());
        Assert.Single(body);
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"items":[{"productCode":"B1","vatRate":18}]}""")]
    [InlineData("""{"items":[{"productCode":"B1","vatRate":null}]}""")]
    [InlineData("""{"items":[{"productCode":" ","vatRate":10}]}""")]
    [InlineData("""{"items":[{"productCode":"B1","vatRate":10},{"productCode":"b1","vatRate":20}]}""")]
    public void PazaramaVatRate_RefusesWhatPazaramaCannotTake(string payload) =>
        Assert.Null(PazaramaVatRateRules.Read(payload));

    [Fact]
    public void PazaramaVatRate_OnlyAnAnswerOfTrue_CountsAsApplied()
    {
        IReadOnlyList<(string, int)> rates = [("B1", 10), ("B2", 20)];
        using var applied = JsonDocument.Parse("""{"data":true,"success":true,"messageCode":"MER_0"}""");
        Assert.All(PazaramaVatRateRules.Lines(applied.RootElement, rates), line => Assert.True(line.Succeeded));
        using var unclear = JsonDocument.Parse("""{"data":false,"success":true}""");
        var lines = PazaramaVatRateRules.Lines(unclear.RootElement, rates);
        Assert.All(lines, line => Assert.Equal("PAZARAMA_VAT_RATE_UNCONFIRMED", line.ErrorCode));
        Assert.Equal(["B1", "B2"], lines.Select(line => line.ExternalKey));
        // The answer travels in a completed operation id the product update job reads back.
        Assert.True(PazaramaOperationId.TryCompletedLines(PazaramaOperationId.Completed(lines), out var decoded));
        Assert.Equal(2, decoded.Count);
    }
}
