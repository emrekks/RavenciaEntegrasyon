using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-11-15 FATURA-FINANS-5: the one rounding of money amounts rounds half a kuruş up (away from zero), as the
// invoice amounts and the commission tariff's price calculator did on their own before.
public sealed class MoneyRoundingTests
{
    [Theory]
    [InlineData("12.345", "12.35")]
    [InlineData("12.355", "12.36")]
    [InlineData("-12.345", "-12.35")]
    [InlineData("53.8575", "53.86")]
    [InlineData("15.025", "15.03")]
    [InlineData("10", "10")]
    public void Round_takes_half_a_kurus_up(string value, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), MoneyRounding.Round(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Rate_keeps_four_places_and_takes_the_half_up() =>
        Assert.Equal(0.1235m, MoneyRounding.Rate(0.12345m));

    // The sites moved onto the helper rounded away from zero already, so their figures stay the same.
    [Theory]
    [InlineData("0.005")]
    [InlineData("1.115")]
    [InlineData("250.505")]
    [InlineData("-3.335")]
    public void Round_is_the_away_from_zero_rounding_the_moved_sites_used(string value)
    {
        var amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(decimal.Round(amount, 2, MidpointRounding.AwayFromZero), MoneyRounding.Round(amount));
    }
}
