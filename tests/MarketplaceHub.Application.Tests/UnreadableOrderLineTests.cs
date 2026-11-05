using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// One rule for every marketplace (owner's decision, 5 Nov 2026): a product line that cannot be read does not stop the
// order. The package is kept for shipping without it and counts it; the line is never guessed (no 0 price or quantity);
// no invoice is drafted, approved or issued until a read fills it in. Pazarama, PTT AVM and n11 are covered beside their
// mapper tests; Trendyol beside MarketplaceInvoiceStatePolicyTests.
public sealed class UnreadableOrderLineTests
{
    [Fact]
    public void TrendyolPackageWithoutItsLinesIsCountedUnlessItIsASplitParent()
    {
        var missing = TrendyolJsonMapper.Orders("""{"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000}]}""");
        Assert.Equal(1, Assert.Single(Assert.Single(missing.Items).Packages).UnreadableLineCount);

        var parent = TrendyolJsonMapper.Orders("""{"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000,"currencyCode":"TRY","status":"UnPacked","lastModifiedDate":1760000000000}]}""");
        Assert.Equal(0, Assert.Single(Assert.Single(parent.Items).Packages).UnreadableLineCount);
    }

    [Fact]
    public void HepsiburadaOrderDetailLeavesOutALineWithoutPriceAndItsPackageCountsIt()
    {
        using var json = JsonDocument.Parse("""
        {
          "orderNumber": "HB-UNREAD-1",
          "orderDate": "2026-09-28T12:15:00Z",
          "items": [
            { "id": "line-1", "merchantSku": "SKU-1", "quantity": 1, "price": 10.0, "status": "Open" },
            { "id": "line-2", "merchantSku": "SKU-2", "quantity": 1, "status": "Open" }
          ],
          "packages": [
            { "packageNumber": "PKG-1", "status": "Packaged", "items": [ { "lineItemId": "line-1", "quantity": 1 }, { "lineItemId": "line-2", "quantity": 1 } ] }
          ]
        }
        """);

        var order = HepsiburadaJsonMapper.Order(json.RootElement, "HB-UNREAD-1");

        Assert.Equal("line-1", Assert.Single(order.Lines).ExternalLineId);
        var package = Assert.Single(order.Packages);
        Assert.Equal(1, package.UnreadableLineCount);
        Assert.Equal("line-1", Assert.Single(package.Allocations).ExternalLineId);
    }

    [Fact]
    public void HepsiburadaPackageListKeepsAPackageWithAnUnreadableLineInsteadOfFillingItWithZero()
    {
        using var json = JsonDocument.Parse("""
        {
          "packageNumber": "PKG-2",
          "orderNumber": "HB-UNREAD-2",
          "status": "Packaged",
          "lastStatusUpdateDate": "2026-09-28T12:15:00Z",
          "orderDate": "2026-09-28T12:00:00Z",
          "items": [
            { "id": "line-1", "lineItemId": "line-1", "merchantSku": "SKU-1", "quantity": 1, "price": 10.0 },
            { "id": "line-2", "lineItemId": "line-2", "merchantSku": "SKU-2", "price": 10.0 }
          ]
        }
        """);

        var mapped = HepsiburadaJsonMapper.OrderPackage(json.RootElement);

        Assert.Equal(1, mapped.Package.UnreadableLineCount);
        Assert.Equal("line-1", Assert.Single(mapped.Package.Allocations).ExternalLineId);
        Assert.Equal("line-1", Assert.Single(mapped.OrderSnapshot!.Lines).ExternalLineId);
    }

    [Fact]
    public void ShopifyLineWithoutAPriceIsLeftOutAndTheRemainderCountsIt()
    {
        var order = ShopifyMap(ShopifyLine(7, price: "100.00") + "," + ShopifyLine(8, price: null), "[]");

        Assert.Equal("7", Assert.Single(order.Lines).ExternalLineId);
        var remainder = Assert.Single(order.Packages);
        Assert.Equal(1, remainder.UnreadableLineCount);
        Assert.Equal("UNFULFILLED", remainder.RawStatus);
        Assert.Equal("7", Assert.Single(remainder.Allocations).ExternalLineId);
    }

    [Fact]
    public void ShopifyUnreadableLineOfAFulfillmentIsCountedOnThatFulfillment()
    {
        const string fulfillment = """
            [{"id":"gid://shopify/Fulfillment/11","status":"SUCCESS","displayStatus":"FULFILLED","deliveredAt":null,"createdAt":"2026-10-19T10:30:00Z","updatedAt":"2026-10-19T10:30:00Z",
              "trackingInfo":[],"events":{"nodes":[]},"fulfillmentLineItems":{"nodes":[
                {"id":"gid://shopify/FulfillmentLineItem/1","quantity":1,"lineItem":{"id":"gid://shopify/LineItem/7"}},
                {"id":"gid://shopify/FulfillmentLineItem/2","quantity":1,"lineItem":{"id":"gid://shopify/LineItem/8"}}]}}]
            """;

        var order = ShopifyMap(ShopifyLine(7, price: "100.00") + "," + ShopifyLine(8, price: null), fulfillment);

        var shipped = Assert.Single(order.Packages);
        Assert.Equal("11", shipped.ExternalPackageId);
        Assert.Equal(1, shipped.UnreadableLineCount);
        Assert.Equal("7", Assert.Single(shipped.Allocations).ExternalLineId);
    }

    [Fact]
    public void ShopifyOrderWithEveryLineReadCountsNone()
    {
        var order = ShopifyMap(ShopifyLine(7, price: "100.00"), "[]");
        Assert.All(order.Packages, package => Assert.Equal(0, package.UnreadableLineCount));
    }

    [Fact]
    public void TheInvoiceRefusalNamesTheMarketplaceAndIsRetriedOnlyByTheOwnersNewApproval()
    {
        var refusal = InvoiceIssuePolicy.OrderLineUnreadableFor("PAZARAMA");
        Assert.Equal("ORDER_LINE_UNREADABLE", refusal.Code);
        Assert.StartsWith("Pazarama", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Okunamayan Ürün Satırı", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Okunamayan Ürün Satırı", InvoiceIssuePolicy.DescribeValidationCode(InvoiceIssuePolicy.OrderLineUnreadableCode), StringComparison.Ordinal);
        // The last check stopped it before the provider: nothing was issued, and it waits for the owner's new approval.
        Assert.True(InvoicingBillingService.CanRetryPreProviderFailure(InvoiceStatus.ManualReview, InvoiceIssuePolicy.OrderLineUnreadableCode, null));
    }

    private static string ShopifyLine(int id, string? price) => price is null
        ? $$$"""{"id":"gid://shopify/LineItem/{{{id}}}","name":"Ürün","sku":"SKU-{{{id}}}","quantity":1,"currentQuantity":1,"originalUnitPriceSet":null,"variant":null}"""
        : $$$"""{"id":"gid://shopify/LineItem/{{{id}}}","name":"Ürün","sku":"SKU-{{{id}}}","quantity":1,"currentQuantity":1,"originalUnitPriceSet":{"shopMoney":{"amount":{{{price}}},"currencyCode":"TRY"}},"variant":null}""";

    private static RemoteOrder ShopifyMap(string lines, string fulfillments)
    {
        using var json = JsonDocument.Parse($$$"""
            {"id":"gid://shopify/Order/1","name":"#1001","createdAt":"2026-10-19T10:00:00Z","updatedAt":"2026-10-19T12:00:00Z","cancelledAt":null,
             "displayFinancialStatus":"PAID","email":null,"phone":null,
             "currentTotalPriceSet":{"shopMoney":{"amount":200.00,"currencyCode":"TRY"}},
             "totalDiscountsSet":{"shopMoney":{"amount":0.00,"currencyCode":"TRY"}},
             "lineItems":{"nodes":[{{{lines}}}]},
             "fulfillments":{{{fulfillments}}},"refunds":[]}
            """);
        return ShopifyHttpClient.MapOrder(json.RootElement);
    }
}
