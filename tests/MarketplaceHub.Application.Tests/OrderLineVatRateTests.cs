using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// T-2 (29 Oct 2026, live): a Hepsiburada claim read carries no vatRate; the line came back at 0% and overwrote the
// 10% the order read had stored. A rate the marketplace did not send is unknown (null), never 0.
public sealed class OrderLineVatRateTests
{
    [Fact]
    public void AHepsiburadaClaimWithoutVatRateLeavesTheRateUnknown()
    {
        const string payload = """
        {
          "claimNumber": "032985701", "status": "AwaitingAction", "claimDate": "2026-09-28T12:15:00Z",
          "orderNumber": "0414141341", "lineItemId": "line-17", "merchantSku": "merchant-sku-17",
          "productName": "Test Product", "quantity": 1, "price": { "amount": 25.50, "currency": "TRY" }
        }
        """;

        var line = Assert.Single(HepsiburadaJsonMapper.OrderFromReturnClaim(payload)!.Lines);
        Assert.Null(line.VatRate);

        var withRate = Assert.Single(HepsiburadaJsonMapper.OrderFromReturnClaim(payload.Replace("\"quantity\": 1", "\"quantity\": 1, \"vatRate\": 10", StringComparison.Ordinal))!.Lines);
        Assert.Equal(10m, withRate.VatRate);
    }

    [Fact]
    public void AHepsiburadaOrderLineWithoutVatRateLeavesTheRateUnknown()
    {
        using var json = JsonDocument.Parse("""
        {
          "id": "line-20", "orderNumber": "HB-2026-20", "orderDate": "2026-09-29T09:15:00Z",
          "merchantSku": "SELLER-20", "name": "Test product", "quantity": 1,
          "unitPrice": { "currency": "TRY", "amount": 10.0 }
        }
        """);

        Assert.Null(Assert.Single(HepsiburadaJsonMapper.PaidOrderLine(json.RootElement).Lines).VatRate);
    }

    [Fact]
    public void AnN11LineWithoutVatRateLeavesTheRateUnknown()
    {
        const string page = """
            {"page":0,"totalPages":1,"content":[{
              "orderNumber":"203872347999","id":"112999455244999",
              "lines":[{"orderLineId":"L1","quantity":1,"productName":"Bluz","stockCode":"BLZ-1","price":100,"sellerInvoiceAmount":100,"totalSellerDiscountPrice":0}],
              "packageHistories":[{"createdDate":1724274054460,"status":"Created"}],"lastModifiedDate":1724323386203,"shipmentPackageStatus":"Created"}]}
            """;

        Assert.Null(Assert.Single(Assert.Single(N11OrderMapper.Packages(page).Items).Lines).VatRate);
    }

    [Fact]
    public void APazaramaItemWithoutVatRateLeavesTheRateUnknown()
    {
        using var json = JsonDocument.Parse("""
        {"data":[{"orderId":"3099b7db-6a35-498d-a45a-4f6dd57a2ecb","orderNumber":283534307,"orderDate":"2026-02-17 17:23","orderAmount":7,"currency":"TL",
          "items":[{"orderItemId":"a1","orderItemStatus":3,"quantity":1,
            "salePrice":{"currency":"TL","value":7},"totalPrice":{"currency":"TL","value":7},
            "product":{"name":"ürün","stockCode":"212","code":"testurunu001"}}]}]}
        """);

        var order = PazaramaOrderMapper.Page(json.RootElement, 100).Items.Single();

        Assert.Null(Assert.Single(order.Lines).VatRate);
    }

    [Theory]
    // Hepsiburada: the order line's own JSON, under either spelling.
    [InlineData("HEPSIBURADA", """{"id":"1","merchantSku":"S"}""", true)]
    [InlineData("HEPSIBURADA", """{"id":"1","vatRate":10}""", false)]
    [InlineData("HEPSIBURADA", """{"id":"1","VatRate":0}""", false)]
    [InlineData("HEPSIBURADA", """{"id":"1","vatRate":null}""", true)]
    // n11: the package line.
    [InlineData("N11", """{"orderLineId":"1","price":100}""", true)]
    [InlineData("N11", """{"orderLineId":"1","vatRate":20}""", false)]
    // Pazarama: the item's product carries the rate.
    [InlineData("PAZARAMA", """{"orderItemId":"a1","product":{"name":"x"}}""", true)]
    [InlineData("PAZARAMA", """{"orderItemId":"a1","vatRate":10}""", true)]
    [InlineData("PAZARAMA", """{"orderItemId":"a1","product":{"vatRate":10}}""", false)]
    // PTT AVM: the line's fields as text, empty ones left out.
    [InlineData("PTTAVM", """{"LineItemId":"1"}""", true)]
    [InlineData("PTTAVM", """{"LineItemId":"1","KdvOrani":"10"}""", false)]
    [InlineData("PTTAVM", """{"LineItemId":"1","KdvOrani":"on"}""", true)]
    // Shopify: the line's taxLines list, every tax line with a numeric rate.
    [InlineData("SHOPIFY", """{"id":"1"}""", true)]
    [InlineData("SHOPIFY", """{"id":"1","taxLines":[]}""", true)]
    [InlineData("SHOPIFY", """{"id":"1","taxLines":[{"rate":0.2},{"title":"x"}]}""", true)]
    [InlineData("SHOPIFY", """{"id":"1","taxLines":{"rate":0.2}}""", false)]
    [InlineData("SHOPIFY", """{"id":"1","taxLines":[{"rate":0.18},{"rate":0.02}]}""", false)]
    [InlineData("SHOPIFY", """{"id":"1","taxLines":[{"rate":0}]}""", false)]
    // O1 (5 Nov 2026): a line without a raw snapshot, with an empty one or one that cannot be read reports no rate:
    // unknown, never the rate it was stored with (0 included).
    [InlineData("HEPSIBURADA", null, true)]
    [InlineData("N11", "{}", true)]
    [InlineData("PAZARAMA", "not json", true)]
    [InlineData("TRENDYOL", "[]", true)]
    public void TheInvoiceGateStopsALineWhoseMarketplaceDidNotReportItsVatRate(string platformCode, string? snapshot, bool unknown) =>
        Assert.Equal(unknown, InvoiceIssuePolicy.IsLineVatRateUnknown(platformCode, snapshot));

    // O1: an unreported rate is taken from the product card; with neither, the line has no rate and nothing is issued.
    [Theory]
    [InlineData("""{"id":"1","vatRate":10}""", null, 10.0)]
    [InlineData("""{"id":"1","vatRate":10}""", 20.0, 10.0)]
    [InlineData("""{"id":"1"}""", 20.0, 20.0)]
    [InlineData(null, 1.0, 1.0)]
    [InlineData("""{"id":"1"}""", null, null)]
    [InlineData(null, null, null)]
    public void AnUnreportedLineRateComesFromTheProductCardAndIsNeverZeroByDefault(string? snapshot, double? cardRate, double? expected)
    {
        var resolved = InvoiceIssuePolicy.ResolveLineVatRate("HEPSIBURADA", snapshot, storedLineVatRate: snapshot is null ? 0m : 10m, productCardVatRate: (decimal?)cardRate);
        Assert.Equal((decimal?)expected, resolved);
    }

    [Fact]
    public void TheRefusalSaysTheRateIsUnknownOnTheCardToo() =>
        Assert.Contains("KDV Oranı Bilinmiyor", InvoiceIssuePolicy.LineVatRateUnknownFor("TRENDYOL").Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("TRENDYOL", "Trendyol")]
    [InlineData("HEPSIBURADA", "Hepsiburada")]
    [InlineData("N11", "N11")]
    [InlineData("PAZARAMA", "Pazarama")]
    [InlineData("PTTAVM", "PTT AVM")]
    [InlineData("SHOPIFY", "Shopify")]
    public void TheRefusalNamesTheMarketplaceThatLeftTheRateOut(string platformCode, string name)
    {
        var refusal = InvoiceIssuePolicy.LineVatRateUnknownFor(platformCode);

        Assert.Equal(InvoiceIssuePolicy.LineVatRateUnknownCode, refusal.Code);
        Assert.StartsWith($"{name} siparişin", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Trendyol", InvoiceIssuePolicy.DescribeValidationCode(InvoiceIssuePolicy.LineVatRateUnknownCode), StringComparison.Ordinal);
    }
}
