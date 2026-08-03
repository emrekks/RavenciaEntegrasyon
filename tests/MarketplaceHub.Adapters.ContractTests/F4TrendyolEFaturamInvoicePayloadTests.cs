using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F4TrendyolEFaturamInvoicePayloadTests
{
    [Fact]
    public void Official_payload_uses_kurus_distinct_lines_exact_total_and_only_requested_note()
    {
        var account = new EfaturamFiscalAccount(10, 20, "RVN");
        var recipient = new EfaturamRecipient("11111111111", "TR", "İstanbul", "Kadıköy", "Anonim adres", null, null, null, "Anonim", "Müşteri", null);
        var source = new EfaturamInvoicePayloadSource(
            "local-1", "EARSIVFATURA", "TRY", "YALNIZ: ÜÇ YÜZ TÜRK LİRASI", "ORDER-1", new DateOnly(2026, 8, 3),
            new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.FromHours(3)), recipient,
            [
                new("Birinci Ürün", "C62", 1, 100m, 100m, 20m, 20m, 0m, 120m),
                new("İkinci Ürün", "C62", 1, 150m, 150m, 30m, 20m, 0m, 180m)
            ],
            new("https://www.trendyol.com", "Trendyol", "CARD", new DateTimeOffset(2026, 8, 3, 11, 0, 0, TimeSpan.FromHours(3)), "CREDIT_CARD"),
            new("1111111111", "Anonim Kargo", null, new DateOnly(2026, 8, 3)));

        using var json = JsonDocument.Parse(TrendyolEFaturamInvoicePayload.Create(account, source));
        var root = json.RootElement;
        Assert.Equal("YALNIZ: ÜÇ YÜZ TÜRK LİRASI", Assert.Single(root.GetProperty("notes").EnumerateArray()).GetString());
        Assert.Equal(2, root.GetProperty("invoiceLines").GetArrayLength());
        Assert.Equal("Birinci Ürün", root.GetProperty("invoiceLines")[0].GetProperty("itemName").GetString());
        Assert.Equal(10000, root.GetProperty("invoiceLines")[0].GetProperty("taxableAmount").GetInt64());
        Assert.Equal(2000, root.GetProperty("invoiceLines")[0].GetProperty("taxAmount").GetInt64());
        Assert.Equal(30000, root.GetProperty("invoiceTotal").GetProperty("payableAmount").GetInt64());
        Assert.Equal(25000, root.GetProperty("invoiceTotal").GetProperty("taxExclusiveAmount").GetInt64());
        Assert.Equal(5000, root.GetProperty("totalTax").GetProperty("totalTaxAmount").GetInt64());
        Assert.Equal("https://www.trendyol.com", root.GetProperty("paymentInfo").GetProperty("purchaseUrl").GetString());
    }

    [Fact]
    public void Internet_sale_earchive_rejects_missing_payment_or_delivery_fields()
    {
        var source = new EfaturamInvoicePayloadSource("local", "EARSIVFATURA", "TRY", "YALNIZ: SIFIR TÜRK LİRASI", "ORDER", new DateOnly(2026, 8, 3), DateTimeOffset.UtcNow,
            new("11111111111", "TR", "İstanbul", "Kadıköy", "Adres", null, null, null, "Ad", "Soyad", null),
            [new("Ürün", "C62", 1, 0, 0, 0, 0, 0, 0)]);
        Assert.Throws<ArgumentException>(() => TrendyolEFaturamInvoicePayload.Create(new(1, 1, null), source));
    }

    [Fact]
    public void Official_create_response_and_permanent_document_url_are_mapped_without_guessing()
    {
        var result = TrendyolEFaturamJsonMapper.OutgoingInvoice("""{"invoiceUuid":"96a8b0f1-7e10-4a44-8d78-eb961539bb4b","invoiceId":"RVN2026000000001","status":205}""", "request-1");
        Assert.Equal("96a8b0f1-7e10-4a44-8d78-eb961539bb4b", result.ExternalReference);
        Assert.Equal("RVN2026000000001", result.InvoiceNumber);
        Assert.Equal("205", result.RawStatus);
        Assert.Equal("https://documents.example.test/invoice.pdf", TrendyolEFaturamJsonMapper.PermanentDocumentUrl("\"https://documents.example.test/invoice.pdf\""));
        Assert.Throws<JsonException>(() => TrendyolEFaturamJsonMapper.PermanentDocumentUrl("not-a-url"));
    }
}
