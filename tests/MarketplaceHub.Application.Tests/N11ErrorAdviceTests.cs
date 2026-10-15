using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.N11;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class N11ErrorAdviceTests
{
    [Theory]
    [InlineData("Girdiğiniz BS7429-40 seller stock code tarafınızdan kullanılmaktadır.", "zaten kullanılıyor")]
    [InlineData("BS7429-40 stockCode vatRate VatRate 0,1,10,20 değerlerinden biri olmak zorundadır.", "0, 1, 10 veya 20")]
    [InlineData("Ürün ekleme isteğinizde 15 karakter altında ürün adları tespit edilmiştir.", "en az 15 karakter")]
    [InlineData("1000012 ID’li Renk özellik alanı boş olamaz.", "zorunlu kategori özelliği")]
    [InlineData("BS7429 shipmentTemplate alanı geçersizdir.", "kargo şablonu adı")]
    [InlineData("Bu ürün için girdiğiniz fiyatta fahiş fiyat düşüklüğü olduğundan dolayı fiyat hatası riski içermektedir (Minimum 120 TL).", "fiyatı yükseltin")]
    [InlineData("Girilen X SellerStockCode ile mağazanızda ürün bulunamamaktadır.", "önce ürünü açın")]
    public void KnownMessagesKeepN11TextAndGetAHint(string message, string hintPart)
    {
        var advised = N11ErrorAdvice.WithAdvice(message);

        Assert.StartsWith(message.TrimEnd('.'), advised);
        Assert.Contains("Öneri:", advised);
        Assert.Contains(hintPart, advised);
    }

    [Theory]
    [InlineData("Beklenmeyen bir N11 hatası.")]
    [InlineData("")]
    public void UnknownMessagesComeBackUnchanged(string message) =>
        Assert.Equal(message, N11ErrorAdvice.WithAdvice(message));

    [Fact]
    public void RejectedSkuLinesCarryTheHint()
    {
        using var json = JsonDocument.Parse("""
            {"id":1,"status":"PROCESSED","skus":{"content":[{"itemCode":"BS7429-40","status":"REJECT","reasons":["İlk görsel boş olamaz"]}]}}
            """);

        var status = N11TaskMapper.Status(json.RootElement, "1");

        Assert.Contains("İlk görsel boş olamaz", status.Lines[0].Status);
        Assert.Contains("Öneri:", status.Lines[0].Status);
    }

    [Fact]
    public void WholeTaskRefusalsCarryTheHint()
    {
        using var json = JsonDocument.Parse("""{"id":1,"type":"PRODUCT_CREATE","status":"REJECT","reasons":["15 karakter altında ürün adları tespit edilmiştir."]}""");

        var refused = N11TaskMapper.Submitted(json.RootElement, DateTimeOffset.UtcNow, "ürün açma", "PRODUCT_CREATE");

        Assert.Contains("Öneri:", refused!.Error!.SafeMessage);
    }
}
