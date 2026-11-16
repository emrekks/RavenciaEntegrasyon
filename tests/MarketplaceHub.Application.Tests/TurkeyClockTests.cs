using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Imports;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ADAPTOR-DIGER-8: one Turkey offset; the policies, the import parsers and the adapters' TurkeyTime all read it.
public sealed class TurkeyClockTests
{
    [Fact]
    public void EveryTurkeyOffsetIsTheSharedOne()
    {
        Assert.Equal(TimeSpan.FromHours(3), TurkeyClock.Offset);
        Assert.Equal(TurkeyClock.Offset, TurkeyTime.Offset);
        Assert.Equal(TurkeyClock.Offset, PazaramaOrderHistoryPolicy.TurkeyOffset);
        Assert.Equal(TurkeyClock.Offset, PttAvmOrderHistoryPolicy.TurkeyOffset);
        Assert.Equal(TurkeyClock.Offset, DailyScheduleWindowPolicy.IstanbulOffset);
        Assert.Equal(TurkeyClock.Offset, ListingVerificationPolicy.IstanbulOffset);
        Assert.Equal(TurkeyClock.Offset, CommissionTariffFileParser.TurkeyOffset);
        Assert.Equal(TurkeyClock.Offset, N11OrderHistoryPolicy.EarliestAvailable.Offset);
    }

    [Theory]
    [InlineData("2026-11-14T20:59:59Z", 2026, 11, 14)]
    [InlineData("2026-11-14T21:00:00Z", 2026, 11, 15)]
    public void TheTurkeyDayTurnsAt21Utc(string instant, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), TurkeyTime.Day(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)));
}
