using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyOrderHistoryPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T12:00:00Z");

    [Fact]
    public void FullScanRemovesTheUpdatedAtFilter()
    {
        var modifiedAfter = ShopifyOrderHistoryPolicy.ModifiedAfter(
            fullScan: true,
            allowBaseline: false,
            lastModifiedWatermark: Now.AddDays(-1),
            lastSuccessAt: Now.AddDays(-1),
            now: Now);

        Assert.Null(modifiedAfter);
    }

    [Fact]
    public void InitialBaselineCoversThreeCalendarMonths()
    {
        var modifiedAfter = ShopifyOrderHistoryPolicy.ModifiedAfter(
            fullScan: false,
            allowBaseline: true,
            lastModifiedWatermark: null,
            lastSuccessAt: null,
            now: Now);

        Assert.Equal(Now.AddMonths(-3), modifiedAfter);
    }

    [Fact]
    public void IncrementalScanKeepsItsOverlapWatermark()
    {
        var watermark = Now.AddMinutes(-5);

        var modifiedAfter = ShopifyOrderHistoryPolicy.ModifiedAfter(
            fullScan: false,
            allowBaseline: false,
            lastModifiedWatermark: watermark,
            lastSuccessAt: Now.AddDays(-1),
            now: Now);

        Assert.Equal(watermark.AddMinutes(-10), modifiedAfter);
    }

    [Fact]
    public void OlderCsvOrdersExplainTheAdditionalShopifyAccessNeeded()
    {
        Assert.True(ShopifyOrderHistoryPolicy.RequiresAllOrdersScope(Now.AddDays(-61), Now));
        Assert.False(ShopifyOrderHistoryPolicy.RequiresAllOrdersScope(Now.AddDays(-60), Now));
        Assert.False(ShopifyOrderHistoryPolicy.RequiresAllOrdersScope(Now.AddDays(-59), Now));
        Assert.Equal(1, ShopifyOrderHistoryPolicy.CountOrdersRequiringAllOrdersScope(
            [Now.AddDays(-61), Now.AddDays(-60), null, Now.AddDays(-1)], Now));

        var notice = ShopifyOrderHistoryPolicy.HistoricalOrdersScopeNotice(4);

        Assert.Contains("4 eşleşmeyen sipariş", notice, StringComparison.Ordinal);
        Assert.Contains("read_all_orders", notice, StringComparison.Ordinal);
        Assert.Contains("Erişilebilir tüm siparişleri tara", notice, StringComparison.Ordinal);
    }
}
