using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TurkeyTimeTests
{
    [Theory]
    [InlineData("2026-11-14T20:59:59Z", 2026, 11, 14)]
    [InlineData("2026-11-14T21:00:00Z", 2026, 11, 15)]
    public void DayFollowsTheTurkeyMidnight(string instant, int year, int month, int day)
    {
        var value = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new DateOnly(year, month, day), TurkeyTime.Day(value));
        Assert.Equal(new DateTime(year, month, day), TurkeyTime.LocalDate(value));
        Assert.Equal(EfaturamTurkeyTime.Day(value), TurkeyTime.Day(value));
    }

    [Fact]
    public void FromLocalReadsAWallClockAsTurkeyTime()
    {
        var utc = TurkeyTime.FromLocal(new DateTime(2026, 11, 15, 1, 30, 0, DateTimeKind.Unspecified));
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 22, 30, 0, TimeSpan.Zero), utc);
        Assert.Equal(TimeSpan.Zero, utc.Offset);
        // The kind of the parsed value never changes the reading.
        Assert.Equal(utc, TurkeyTime.FromLocal(new DateTime(2026, 11, 15, 1, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void FormatWritesTheTurkeyWallClock()
    {
        var value = new DateTimeOffset(2026, 11, 14, 21, 5, 0, TimeSpan.Zero);
        Assert.Equal("2026-11-15 00:05", TurkeyTime.Format(value, "yyyy-MM-dd HH:mm"));
        Assert.Equal(TurkeyTime.Format(value, "dd/MM/yyyy"), TurkeyTime.FormatWithZoneRules(value, "dd/MM/yyyy"));
        Assert.Equal(TurkeyTime.Offset, EfaturamTurkeyTime.Offset);
    }
}
