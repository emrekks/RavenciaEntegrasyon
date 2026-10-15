using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11PriceInventoryTests
{
    [Fact]
    public void PayloadUsesStockCodesTwoDecimalPricesAndTheIntegratorName()
    {
        var lines = new[]
        {
            new PriceInventoryPushLine(Guid.NewGuid(), Guid.NewGuid(), "test_variant_ea1_1", 2, 2000m, 1599.999m, "TRY", 1, 1, "h1"),
            new PriceInventoryPushLine(Guid.NewGuid(), Guid.NewGuid(), "test_variant_ea1_2", 1_500_000, 2200.5m, 1800m, "TRY", 1, 1, "h2")
        };

        var json = N11PriceInventoryRules.Payload(lines);

        Assert.Contains("\"listPrice\":2000.00", json);
        Assert.Contains("\"salePrice\":1600.00", json);
        Assert.Contains("\"listPrice\":2200.50", json);
        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("Ravencia", payload.GetProperty("integrator").GetString());
        var skus = payload.GetProperty("skus");
        Assert.Equal("test_variant_ea1_1", skus[0].GetProperty("stockCode").GetString());
        Assert.Equal(2, skus[0].GetProperty("quantity").GetInt32());
        Assert.Equal(999_999, skus[1].GetProperty("quantity").GetInt32());
        Assert.Equal("TL", skus[1].GetProperty("currencyType").GetString());
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(4.9, 4)]
    [InlineData(2_000_000, 999_999)]
    public void QuantityIsWholeNonNegativeAndCapped(decimal available, decimal expected) =>
        Assert.Equal(expected, N11PriceInventoryRules.Quantity(available));

    [Fact]
    public void QueuedTaskReturnsItsIdAndRejectedTaskBecomesAValidationError()
    {
        using var queued = JsonDocument.Parse("""{"id":1092,"type":"SKU_UPDATE","status":"IN_QUEUE","reasons":["1 sku işlenmeye alındı."]}""");
        using var rejected = JsonDocument.Parse("""{"id":1093,"type":"SKU_UPDATE","status":"REJECT","reasons":["listPrice, salePrice'dan düşük olamaz."]}""");

        var accepted = N11TaskMapper.Submitted(queued.RootElement, DateTimeOffset.UnixEpoch)!;
        var refused = N11TaskMapper.Submitted(rejected.RootElement, DateTimeOffset.UnixEpoch)!;

        Assert.Equal("1092", accepted.Reference!.ExternalOperationId);
        Assert.Null(accepted.Error);
        Assert.Equal("N11_TASK_REJECTED", refused.Error!.Code);
        Assert.Contains("salePrice", refused.Error.SafeMessage);
    }

    [Fact]
    public void TaskDetailsMapToLinesKeyedByStockCode()
    {
        using var detail = JsonDocument.Parse("""
            {"taskId":1095,"skus":{"content":[
              {"id":1585,"taskId":1095,"itemCode":"1234567m","status":"SUCCESS","reasons":["Başarıyla tamamlandı."]},
              {"id":1586,"taskId":1095,"itemCode":"1234567s","status":"FAIL","reasons":["Ürün bulunamadı."]}]},
             "status":"PROCESSED"}
            """);
        using var queued = JsonDocument.Parse("""{"taskId":1096,"skus":{"content":[]},"status":"IN_QUEUE"}""");

        var status = N11TaskMapper.Status(detail.RootElement, "1095");

        Assert.Equal("COMPLETED", status.Status);
        Assert.True(status.Lines.Single(x => x.ExternalKey == "1234567m").Succeeded);
        var failed = status.Lines.Single(x => x.ExternalKey == "1234567s");
        Assert.False(failed.Succeeded);
        Assert.Equal("N11_SKU_REJECTED", failed.ErrorCode);
        Assert.Equal("Ürün bulunamadı.", failed.Status);
        Assert.Equal("IN_PROGRESS", N11TaskMapper.Status(queued.RootElement, "1096").Status);

        using var droppedWithoutLines = JsonDocument.Parse("""{"taskId":1097,"skus":{"content":[]},"status":"REJECT"}""");
        Assert.Equal("REJECTED", N11TaskMapper.Status(droppedWithoutLines.RootElement, "1097").Status);
    }

    [Fact]
    public void WritesNeedTheConnectionSwitch()
    {
        Assert.True(N11HttpClient.ConnectionWritesEnabled("""{"ExternalWritesEnabled":true}"""));
        Assert.False(N11HttpClient.ConnectionWritesEnabled("""{"ExternalWritesEnabled":false,"InvoiceCreationEnabled":false}"""));
        Assert.False(N11HttpClient.ConnectionWritesEnabled("bozuk"));
    }
}
