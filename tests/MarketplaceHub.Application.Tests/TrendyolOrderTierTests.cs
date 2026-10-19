using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolOrderTierTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartsAtTheConfiguredFloorAndStepsUpUnderSustainedPressure()
    {
        var tier = new TrendyolOrderTier();
        Assert.Equal(30, tier.Limit(30));

        for (var i = 0; i < TrendyolOrderTier.PressureToStepUp - 1; i++) Assert.False(tier.RecordPressure(Start.AddSeconds(i)));
        Assert.True(tier.RecordPressure(Start.AddSeconds(30)));
        Assert.Equal(40, tier.Limit(30));
    }

    [Fact]
    public void ScatteredWaitsDoNotRaiseTheTier()
    {
        var tier = new TrendyolOrderTier();
        for (var i = 0; i < 60; i++) tier.RecordPressure(Start.AddMinutes(i * 11));

        Assert.Equal(30, tier.Limit(30));
    }

    [Fact]
    public void A429StepsBackAndHoldsForADay()
    {
        var tier = new TrendyolOrderTier();
        Press(tier, Start);
        Assert.Equal(40, tier.Limit(30));

        Assert.True(tier.RecordRateLimited(Start.AddMinutes(5)));
        Assert.Equal(30, tier.Limit(30));
        Press(tier, Start.AddHours(2));
        Assert.Equal(30, tier.Limit(30));
        Press(tier, Start.AddDays(1).AddMinutes(10));
        Assert.Equal(40, tier.Limit(30));
    }

    [Fact]
    public void NeverGoesBelowTheFloorOrAboveTheTopTier()
    {
        var tier = new TrendyolOrderTier();
        Assert.False(tier.RecordRateLimited(Start));
        Assert.Equal(50, tier.Limit(50));
        var at = Start.AddDays(2);
        for (var step = 0; step < 6; step++)
        {
            Press(tier, at);
            at = at.AddHours(1);
        }
        Assert.Equal(100, tier.Limit(30));
    }

    private static void Press(TrendyolOrderTier tier, DateTimeOffset at)
    {
        for (var i = 0; i < TrendyolOrderTier.PressureToStepUp; i++) tier.RecordPressure(at.AddSeconds(i));
    }
}
