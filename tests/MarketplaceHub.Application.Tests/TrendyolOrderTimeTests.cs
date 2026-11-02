using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Trendyol's orderDate is a "GMT+3" epoch (Turkey wall-clock time written as if it were UTC); its other dates are real
// UTC epochs. Live 2 Nov 2026: order 11776722829 arrived 19:01:53Z, orderDate 22:01:41Z, lastModifiedDate 19:01:46Z,
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
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Created","orderDate":{{OrderDate}},"lastModifiedDate":{{LastModifiedDate}},
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
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Created","orderDate":{{OrderDate}},"lines":[
          {"lineId":"1","stockCode":"SKU-1","productName":"Bluz","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10}
        ]}]}
        """;

        var order = Assert.Single(TrendyolJsonMapper.Orders(json).Items);

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 19, 1, 41, 397, TimeSpan.Zero), order.OrderedAt);
        Assert.Equal(order.OrderedAt, order.LastModifiedAt);
    }

    [Fact]
    public void AReturnClaimProjectionReadsTheOrderDateAsTurkeyWallClockAndTheClaimDateAsUtc()
    {
        var withOrderDate = TrendyolJsonMapper.OrderFromReturnClaim($$"""{"orderNumber":"ord-1","orderDate":{{OrderDate}},"claimDate":{{LastModifiedDate}},"items":[]}""");
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 19, 1, 41, 397, TimeSpan.Zero), withOrderDate?.OrderedAt);

        var claimDateOnly = TrendyolJsonMapper.OrderFromReturnClaim($$"""{"orderNumber":"ord-1","claimDate":{{LastModifiedDate}},"items":[]}""");
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(LastModifiedDate), claimDateOnly?.OrderedAt);
    }
}
