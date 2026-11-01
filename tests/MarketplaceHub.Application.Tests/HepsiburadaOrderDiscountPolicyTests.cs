using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaOrderDiscountPolicyTests
{
    [Fact]
    public void DiscountStaysAsReportedWhenNothingIsCancelled()
    {
        var discount = HepsiburadaOrderDiscountPolicy.RemainingDiscount(30m, [(100m, 1m, 0m), (50m, 2m, 0m)]);

        Assert.Equal(30m, discount);
    }

    [Fact]
    public void CancelledLineTakesItsShareOfTheDiscount()
    {
        // 200 ordered, the 50 line is cancelled: 150 of 200 remains.
        var discount = HepsiburadaOrderDiscountPolicy.RemainingDiscount(20m, [(150m, 1m, 0m), (50m, 1m, 1m)]);

        Assert.Equal(15m, discount);
    }

    [Fact]
    public void CancelledQuantityTakesItsShareOfTheDiscount()
    {
        // 3 x 100 ordered, 1 cancelled: 200 of 300 remains.
        var discount = HepsiburadaOrderDiscountPolicy.RemainingDiscount(10m, [(100m, 3m, 1m)]);

        Assert.Equal(6.67m, discount);
    }

    [Fact]
    public void FullyCancelledReadLeavesNoDiscount()
    {
        var discount = HepsiburadaOrderDiscountPolicy.RemainingDiscount(25m, [(100m, 1m, 1m), (40m, 2m, 2m)]);

        Assert.Equal(0m, discount);
    }

    [Fact]
    public void DiscountStaysAsReportedWhenNoLineValueIsKnown()
    {
        Assert.Equal(12m, HepsiburadaOrderDiscountPolicy.RemainingDiscount(12m, []));
        Assert.Equal(12m, HepsiburadaOrderDiscountPolicy.RemainingDiscount(12m, [(0m, 1m, 1m)]));
    }

    [Fact]
    public void NoDiscountStaysZero()
    {
        Assert.Equal(0m, HepsiburadaOrderDiscountPolicy.RemainingDiscount(0m, [(100m, 2m, 1m)]));
    }
}
