using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// n11 product update ("Platformlarda Güncelle"): the product-update task carries only the picked fields n11 can change.
public sealed class N11ProductUpdateTests
{
    private static readonly IReadOnlyList<N11SkuAttribute> Attributes = [new(1, null, "Marka"), new(2001, 98765, null), new(1000004, null, "Türkiye")];

    [Fact]
    public void AllPicks_SendEveryFieldN11Takes_AndNothingElse()
    {
        using var json = JsonDocument.Parse(N11ProductUpdateComposer.Payload("Ravencia", [new("BS7429-40", "<p>Açıklama</p>", 20, 3, "Kargo Şablonu 1", Attributes)]));
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal("Ravencia", payload.GetProperty("integrator").GetString());
        var sku = Assert.Single(payload.GetProperty("skus").EnumerateArray());
        Assert.Equal(["stockCode", "description", "vatRate", "preparingDay", "shipmentTemplate", "attributes"], sku.EnumerateObject().Select(property => property.Name));
        Assert.Equal("BS7429-40", sku.GetProperty("stockCode").GetString());
        Assert.Equal("<p>Açıklama</p>", sku.GetProperty("description").GetString());
        Assert.Equal(20, sku.GetProperty("vatRate").GetInt32());
        Assert.Equal(3, sku.GetProperty("preparingDay").GetInt32());
        Assert.Equal("Kargo Şablonu 1", sku.GetProperty("shipmentTemplate").GetString());
        var attributes = sku.GetProperty("attributes");
        Assert.Equal(3, attributes.GetArrayLength());
        // Every attribute carries id, valueId and customValue; the one that does not apply is null.
        Assert.Equal(1, attributes[0].GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, attributes[0].GetProperty("valueId").ValueKind);
        Assert.Equal("Marka", attributes[0].GetProperty("customValue").GetString());
        Assert.Equal(98765, attributes[1].GetProperty("valueId").GetInt64());
        Assert.Equal(JsonValueKind.Null, attributes[1].GetProperty("customValue").ValueKind);
    }

    [Fact]
    public void FieldsNotPicked_AreLeftOut_SoN11KeepsWhatItHolds()
    {
        using var json = JsonDocument.Parse(N11ProductUpdateComposer.Payload("Ravencia", [new("A", "Açıklama", null, null, null, null), new("B", "Açıklama", null, null, null, null)]));
        var skus = json.RootElement.GetProperty("payload").GetProperty("skus");
        Assert.Equal(2, skus.GetArrayLength());
        foreach (var sku in skus.EnumerateArray())
            Assert.Equal(["stockCode", "description"], sku.EnumerateObject().Select(property => property.Name));
        // A delivery pick on a connection without a chosen template sends the preparing day alone.
        using var delivery = JsonDocument.Parse(N11ProductUpdateComposer.Payload("Ravencia", [new("A", null, null, 5, null, null)]));
        Assert.Equal(["stockCode", "preparingDay"], delivery.RootElement.GetProperty("payload").GetProperty("skus")[0].EnumerateObject().Select(property => property.Name));
        // Never a field the n11 update cannot change or does not know.
        foreach (var name in new[] { "title", "images", "brand", "categoryId", "barcode", "desi", "salePrice", "listPrice", "quantity", "currencyType", "productMainId" })
            Assert.DoesNotContain($"\"{name}\"", json.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true, true, true, true)]
    [InlineData(new[] { "DESCRIPTION" }, true, false, false, false)]
    [InlineData(new[] { " attributes ", "vat_rate" }, false, true, true, false)]
    [InlineData(new[] { "DELIVERY", "TITLE", "IMAGES" }, false, false, false, true)]
    [InlineData(new[] { "TITLE", "IMAGES", "DESI", "WARRANTY" }, false, false, false, false)]
    public void Picks_AreTheFieldsN11Takes(string[]? fields, bool description, bool attributes, bool vatRate, bool delivery)
    {
        var picks = N11ProductUpdateComposer.Picks(fields);
        Assert.Equal(new N11UpdatePicks(description, attributes, vatRate, delivery), picks);
        Assert.Equal(description || attributes || vatRate || delivery, picks.Any);
    }

    [Theory]
    [InlineData("BS7429-40", null, true)]
    [InlineData("BS7429-40", MarketplaceVariantLinkHealth.NotFound, false)]
    [InlineData("BS7429-40", MarketplaceVariantLinkHealth.Rejected, false)]
    // An n11 item read without a stock code cannot be addressed by the update.
    [InlineData("n11:123456", null, false)]
    [InlineData(" ", null, false)]
    public void OnlyASkuN11HoldsByItsStockCode_IsSent(string externalId, string? health, bool sent)
    {
        var link = new MarketplaceVariantLink { ExternalId = externalId, HealthStatus = health! };
        Assert.Equal(sent, N11ProductUpdateComposer.Addressable(link));
    }

    [Fact]
    public void TheTaskBody_CountsAsAPhaseWithItems()
    {
        var body = N11ProductUpdateComposer.Payload("Ravencia", [new("A", "Açıklama", null, null, null, null)]);
        Assert.True(MarketplaceJobProcessor.HasItems(body));
        Assert.False(MarketplaceJobProcessor.HasItems("""{"payload":{"integrator":"x","skus":[]}}"""));
        Assert.False(MarketplaceJobProcessor.HasItems(N11ProductUpdateComposer.EmptyItems));
        Assert.False(MarketplaceJobProcessor.HasItems("{}"));
        Assert.True(MarketplaceJobProcessor.HasItems("""{"items":[{"barcode":"B1"}]}"""));
        // The adapter takes the same body as a create: the integrator is set, 1-1000 SKUs.
        using var sent = JsonDocument.Parse(N11HttpClient.CreatePayload(body, "Ravencia Hub")!);
        Assert.Equal("Ravencia Hub", sent.RootElement.GetProperty("payload").GetProperty("integrator").GetString());
    }

    [Fact]
    public void ProductUpdateTaskAnswers_AreReadLikeTheOtherN11Tasks()
    {
        var now = DateTimeOffset.UtcNow;
        using var queued = JsonDocument.Parse("""{"id":2017,"type":"PRODUCT_UPDATE","status":"IN_QUEUE","reasons":["1 adet SKU işlenmek üzere kuyruğa alındı."]}""");
        var accepted = N11TaskMapper.Submitted(queued.RootElement, now, "ürün güncelleme", "PRODUCT_UPDATE");
        Assert.Equal("2017", accepted!.Reference!.ExternalOperationId);
        Assert.Equal("PRODUCT_UPDATE", accepted.Reference.Kind);

        using var rejected = JsonDocument.Parse("""{"id":2018,"type":"PRODUCT_UPDATE","status":"REJECT","reasons":["Geçersiz KDV oranı."]}""");
        var refused = N11TaskMapper.Submitted(rejected.RootElement, now, "ürün güncelleme", "PRODUCT_UPDATE");
        Assert.Equal("N11_TASK_REJECTED", refused!.Error!.Code);
        Assert.Contains("ürün güncelleme", refused.Error.SafeMessage);
    }
}
