using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolCarrierCatalogTests
{
    [Theory]
    [InlineData("HepsiJet")]
    [InlineData("HEPSIJET")]
    [InlineData("HepsiJet Kargo")]
    public void Resolves_hepsijet_to_its_registered_carrier_identity(string provider)
    {
        Assert.True(TrendyolCarrierCatalog.TryResolve(provider, out var carrier));
        Assert.Equal("2650701090", carrier.TaxId);
        Assert.Equal("D FAST DAĞITIM HİZMETLERİ VE LOJİSTİK ANONİM ŞİRKETİ", carrier.Name);
    }

    [Fact]
    public void Canonical_e_archive_payload_includes_hepsijet_sender_identity()
    {
        const string canonical = """
        {
          "Id":"invoice-1",
          "InvoiceType":"EARSIVFATURA",
          "Currency":"TRY",
          "Note":"Satış faturası",
          "IssuedAt":"2026-10-05T12:00:00+03:00",
          "Order":{
            "OrderNumber":"4227866665",
            "OrderedAt":"2026-09-26T18:52:26+03:00",
            "CustomerSnapshotJson":"{}",
            "InvoiceAddressSnapshotJson":"{\"invoiceAddress\":{\"taxNumber\":\"1234567890\",\"fullAddress\":\"Test adres\",\"city\":\"İstanbul\",\"district\":\"Şişli\"}}"
          },
          "Package":{"CargoProviderExternalId":"HepsiJet","StatusOccurredAt":"2026-09-29T10:00:00+03:00"},
          "Lines":[{"DescriptionSnapshot":"Ürün","UnitSnapshot":"ADET","Quantity":1,"UnitPrice":100,"LineTotal":120,"VatAmount":20,"VatRate":20,"DiscountAmount":0}]
        }
        """;

        var payload = TrendyolEFaturamCanonicalPayload.Create(new(1, 2, null), canonical);
        using var document = JsonDocument.Parse(payload);
        var delivery = document.RootElement.GetProperty("deliveryInfo");

        Assert.Equal("2650701090", delivery.GetProperty("carrierTaxId").GetString());
        Assert.Equal("D FAST DAĞITIM HİZMETLERİ VE LOJİSTİK ANONİM ŞİRKETİ", delivery.GetProperty("carrierName").GetString());
    }
}
