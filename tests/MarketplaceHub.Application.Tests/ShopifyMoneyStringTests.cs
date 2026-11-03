using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Imports;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Shopify's GraphQL Money scalars are JSON strings ("675.0"), its Int fields JSON numbers. The live order line snapshots
// (3 Nov 2026) carry "originalUnitPriceSet":{"shopMoney":{"amount":"675.0"}} next to "quantity":1; a reader of JSON
// numbers only turned every API price, total, discount and refund into 0. The payload below has that shape (order
// #R1004: two lines of 675.0, a 5% discount), with its texts and ids replaced.
public sealed class ShopifyMoneyStringTests
{
    private const string Order = """
        {"id":"gid://shopify/Order/6100000000001","name":"#R9004","createdAt":"2026-07-20T21:58:06Z","updatedAt":"2026-07-20T22:11:06Z","cancelledAt":null,
         "displayFinancialStatus":"PAID","email":null,"phone":null,
         "currentTotalPriceSet":{"shopMoney":{"amount":"1282.5","currencyCode":"TRY"}},
         "totalDiscountsSet":{"shopMoney":{"amount":"67.5","currencyCode":"TRY"}},
         "currentTotalDiscountsSet":{"shopMoney":{"amount":"67.5","currencyCode":"TRY"}},
         "lineItems":{"nodes":[
           {"id":"gid://shopify/LineItem/14200000000001","sku":"SKU-A","name":"Ürün A - Lacivert / 4XL","variant":null,"quantity":1,"currentQuantity":1,"originalUnitPriceSet":{"shopMoney":{"amount":"675.0","currencyCode":"TRY"}}},
           {"id":"gid://shopify/LineItem/14200000000002","sku":"SKU-B","name":"Ürün B - Siyah / 3XL","variant":null,"quantity":1,"currentQuantity":1,"originalUnitPriceSet":{"shopMoney":{"amount":"675.0","currencyCode":"TRY"}}}]},
         "fulfillments":[{"id":"gid://shopify/Fulfillment/5300000000001","status":"SUCCESS","displayStatus":"DELIVERED","deliveredAt":"2026-07-23T09:00:00Z","createdAt":"2026-07-21T08:00:00Z","updatedAt":"2026-07-23T09:00:00Z",
           "trackingInfo":[],"events":{"nodes":[]},
           "fulfillmentLineItems":{"nodes":[
             {"id":"gid://shopify/FulfillmentLineItem/1","quantity":1,"lineItem":{"id":"gid://shopify/LineItem/14200000000001"}},
             {"id":"gid://shopify/FulfillmentLineItem/2","quantity":1,"lineItem":{"id":"gid://shopify/LineItem/14200000000002"}}]}}],
         "refunds":[{"id":"gid://shopify/Refund/1090000000001","createdAt":"2026-07-25T10:00:00Z","totalRefundedSet":{"shopMoney":{"amount":"120.25","currencyCode":"TRY"}}}]}
        """;

    [Fact]
    public void AnApiOrderWithStringAmountsKeepsItsPricesTotalsAndRefunds()
    {
        using var json = JsonDocument.Parse(Order);
        var order = ShopifyHttpClient.MapOrder(json.RootElement);

        Assert.Equal((1350m, 67.5m, 1282.5m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        Assert.All(order.Lines, line => Assert.Equal(675m, line.UnitPrice));
        var package = Assert.Single(order.Packages);
        Assert.Equal((1350m, 67.5m, 1282.5m), (package.GrossAmount, package.DiscountAmount, package.NetAmount));
        var refund = Assert.Single(order.Refunds!);
        Assert.Equal(120.25m, refund.Amount);
        Assert.Equal(120.25m, order.RefundedAmount);
        Assert.Equal("PARTIALLY_REFUNDED", order.RefundStatus);
    }

    // The CSV-completed orders: an API read that now parses the same totals keeps the CSV values, so the next sync
    // changes nothing on them.
    [Fact]
    public void ACsvImportedOrderKeepsItsTotalsWhenTheApiReadAgrees()
    {
        using var json = JsonDocument.Parse(Order);
        var order = ShopifyHttpClient.MapOrder(json.RootElement);
        var snapshot = ShopifyOrderCsvSnapshotPolicy.ImportCustomer("{}", null, null, null, "paid", "fulfilled", null,
            grossTotal: 1350m, netTotal: 1282.5m, discountAmount: 67.5m, "TRY", DateTimeOffset.UnixEpoch);

        var merged = ShopifyOrderCsvSnapshotPolicy.MergeRemoteAmounts(snapshot, order.GrossAmount, order.DiscountAmount, order.NetAmount);

        Assert.Equal((1350m, 67.5m, 1282.5m), merged);
    }

    [Theory]
    [InlineData("\"2099.0\"", "2099.0")]
    [InlineData("\"2099.00\"", "2099.00")]
    [InlineData("\"0.0\"", "0.0")]
    [InlineData("\"-12.50\"", "-12.50")]
    [InlineData("\"44.95\"", "44.95")]
    [InlineData("675", "675")]
    [InlineData("675.5", "675.5")]
    public void AMoneyAmountIsReadFromAStringOrANumber(string raw, string expected)
    {
        using var json = JsonDocument.Parse($$"""{"amount":{{raw}}}""");
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ShopifyHttpClient.DecimalOrNull(json.RootElement, "amount"));
    }

    // A thousands separator, a comma decimal, an exponent or text is not a Shopify amount: it stays unknown instead
    // of being read as another number.
    [Theory]
    [InlineData("\"1,299.00\"")]
    [InlineData("\"1.299,00\"")]
    [InlineData("\"12,5\"")]
    [InlineData("\"1e3\"")]
    [InlineData("\"\"")]
    [InlineData("\"abc\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    public void AnAmountThatIsNotAPlainDecimalStaysUnknown(string raw)
    {
        using var json = JsonDocument.Parse($$"""{"amount":{{raw}}}""");
        Assert.Null(ShopifyHttpClient.DecimalOrNull(json.RootElement, "amount"));
    }

    [Fact]
    public void AMissingAmountOrANonObjectStaysUnknown()
    {
        using var json = JsonDocument.Parse("""{"other":"1.0","list":[1]}""");
        Assert.Null(ShopifyHttpClient.DecimalOrNull(json.RootElement, "amount"));
        Assert.Null(ShopifyHttpClient.DecimalOrNull(json.RootElement.GetProperty("list"), "amount"));
        Assert.Null(ShopifyHttpClient.DecimalOrNull(default, "amount"));
    }
}
