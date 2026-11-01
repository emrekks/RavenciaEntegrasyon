using System.Globalization;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyOrderAmountsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoFulfillmentsShareTheOrderTotalsInsteadOfEachCarryingThem()
    {
        var lines = new List<RemoteOrderLine> { Line("a", 100m), Line("b", 50m) };
        var packages = new List<RemotePackage> { Package("f1", ("a", 1m)), Package("f2", ("b", 1m)) };

        var split = ShopifyHttpClient.SplitOrderAmounts(packages, lines, gross: 150m, discount: 10m);

        Assert.Equal((100m, 6.67m, 93.33m), (split[0].GrossAmount, split[0].DiscountAmount, split[0].NetAmount));
        Assert.Equal((50m, 3.33m, 46.67m), (split[1].GrossAmount, split[1].DiscountAmount, split[1].NetAmount));
        Assert.Equal(150m, split.Sum(package => package.GrossAmount));
        Assert.Equal(10m, split.Sum(package => package.DiscountAmount));
        Assert.Equal(140m, split.Sum(package => package.NetAmount));
    }

    [Fact]
    public void ASinglePackageKeepsTheOrderTotalsAndACancelledRestHasNone()
    {
        var lines = new List<RemoteOrderLine> { Line("a", 100m) };
        var packages = new List<RemotePackage> { Package("f1", ("a", 1m)), Package("rest") };

        var split = ShopifyHttpClient.SplitOrderAmounts(packages, lines, gross: 100m, discount: 5m);

        Assert.Equal((100m, 5m, 95m), (split[0].GrossAmount, split[0].DiscountAmount, split[0].NetAmount));
        Assert.Equal((0m, 0m, 0m), (split[1].GrossAmount, split[1].DiscountAmount, split[1].NetAmount));
    }

    // A discounted order split over packages: each package's gross is its line value and gross - discount is its
    // payable, so the invoice calculation accepts every package (the gross used to be the net, and it stopped).
    [Fact]
    public void EachPackageOfADiscountedOrderPassesTheInvoiceCheck()
    {
        var lines = new List<RemoteOrderLine> { Line("a", 333.33m), Line("b", 199.99m), Line("c", 0.01m) };
        var packages = new List<RemotePackage> { Package("f1", ("a", 1m)), Package("f2", ("b", 1m), ("c", 1m)) };

        var split = ShopifyHttpClient.SplitOrderAmounts(packages, lines, gross: 533.33m, discount: 26.67m);

        Assert.Equal(533.33m, split.Sum(package => package.GrossAmount));
        Assert.Equal(26.67m, split.Sum(package => package.DiscountAmount));
        Assert.Equal(506.66m, split.Sum(package => package.NetAmount));
        foreach (var package in split)
        {
            var sources = package.Allocations.Select(allocation => new InvoicePackageLineSource(
                Guid.NewGuid(), "Ürün", allocation.ExternalLineId, allocation.AllocatedQuantity,
                lines.Single(line => line.ExternalLineId == allocation.ExternalLineId).UnitPrice, 10m)).ToArray();
            Assert.True(InvoiceAmounts.TryCalculatePackage(sources, package.GrossAmount, package.DiscountAmount, package.NetAmount, out _, out var error), error);
        }
    }

    // Deep audit 2026-10-20 S11 and S4: gross is before the discount (net + discount), and the
    // unfulfilled remainder of an open order is a new package, not one for manual review.
    [Fact]
    public void OrderGrossIsBeforeTheDiscountAndTheOpenRemainderIsNew()
    {
        var order = Map(net: 90m, originalDiscount: 10m, currentDiscount: 10m, [OrderLine(7, 1, 1, 100m)], "[]");

        Assert.Equal((100m, 10m, 90m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        var remainder = Assert.Single(order.Packages);
        Assert.Equal("UNFULFILLED", remainder.RawStatus);
        Assert.Equal(ShipmentPackageStatus.New, ShopifyShipmentPackageStatusPolicy.FromRemote(remainder.RawStatus));
        Assert.Equal((100m, 10m, 90m), (remainder.GrossAmount, remainder.DiscountAmount, remainder.NetAmount));
    }

    // A read without currentTotalDiscountsSet (an older snapshot) still uses the original discount.
    [Fact]
    public void WithoutTheCurrentDiscountTheOriginalOneIsUsed()
    {
        var order = Map(net: 90m, originalDiscount: 10m, currentDiscount: null, [OrderLine(7, 1, 1, 100m)], "[]");

        Assert.Equal((100m, 10m, 90m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
    }

    // A cancelled line takes its share of the discount with it: the current discount is paired with the current
    // total. With the original discount the gross read 110 for 100 of products and the invoice stopped.
    [Fact]
    public void APartialCancellationLeavesTheCancelledLinesDiscountOut()
    {
        var order = Map(net: 90m, originalDiscount: 20m, currentDiscount: 10m, [OrderLine(7, 1, 1, 100m), OrderLine(8, 1, 0, 100m)], "[]");

        Assert.Equal((100m, 10m, 90m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        Assert.Equal(1m, order.Lines.Single(line => line.ExternalLineId == "8").CancelledQuantity);
        var remainder = Assert.Single(order.Packages);
        Assert.Equal((100m, 10m, 90m), (remainder.GrossAmount, remainder.DiscountAmount, remainder.NetAmount));
        var sources = new[] { new InvoicePackageLineSource(Guid.NewGuid(), "Ürün", "SKU-7", 1m, 100m, 10m) };
        Assert.True(InvoiceAmounts.TryCalculatePackage(sources, remainder.GrossAmount, remainder.DiscountAmount, remainder.NetAmount, out _, out var error), error);
    }

    // A unit refunded after delivery is a return, not a cancellation: it stays in the sale (with its share of the
    // discount) and is written as returned on the delivered fulfillment.
    [Fact]
    public void AUnitRefundedAfterDeliveryIsReturnedNotCancelled()
    {
        var order = Map(net: 90m, originalDiscount: 20m, currentDiscount: 10m, [OrderLine(7, 2, 1, 100m)], Fulfillment(7, 2, delivered: true));

        var line = Assert.Single(order.Lines);
        Assert.Equal(0m, line.CancelledQuantity);
        Assert.Null(line.CancelledAt);
        var package = Assert.Single(order.Packages);
        Assert.Equal("DELIVERED", package.RawStatus);
        var allocation = Assert.Single(package.Allocations);
        Assert.Equal((2m, 0m, 2m, 2m, 1m), (allocation.AllocatedQuantity, allocation.CancelledQuantity, allocation.ShippedQuantity, allocation.DeliveredQuantity, allocation.ReturnedQuantity));
        Assert.Equal((200m, 20m, 180m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        Assert.Equal((200m, 20m, 180m), (package.GrossAmount, package.DiscountAmount, package.NetAmount));
    }

    // Of a line's removed units only those no fulfillment holds are cancelled.
    [Fact]
    public void RemovedUnitsBeyondTheFulfilledOnesAreCancelled()
    {
        var order = Map(net: 100m, originalDiscount: 0m, currentDiscount: 0m, [OrderLine(7, 3, 1, 100m)], Fulfillment(7, 1, delivered: true));

        Assert.Equal(2m, Assert.Single(order.Lines).CancelledQuantity);
        Assert.All(order.Packages.SelectMany(package => package.Allocations), allocation => Assert.Equal(0m, allocation.ReturnedQuantity));
        Assert.Equal((100m, 0m, 100m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        Assert.Equal(100m, order.Packages.Sum(package => package.GrossAmount));
    }

    // A refunded unit whose parcel is still on its way cannot be counted as returned (returned <= delivered); it
    // stays active and is not cancelled either.
    [Fact]
    public void AUnitRefundedBeforeDeliveryIsNeitherCancelledNorReturned()
    {
        var order = Map(net: 0m, originalDiscount: 0m, currentDiscount: 0m, [OrderLine(7, 1, 0, 100m)], Fulfillment(7, 1, delivered: false));

        Assert.Equal(0m, Assert.Single(order.Lines).CancelledQuantity);
        var allocation = Assert.Single(Assert.Single(order.Packages).Allocations);
        Assert.Equal((1m, 0m, 1m, 0m, 0m), (allocation.AllocatedQuantity, allocation.CancelledQuantity, allocation.ShippedQuantity, allocation.DeliveredQuantity, allocation.ReturnedQuantity));
        Assert.Equal((100m, 0m, 100m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
    }

    [Fact]
    public void TheOrderQueryCarriesBothEndsOfTheWindow()
    {
        var query = ShopifyHttpClient.BuildOrderQuery(new OrderPollWindow(At.AddDays(-1), At));
        Assert.Equal("updated_at:>='2026-10-18T12:00:00Z' updated_at:<='2026-10-19T12:00:00Z'", query);
    }

    [Fact]
    public void AStoppedReadResumesInItsOwnWindow()
    {
        var window = new ShopifyOrderHistoryPolicy.ResumableWindow(At.AddMonths(-3), At, 7, "eyJsYXN0X2lkIjo1fQ==:x");
        Assert.Equal(window, ShopifyOrderHistoryPolicy.Decode(ShopifyOrderHistoryPolicy.Encode(window)));
        Assert.Null(ShopifyOrderHistoryPolicy.Decode("eyJsYXN0X2lkIjo1fQ=="));
        var open = new ShopifyOrderHistoryPolicy.ResumableWindow(null, At, 0, null);
        Assert.Equal(open, ShopifyOrderHistoryPolicy.Decode(ShopifyOrderHistoryPolicy.Encode(open)));
    }

    private static RemoteOrderLine Line(string id, decimal price) => new(id, "SKU-" + id, null, "Ürün", 1, price, 0, "OPEN");

    private static RemotePackage Package(string id, params (string Line, decimal Quantity)[] allocations) =>
        new(id, null, "FULFILLED", At, null, null, [.. allocations.Select(allocation => new RemotePackageAllocation(allocation.Line, allocation.Quantity, 0, 0, 0, 0))]);

    private static string Amount(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string OrderLine(int id, int quantity, int currentQuantity, decimal price) =>
        $$$"""{"id":"gid://shopify/LineItem/{{{id}}}","name":"Ürün","sku":"SKU-{{{id}}}","quantity":{{{quantity}}},"currentQuantity":{{{currentQuantity}}},"originalUnitPriceSet":{"shopMoney":{"amount":{{{Amount(price)}}},"currencyCode":"TRY"}},"variant":null}""";

    private static string Fulfillment(int lineId, int quantity, bool delivered) =>
        $$$"""
        [{"id":"gid://shopify/Fulfillment/11","status":"SUCCESS","displayStatus":"{{{(delivered ? "DELIVERED" : "IN_TRANSIT")}}}","deliveredAt":{{{(delivered ? "\"2026-10-19T11:00:00Z\"" : "null")}}},"createdAt":"2026-10-19T10:30:00Z","updatedAt":"2026-10-19T11:00:00Z",
          "trackingInfo":[],"events":{"nodes":[]},"fulfillmentLineItems":{"nodes":[{"id":"gid://shopify/FulfillmentLineItem/1","quantity":{{{quantity}}},"lineItem":{"id":"gid://shopify/LineItem/{{{lineId}}}"}}]}}]
        """;

    private static RemoteOrder Map(decimal net, decimal originalDiscount, decimal? currentDiscount, string[] lines, string fulfillments)
    {
        var current = currentDiscount is { } value ? $$$"""
            "currentTotalDiscountsSet":{"shopMoney":{"amount":{{{Amount(value)}}},"currencyCode":"TRY"}},
            """ : string.Empty;
        using var json = System.Text.Json.JsonDocument.Parse($$$"""
            {"id":"gid://shopify/Order/1","name":"#1001","createdAt":"2026-10-19T10:00:00Z","updatedAt":"2026-10-19T12:00:00Z","cancelledAt":null,
             "displayFinancialStatus":"PAID","email":null,"phone":null,
             "currentTotalPriceSet":{"shopMoney":{"amount":{{{Amount(net)}}},"currencyCode":"TRY"}},
             "totalDiscountsSet":{"shopMoney":{"amount":{{{Amount(originalDiscount)}}},"currencyCode":"TRY"}},
             {{{current}}}
             "lineItems":{"nodes":[{{{string.Join(",", lines)}}}]},
             "fulfillments":{{{fulfillments}}},"refunds":[]}
            """);
        return ShopifyHttpClient.MapOrder(json.RootElement);
    }
}
