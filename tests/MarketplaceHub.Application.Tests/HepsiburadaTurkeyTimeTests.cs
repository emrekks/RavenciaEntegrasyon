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

    [Fact]
    public void QueryDatesAreSentInTurkeyTime() =>
        Assert.Equal("2026-10-19 14:19", HepsiburadaJsonMapper.QueryTime(new DateTimeOffset(2026, 10, 19, 11, 19, 30, TimeSpan.Zero)));
}
