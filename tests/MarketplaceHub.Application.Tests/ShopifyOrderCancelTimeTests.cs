using System.Text.Json;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyOrderCancelTimeTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CancelledAt = new(2026, 10, 19, 11, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ACancelledOrderDatesItsCancelledLinesAndRemainderWithItsCancelledAt()
    {
        var order = Map("\"2026-10-19T14:30:00+03:00\"", Line(7, 2, 0), "[]");

        var line = Assert.Single(order.Lines);
        Assert.Equal(2m, line.CancelledQuantity);
        Assert.Equal(CancelledAt, line.CancelledAt);
        var remainder = Assert.Single(order.Packages);
        Assert.Equal("CANCELLED", remainder.RawStatus);
        Assert.Equal(CancelledAt, remainder.CancelledAt);
        // The package's status time is unchanged: it is still the order's updatedAt.
        Assert.Equal(UpdatedAt, remainder.OccurredAt);
        Assert.Equal(CancelledAt, OrderLineCancelTime.Reported("7", order));
        Assert.Equal(TimeSpan.Zero, OrderLineCancelTime.Reported("7", order)!.Value.Offset);
    }

    [Fact]
    public void InACancelledOrderALineWithNothingCancelledHasNoCancellationTime()
    {
        var order = Map("\"2026-10-19T11:30:00Z\"", Line(7, 1, 1), FulfilledLine7);

        var line = Assert.Single(order.Lines);
        Assert.Equal(0m, line.CancelledQuantity);
        Assert.Null(line.CancelledAt);
        Assert.All(order.Packages, package => Assert.Null(package.CancelledAt));
    }

    [Fact]
    public void ALineCutWhileTheOrderStaysOpenHasNoCancellationTime()
    {
        var order = Map("null", Line(7, 3, 1), "[]");

        var line = Assert.Single(order.Lines);
        Assert.Equal(2m, line.CancelledQuantity);
        Assert.Null(line.CancelledAt);
        var remainder = Assert.Single(order.Packages);
        Assert.Equal("UNFULFILLED", remainder.RawStatus);
        Assert.Null(remainder.CancelledAt);
        Assert.Null(OrderLineCancelTime.Reported("7", order));
    }

    [Fact]
    public void ACancelledFulfillmentIsNotALineCancellationAndCarriesNoTime()
    {
        var order = Map("null", Line(7, 1, 1), CancelledFulfillmentLine7);

        var fulfillment = Assert.Single(order.Packages, package => package.ExternalPackageId == "11");
        Assert.Equal("CANCELLED", fulfillment.RawStatus);
        Assert.Null(fulfillment.CancelledAt);
        Assert.Null(Assert.Single(order.Lines).CancelledAt);
    }

    private const string FulfilledLine7 = """
        [{"id":"gid://shopify/Fulfillment/11","status":"SUCCESS","displayStatus":"FULFILLED","deliveredAt":null,"createdAt":"2026-10-19T10:30:00Z","updatedAt":"2026-10-19T10:30:00Z",
          "trackingInfo":[],"events":{"nodes":[]},"fulfillmentLineItems":{"nodes":[{"id":"gid://shopify/FulfillmentLineItem/1","quantity":1,"lineItem":{"id":"gid://shopify/LineItem/7"}}]}}]
        """;

    private const string CancelledFulfillmentLine7 = """
        [{"id":"gid://shopify/Fulfillment/11","status":"CANCELLED","displayStatus":"CANCELED","deliveredAt":null,"createdAt":"2026-10-19T10:30:00Z","updatedAt":"2026-10-19T11:00:00Z",
          "trackingInfo":[],"events":{"nodes":[]},"fulfillmentLineItems":{"nodes":[{"id":"gid://shopify/FulfillmentLineItem/1","quantity":1,"lineItem":{"id":"gid://shopify/LineItem/7"}}]}}]
        """;

    private static string Line(int id, int quantity, int currentQuantity) =>
        $$$"""{"id":"gid://shopify/LineItem/{{{id}}}","name":"Ürün","sku":"SKU-{{{id}}}","quantity":{{{quantity}}},"currentQuantity":{{{currentQuantity}}},"originalUnitPriceSet":{"shopMoney":{"amount":100.00,"currencyCode":"TRY"}},"variant":null}""";

    private static MarketplaceHub.Application.RemoteOrder Map(string cancelledAt, string line, string fulfillments)
    {
        using var json = JsonDocument.Parse($$$"""
            {"id":"gid://shopify/Order/1","name":"#1001","createdAt":"2026-10-19T10:00:00Z","updatedAt":"2026-10-19T12:00:00Z","cancelledAt":{{{cancelledAt}}},
             "displayFinancialStatus":"PAID","email":null,"phone":null,
             "currentTotalPriceSet":{"shopMoney":{"amount":0.00,"currencyCode":"TRY"}},
             "totalDiscountsSet":{"shopMoney":{"amount":0.00,"currencyCode":"TRY"}},
             "lineItems":{"nodes":[{{{line}}}]},
             "fulfillments":{{{fulfillments}}},"refunds":[]}
            """);
        return ShopifyHttpClient.MapOrder(json.RootElement);
    }
}
