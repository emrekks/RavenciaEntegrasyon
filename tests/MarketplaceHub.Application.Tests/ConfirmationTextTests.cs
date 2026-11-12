using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ConfirmationTextTests
{
    [Theory]
    [InlineData("Verileri sil", "Verileri sil", true)]
    [InlineData("  verileri SIL  ", "Verileri sil", true)]
    [InlineData("VERİLERİ SİL", "Verileri sil", true)]
    [InlineData("TRENDYOL", "Trendyol", true)]
    [InlineData("HEPSİBURADA", "Hepsiburada", true)]
    [InlineData("hepsiburada ", "Hepsiburada", true)]
    [InlineData("Trendyol E", "Trendyol", false)]
    [InlineData("", "Trendyol", false)]
    [InlineData(null, "Trendyol", false)]
    public void IgnoresCaseAndTheSpacesAtTheEnds(string? typed, string expected, bool matches) =>
        Assert.Equal(matches, ConfirmationText.Matches(typed, expected));
}
