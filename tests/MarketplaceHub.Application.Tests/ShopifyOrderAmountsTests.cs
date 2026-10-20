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

        var split = ShopifyHttpClient.SplitOrderAmounts(packages, lines, total: 140m, discount: 10m);

        Assert.Equal(140m, split.Sum(package => package.NetAmount));
        Assert.Equal(10m, split.Sum(package => package.DiscountAmount));
        Assert.Equal(93.33m, split[0].NetAmount);
        Assert.Equal(46.67m, split[1].NetAmount);
    }

    [Fact]
    public void ASinglePackageKeepsTheOrderTotalsAndACancelledRestHasNone()
    {
        var lines = new List<RemoteOrderLine> { Line("a", 100m) };
        var packages = new List<RemotePackage> { Package("f1", ("a", 1m)), Package("rest") };

        var split = ShopifyHttpClient.SplitOrderAmounts(packages, lines, total: 118m, discount: 5m);

        Assert.Equal((118m, 5m, 118m), (split[0].GrossAmount, split[0].DiscountAmount, split[0].NetAmount));
        Assert.Equal(0m, split[1].NetAmount);
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
}
