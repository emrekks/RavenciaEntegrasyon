using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Trendyol's orderDate is a "GMT+3" epoch (Turkey wall-clock time written as if it were UTC); its other dates are real
// UTC epochs. Live 2 Nov 2026: order 11700000011 arrived 19:01:53Z, orderDate 22:01:41Z, lastModifiedDate 19:01:46Z,
// agreedDeliveryDate 20:59Z (23:59 Istanbul).
public sealed class TrendyolOrderTimeTests
{
    private const long OrderDate = 1793656901397;        // 2026-11-02T22:01:41.397 Turkey wall clock
    private const long LastModifiedDate = 1793646106033; // 2026-11-02T19:01:46.033Z
    private const long AgreedDeliveryDate = 1793739540000; // 2026-11-03T20:59:00Z

    [Fact]
    public void OrderDateIsReadAsTurkeyWallClockAndTheOtherDatesAsUtc()
    {
        var json = $$"""
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","currencyCode":"TRY","status":"Created","orderDate":{{OrderDate}},"lastModifiedDate":{{LastModifiedDate}},
          "agreedDeliveryDate":{{AgreedDeliveryDate}},"lines":[
          {"lineId":"1","stockCode":"SKU-1","productName":"Bluz","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10}
        ]}]}
        """;

        var order = Assert.Single(TrendyolJsonMapper.Orders(json).Items);

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 19, 1, 41, 397, TimeSpan.Zero), order.OrderedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(LastModifiedDate), order.LastModifiedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(AgreedDeliveryDate), order.ShipmentDueAt);
        // Placed before it was last changed, as a real order is.
        Assert.True(order.OrderedAt <= order.LastModifiedAt);
    }

    [Fact]
    public void WithoutALastChangeThePackageTimeFallsBackToTheRealOrderTime()
    {
        var json = $$"""
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","currencyCode":"TRY","status":"Created","orderDate":{{OrderDate}},"lines":[
          {"lineId":"1","stockCode":"SKU-1","productName":"Bluz","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10}
        ]}]}
        """;

        var order = Assert.Single(TrendyolJsonMapper.Orders(json).Items);

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 19, 1, 41, 397, TimeSpan.Zero), order.OrderedAt);
        Assert.Equal(order.OrderedAt, order.LastModifiedAt);
    }

    // ADAPTOR-TY-HB-5: an epoch outside the DateTimeOffset range drops only its own package, never the whole page. A broken
    // lastModifiedDate falls back to the order date; a broken orderDate refuses that package with an issue.
    [Fact]
    public void AnOutOfRangeEpochDropsOnlyItsOwnPackage()
    {
        const long Broken = 99999999999999999;
        var json = $$"""
        {"content":[
          {"id":"pkg-1","orderNumber":"ord-1","currencyCode":"TRY","status":"Created","orderDate":{{OrderDate}},"lastModifiedDate":{{Broken}},"lines":[
            {"lineId":"1","stockCode":"SKU-1","productName":"Bluz","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10}]},
          {"id":"pkg-2","orderNumber":"ord-2","currencyCode":"TRY","status":"Created","orderDate":{{Broken}},"lines":[
            {"lineId":"2","stockCode":"SKU-2","productName":"Bluz","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10}]},
          {"id":"pkg-3","orderNumber":"ord-3","currencyCode":"TRY","status":"Created","orderDate":{{OrderDate}},"lastModifiedDate":{{LastModifiedDate}},"lines":[
            {"lineId":"3","stockCode":"SKU-3","productName":"Bluz","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10}]}
        ]}
        """;

        var page = TrendyolJsonMapper.Orders(json);

        Assert.Equal(["ord-1", "ord-3"], page.Items.Select(order => order.OrderNumber).ToArray());
        Assert.Equal(page.Items[0].OrderedAt, page.Items[0].LastModifiedAt);
        var issue = Assert.Single(page.Issues!);
        Assert.Equal("ORDER_PACKAGE_INVALID", issue.Code);
        Assert.Equal("ord-2", issue.RereadOrderId);
    }

    [Fact]
    public void AReturnClaimProjectionReadsTheOrderDateAsTurkeyWallClockAndTheClaimDateAsUtc()
    {
        var withOrderDate = TrendyolJsonMapper.OrderFromReturnClaim($$"""{"orderNumber":"ord-1","orderDate":{{OrderDate}},"claimDate":{{LastModifiedDate}},"items":[]}""");
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 19, 1, 41, 397, TimeSpan.Zero), withOrderDate?.OrderedAt);

        // ADAPTOR-TY-HB-1: the claim's date is not when the order was placed; without the order's own date no order is built.
        var claimDateOnly = TrendyolJsonMapper.OrderFromReturnClaim($$"""{"orderNumber":"ord-1","claimDate":{{LastModifiedDate}},"items":[]}""");
        Assert.Null(claimDateOnly);
    }
}
