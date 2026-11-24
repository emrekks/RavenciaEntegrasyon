using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// O8 (scan 23 Nov 2026): Trendyol numbers an order awaiting payment "0" until it creates the package. That number is kept
// as the order's placeholder package (ShipmentPackageSupersession, closed once the real package arrives), but only when
// no id field names a real package: a zero "id" never hides a real shipmentPackageId, and "000" is the same placeholder.
public sealed class TrendyolPackageIdTests
{
    private static string Package(string idFields, string status, string orderNumber) => $$"""
        {
          {{idFields}}
          "orderNumber": "{{orderNumber}}",
          "orderDate": 1762253333685,
          "lastModifiedDate": 1762865408581,
          "currencyCode": "TRY",
          "status": "{{status}}",
          "lines": [
            { "lineId": 1, "quantity": 1, "lineUnitPrice": 100.0, "vatRate": 10, "barcode": "8690000000001", "stockCode": "TEST-1", "productName": "Test Ürün", "orderLineItemStatusName": "{{status}}" }
          ]
        }
        """;

    private static string Page(params string[] packages) => $$"""{ "content": [ {{string.Join(", ", packages)}} ], "page": 0, "totalPages": 1 }""";

    [Fact]
    public void AZeroIdBesideTheRealShipmentPackageIdTakesTheRealOne()
    {
        var page = TrendyolJsonMapper.Orders(Page(Package("\"id\": 0, \"shipmentPackageId\": 3330111999,", "Created", "10900000102")));

        var order = Assert.Single(page.Items);
        Assert.Equal("3330111999", Assert.Single(order.Packages).ExternalPackageId);
    }

    [Theory]
    [InlineData("\"id\": 0, \"shipmentPackageId\": 0,")]
    [InlineData("\"shipmentPackageId\": \"000\",")]
    public void AnAwaitingOrderWithoutARealPackageKeepsTheOnePlaceholder(string idFields)
    {
        var page = TrendyolJsonMapper.Orders(Page(Package(idFields, "Awaiting", "10900000101")));

        var order = Assert.Single(page.Items);
        Assert.True(ShipmentPackageSupersession.IsPlaceholder(Assert.Single(order.Packages).ExternalPackageId));
        Assert.Empty(page.Issues ?? []);
    }

    [Fact]
    public void APackageWithoutAnyIdIsRefusedWithItsIssue()
    {
        var page = TrendyolJsonMapper.Orders(Page(Package("", "Created", "10900000103")));

        Assert.Empty(page.Items);
        Assert.Contains("paket kimliği", Assert.Single(page.Issues ?? []).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"id\": 0, \"shipmentPackageId\": 0, \"packageId\": 0}", "0")]
    [InlineData("{\"id\": 1234}", "1234")]
    [InlineData("{\"id\": \"0\", \"packageId\": \"77\"}", "77")]
    [InlineData("{\"shipmentPackageId\": 3330111111}", "3330111111")]
    [InlineData("{\"orderNumber\": \"1\"}", "")]
    public void PackageIdReadsTheFirstIdThatIsNotZero(string json, string expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, TrendyolJsonMapper.PackageId(document.RootElement));
    }
}
