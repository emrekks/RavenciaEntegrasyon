using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan C-13 (13 Nov 2026): a Hepsiburada line a page carries twice takes the status of its newest copy (not the
// alphabetically last one), and the duplicate copy adds no second discount; packages of one order still add up.
public sealed class HepsiburadaOrderLineMergeTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ALineSeenTwice_TakesItsNewestStatus_AndItsDiscountOnce()
    {
        var shipped = Order(At, 10m, Line("L1", "Shipped"));
        var delivered = Order(At.AddHours(5), 10m, Line("L1", "Delivered"));

        var merged = MarketplaceJobProcessor.MergeHepsiburadaOrderLines([shipped, delivered]);

        Assert.Equal("Delivered", Assert.Single(merged.Lines).RawStatus);
        Assert.Equal(10m, merged.DiscountAmount);
        Assert.Equal(90m, merged.NetAmount);
    }

    [Fact]
    public void TwoPackagesOfOneOrder_KeepBothLinesAndBothDiscounts()
    {
        var first = Order(At, 10m, Line("L1", "Shipped"));
        var second = Order(At.AddMinutes(1), 5m, Line("L2", "Shipped"));

        var merged = MarketplaceJobProcessor.MergeHepsiburadaOrderLines([first, second]);

        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal(15m, merged.DiscountAmount);
    }

    private static RemoteOrderLine Line(string id, string status) => new(id, $"SKU-{id}", null, "Ürün", 1m, 100m, 20m, status);

    private static RemoteOrder Order(DateTimeOffset modifiedAt, decimal discount, params RemoteOrderLine[] lines) =>
        new("HB-1", "HB-1", At, modifiedAt, "TRY", 100m * lines.Length, discount, 100m * lines.Length - discount, "{}", "{}", "{}", lines, [], "{}");
}
