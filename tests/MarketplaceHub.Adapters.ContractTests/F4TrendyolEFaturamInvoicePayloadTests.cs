using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F4TrendyolEFaturamInvoicePayloadTests
{
    [Fact]
    public void Official_payload_uses_kurus_distinct_lines_exact_total_and_only_requested_note()
    {
        var account = new EfaturamFiscalAccount(10, 20, null);
        var recipient = new EfaturamRecipient("11111111111", "TR", "İstanbul", "Kadıköy", "Anonim adres", null, null, null, "Anonim", "Müşteri", null);
        var source = new EfaturamInvoicePayloadSource(
            "local-1", "EARSIVFATURA", "TRY", "YALNIZ: ÜÇ YÜZ TÜRK LİRASI", "ORDER-1", new DateOnly(2026, 8, 3),
            new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.FromHours(3)), recipient,
            [
                new("Birinci Ürün", "C62", 1, 100m, 100m, 20m, 20m, 0m, 120m),
                new("İkinci Ürün", "C62", 1, 150m, 150m, 30m, 20m, 0m, 180m)
            ],
            new("https://www.trendyol.com", "Trendyol", "PAZARYERI", new DateTimeOffset(2026, 8, 3, 11, 0, 0, TimeSpan.FromHours(3)), "MEDIATOR"),
            new("1111111111", "Anonim Kargo", null, new DateOnly(2026, 8, 3)));

        using var json = JsonDocument.Parse(TrendyolEFaturamInvoicePayload.Create(account, source));
        var root = json.RootElement;
        Assert.Equal("PARTNER", root.GetProperty("source").GetString());
        Assert.Equal("YALNIZ: ÜÇ YÜZ TÜRK LİRASI", Assert.Single(root.GetProperty("notes").EnumerateArray()).GetString());
        Assert.Equal(2, root.GetProperty("invoiceLines").GetArrayLength());
        Assert.Equal("Birinci Ürün", root.GetProperty("invoiceLines")[0].GetProperty("itemName").GetString());
        Assert.Equal(10000, root.GetProperty("invoiceLines")[0].GetProperty("taxableAmount").GetInt64());
        Assert.Equal(2000, root.GetProperty("invoiceLines")[0].GetProperty("taxAmount").GetInt64());
        Assert.Equal(30000, root.GetProperty("invoiceTotal").GetProperty("payableAmount").GetInt64());
        Assert.Equal(25000, root.GetProperty("invoiceTotal").GetProperty("taxExclusiveAmount").GetInt64());
        Assert.Equal(5000, root.GetProperty("totalTax").GetProperty("totalTaxAmount").GetInt64());
        Assert.False(root.TryGetProperty("prefix", out _));
        Assert.Equal("https://www.trendyol.com", root.GetProperty("paymentInfo").GetProperty("purchaseUrl").GetString());
        Assert.Equal("2026-08-03T09:00:00.000Z", root.GetProperty("issuedAt").GetString());
        Assert.Equal("2026-08-03T08:00:00.000Z", root.GetProperty("paymentInfo").GetProperty("paymentDate").GetString());
    }

    [Fact]
    public void Official_payload_derives_tax_exclusive_unit_price_from_line_basis()
    {
        var source = new EfaturamInvoicePayloadSource(
            "local", "EARSIVFATURA", "TRY", "YALNIZ", "ORDER", new DateOnly(2026, 8, 18), DateTimeOffset.UtcNow,
            new("11111111111", "TR", "İzmir", "Bornova", "Test adresi", null, null, null, "Test", "Müşteri", null),
            [new("Ürün", "C62", 2m, 52.90m, 88.17m, 17.63m, 20m, 0m, 105.80m)],
            new("https://www.trendyol.com", "Trendyol", "PAZARYERI", DateTimeOffset.UtcNow, "MEDIATOR"),
            new("8590921777", "Yurtiçi Kargo", null, new DateOnly(2026, 8, 18)));

        using var payload = JsonDocument.Parse(TrendyolEFaturamInvoicePayload.Create(new(1, 1, null), source));
        var line = payload.RootElement.GetProperty("invoiceLines")[0];

        Assert.Equal(4408.5m, line.GetProperty("unitPriceAmount").GetDecimal());
        Assert.Equal(8817, line.GetProperty("totalAmount").GetInt64());
    }

    [Fact]
    public void Low_level_payload_omits_optional_internet_fields_when_source_does_not_supply_them()
    {
        var source = new EfaturamInvoicePayloadSource("local", "TEMELFATURA", "TRY", "YALNIZ: SIFIR TÜRK LİRASI", "ORDER", new DateOnly(2026, 8, 3), DateTimeOffset.UtcNow,
            new("11111111111", "TR", "İstanbul", "Kadıköy", "Adres", null, null, null, "Ad", "Soyad", null),
            [new("Ürün", "C62", 1, 0, 0, 0, 0, 0, 0)]);
        using var payload = JsonDocument.Parse(TrendyolEFaturamInvoicePayload.Create(new(1, 1, null), source));
        Assert.False(payload.RootElement.TryGetProperty("paymentInfo", out _));
        Assert.False(payload.RootElement.TryGetProperty("deliveryInfo", out _));
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
        Assert.Throws<JsonException>(() => TrendyolEFaturamJsonMapper.PermanentDocumentUrl("http://documents.example.test/invoice.pdf"));
    }

    [Fact]
    public void Canonical_Trendyol_order_is_converted_to_official_distinct_line_payload()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(),
            InvoiceType = "EARSIVFATURA",
            Currency = "TRY",
            Note = "YALNIZ: ÜÇ YÜZ TÜRK LİRASI",
            PayableTotal = 300m,
            IssuedAt = new DateTimeOffset(2026, 8, 3, 12, 5, 0, TimeSpan.FromHours(3)),
            Order = new
            {
                OrderNumber = "ORDER-1",
                OrderedAt = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.FromHours(3)),
                CustomerSnapshotJson = JsonSerializer.Serialize(new { customerFirstName = "Anonim", customerLastName = "Müşteri", customerEmail = "anonim@example.test" }),
                InvoiceAddressSnapshotJson = JsonSerializer.Serialize(new { invoiceAddress = new { identityNumber = "11111111111", countryCode = "TR", city = "İstanbul", district = "Kadıköy", fullAddress = "Anonim adres" } }),
                ShipmentAddressSnapshotJson = "{}"
            },
            Package = new { ExternalPackageId = "P-1", CargoProviderExternalId = "TEXMP", StatusOccurredAt = new DateTimeOffset(2026, 8, 3, 13, 0, 0, TimeSpan.FromHours(3)) },
            Lines = new[]
            {
                new { LineSequence = 1, DescriptionSnapshot = "Birinci Ürün", SkuSnapshot = "SKU-1", UnitSnapshot = "ADET", Quantity = 1m, UnitPrice = 100m, DiscountAmount = 0m, VatRate = 20m, VatAmount = 20m, LineTotal = 120m },
                new { LineSequence = 2, DescriptionSnapshot = "İkinci Ürün", SkuSnapshot = "SKU-2", UnitSnapshot = "ADET", Quantity = 1m, UnitPrice = 150m, DiscountAmount = 0m, VatRate = 20m, VatAmount = 30m, LineTotal = 180m }
            }
        });
        var account = new EfaturamFiscalAccount(10, 20, null, "WEB");

        using var result = JsonDocument.Parse(TrendyolEFaturamCanonicalPayload.Create(account, canonical));
        Assert.Equal(2, result.RootElement.GetProperty("invoiceLines").GetArrayLength());
        Assert.Equal(30000, result.RootElement.GetProperty("invoiceTotal").GetProperty("payableAmount").GetInt64());
        Assert.Equal("11111111111", result.RootElement.GetProperty("recipientInfo").GetProperty("taxId").GetString());
        Assert.Equal("https://www.trendyol.com", result.RootElement.GetProperty("paymentInfo").GetProperty("purchaseUrl").GetString());
        Assert.Equal("8590921777", result.RootElement.GetProperty("deliveryInfo").GetProperty("carrierTaxId").GetString());
    }

    [Fact]
    public void Canonical_payload_uses_customer_snapshot_tax_id_when_address_does_not_repeat_it()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(), InvoiceType = "TEMELFATURA", Currency = "TRY", Note = "YALNIZ: YÜZ YİRMİ TÜRK LİRASI", PayableTotal = 120m, IssuedAt = DateTimeOffset.UtcNow,
            Order = new { OrderNumber = "ORDER", OrderedAt = DateTimeOffset.UtcNow, CustomerSnapshotJson = JsonSerializer.Serialize(new { customerTaxNumber = "11111111111", customerFirstName = "Test", customerLastName = "Müşteri" }), InvoiceAddressSnapshotJson = JsonSerializer.Serialize(new { invoiceAddress = new { countryCode = "TR", city = "İzmir", district = "Bornova", fullAddress = "Test adresi" } }), ShipmentAddressSnapshotJson = "{}" },
            Package = (object?)null,
            Lines = new[] { new { LineSequence = 1, DescriptionSnapshot = "Ürün", SkuSnapshot = "SKU", UnitSnapshot = "ADET", Quantity = 1m, UnitPrice = 100m, DiscountAmount = 0m, VatRate = 20m, VatAmount = 20m, LineTotal = 120m } }
        });

        using var payload = JsonDocument.Parse(TrendyolEFaturamCanonicalPayload.Create(new EfaturamFiscalAccount(1, 1, null), canonical));

        Assert.Equal("11111111111", payload.RootElement.GetProperty("recipientInfo").GetProperty("taxId").GetString());
    }

    [Fact]
    public void Canonical_payload_rejects_non_ascii_tax_digits()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(),
            InvoiceType = "TEMELFATURA",
            Currency = "TRY",
            Note = "YALNIZ: YÜZ YİRMİ TÜRK LİRASI",
            PayableTotal = 120m,
            IssuedAt = DateTimeOffset.UtcNow,
            Order = new { OrderNumber = "ORDER", OrderedAt = DateTimeOffset.UtcNow, CustomerSnapshotJson = "{}", InvoiceAddressSnapshotJson = JsonSerializer.Serialize(new { invoiceAddress = new { identityNumber = "١١١١١١١١١١١", countryCode = "TR", city = "İstanbul", district = "Kadıköy", fullAddress = "Adres" } }), ShipmentAddressSnapshotJson = "{}" },
            Package = (object?)null,
            Lines = new[] { new { LineSequence = 1, DescriptionSnapshot = "Ürün", SkuSnapshot = "SKU", UnitSnapshot = "ADET", Quantity = 1m, UnitPrice = 100m, DiscountAmount = 0m, VatRate = 20m, VatAmount = 20m, LineTotal = 120m } }
        });
        var error = Assert.Throws<JsonException>(() => TrendyolEFaturamCanonicalPayload.Create(new EfaturamFiscalAccount(1, 1, null), canonical));
        Assert.Contains("EFATURAM_RECIPIENT_TAX_ID_REQUIRED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_earchive_rejects_missing_carrier_mapping()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(),
            InvoiceType = "EARSIVFATURA",
            Currency = "TRY",
            Note = "YALNIZ: YÜZ YİRMİ TÜRK LİRASI",
            PayableTotal = 120m,
            IssuedAt = DateTimeOffset.UtcNow,
            Order = new { OrderNumber = "ORDER", OrderedAt = DateTimeOffset.UtcNow, CustomerSnapshotJson = "{}", InvoiceAddressSnapshotJson = JsonSerializer.Serialize(new { invoiceAddress = new { identityNumber = "11111111111", countryCode = "TR", city = "İstanbul", district = "Kadıköy", fullAddress = "Adres" } }), ShipmentAddressSnapshotJson = "{}" },
            Package = new { ExternalPackageId = "P-1", CargoProviderExternalId = "UNMAPPED", StatusOccurredAt = DateTimeOffset.UtcNow },
            Lines = new[] { new { LineSequence = 1, DescriptionSnapshot = "Ürün", SkuSnapshot = "SKU", UnitSnapshot = "ADET", Quantity = 1m, UnitPrice = 100m, DiscountAmount = 0m, VatRate = 20m, VatAmount = 20m, LineTotal = 120m } }
        });
        var error = Assert.Throws<JsonException>(() => TrendyolEFaturamCanonicalPayload.Create(new EfaturamFiscalAccount(1, 1, null), canonical));
        Assert.Contains("EFATURAM_CARRIER_CATALOG_MISS", error.Message, StringComparison.Ordinal);
    }

}
