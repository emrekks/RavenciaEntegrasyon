using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaOrderHistoryPolicyTests
{
    [Fact]
    public void InitialOrderHistoryWindowUsesOneCalendarMonth()
    {
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");

        Assert.Equal(DateTimeOffset.Parse("2026-09-02T00:00:00Z"), HepsiburadaOrderHistoryPolicy.InitialWindowStart(anchor));
    }

    [Fact]
    public void IncrementalOrderWindowStaysInsideTheOneMonthBaseline()
    {
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var oldest = HepsiburadaOrderHistoryPolicy.InitialWindowStart(anchor);

        Assert.Equal(oldest, HepsiburadaOrderHistoryPolicy.ClampWindowStart(anchor, oldest.AddDays(-15)));
        Assert.Equal(anchor, HepsiburadaOrderHistoryPolicy.ClampWindowStart(anchor, anchor.AddDays(1)));
        Assert.Equal(anchor.AddDays(-2), HepsiburadaOrderHistoryPolicy.ClampWindowStart(anchor, anchor.AddDays(-2)));
    }

    [Fact]
    public void OnlyPaidOrdersFromTheLastMonthAreImported()
    {
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");

        Assert.False(HepsiburadaOrderHistoryPolicy.ShouldImportPaidOrder("Open", anchor.AddMonths(-1).AddSeconds(-1), anchor));
        Assert.True(HepsiburadaOrderHistoryPolicy.ShouldImportPaidOrder("Open", anchor.AddMonths(-1), anchor));
        Assert.False(HepsiburadaOrderHistoryPolicy.ShouldImportPaidOrder("Unpacked", anchor.AddMonths(-5), anchor));
        Assert.True(HepsiburadaOrderHistoryPolicy.ShouldImportPaidOrder("Unpacked", anchor.AddDays(-1), anchor));
        Assert.False(HepsiburadaOrderHistoryPolicy.ShouldImportPaidOrder(null, anchor, anchor));
        Assert.False(HepsiburadaOrderHistoryPolicy.ShouldImportPaidOrder("ClaimCreated", anchor, anchor));
    }

    [Fact]
    public void ClaimOnlyPaidOrdersWithoutPackagesAreNotImported()
    {
        var anchor = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        var line = new RemoteOrderLine("line-1", "sku", null, "Item", 1, 1, 0, "ClaimCreated");
        var order = new RemoteOrder("order-1", "order-1", anchor.AddDays(-1), anchor, "TRY", 1, 0, 1,
            "{}", "{}", "{}", [line], [], "{}", LifecycleStatus: "Unpacked");

        Assert.False(HepsiburadaOrderHistoryPolicy.ShouldImportPaidOrder(order, anchor));
        Assert.True(HepsiburadaOrderHistoryPolicy.ShouldImportPaidOrder(order with
        {
            Lines = [line with { RawStatus = "Unpacked" }]
        }, anchor));
    }
}
