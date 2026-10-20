using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaTurkeyTimeTests
{
    [Fact]
    public void ADateWithoutAnOffsetIsTurkeyTime()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 11, 19, 58, TimeSpan.Zero), HepsiburadaJsonMapper.TurkeyDate("2026-10-19T14:19:58"));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 11, 20, 0, 377, TimeSpan.Zero), HepsiburadaJsonMapper.TurkeyDate("2026-10-19T14:20:00.377"));
    }

    [Fact]
    public void ADateWithAnOffsetKeepsIt()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 14, 19, 58, TimeSpan.Zero), HepsiburadaJsonMapper.TurkeyDate("2026-10-19T14:19:58Z"));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 11, 19, 58, TimeSpan.Zero), HepsiburadaJsonMapper.TurkeyDate("2026-10-19T14:19:58+03:00"));
        Assert.Null(HepsiburadaJsonMapper.TurkeyDate("not a date"));
    }

    [Theory]
    [InlineData("2026-10-19T14:19:58Z", true)]
    [InlineData("2026-10-19T14:19:58+03:00", true)]
    [InlineData("2026-10-19T14:19:58-0500", true)]
    [InlineData("2026-10-19T14:19:58+03", true)]
    [InlineData("2026-10-19T14:19:58.377+03", true)]
    [InlineData("2026-10-19T14:19+03", true)]
    [InlineData("2026-10-19 14:19:58", false)]
    [InlineData("2026-10-19T14:19:58", false)]
    [InlineData("2026-10-19T14:19:58.377", false)]
    [InlineData("2026-10-19", false)]
    public void AnOffsetIsRecognisedWhateverTheServerTimeZone(string text, bool hasOffset) =>
        Assert.Equal(hasOffset, HepsiburadaJsonMapper.HasOffset(text));

    // G29: an hours-only offset is an offset, not Turkey wall-clock text.
    [Fact]
    public void AnHoursOnlyOffsetIsKept() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 11, 19, 58, TimeSpan.Zero), MarketplaceHub.Infrastructure.Adapters.TurkeyTime.Parse("2026-10-19T14:19:58+03"));

    // C13: the documented "GMT+3 epoch" reading, stated once and not applied to any request.
    [Fact]
    public void AGmtPlusThreeEpochIsTheTurkeyWallClockWrittenAsUtc()
    {
        var instant = new DateTimeOffset(2026, 10, 19, 11, 0, 0, TimeSpan.Zero);
        var epoch = MarketplaceHub.Infrastructure.Adapters.TurkeyTime.ToTurkeyWallClockEpochMilliseconds(instant);

        Assert.Equal(new DateTimeOffset(2026, 10, 19, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), epoch);
        Assert.Equal(instant, MarketplaceHub.Infrastructure.Adapters.TurkeyTime.FromTurkeyWallClockEpochMilliseconds(epoch));
    }

    [Fact]
    public void QueryDatesAreSentInTurkeyTime() =>
        Assert.Equal("2026-10-19 14:19", HepsiburadaJsonMapper.QueryTime(new DateTimeOffset(2026, 10, 19, 11, 19, 30, TimeSpan.Zero)));
}
