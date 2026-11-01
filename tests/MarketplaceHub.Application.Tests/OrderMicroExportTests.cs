using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OrderMicroExportTests
{
    // The country the row's flag shows: the invoice address's, else the storefront's; TR is not a country of export.
    [Fact]
    public void MicroExportOrdersAreCountedPerCountryAsTheRowFlagShowsThem()
    {
        var counts = MarketplaceSalesService.MicroExportCountryCounts([
            ("""{"countryCode":"SA"}""", null),
            ("""{"countryCode":"sa"}""", "SA"),
            ("""{"countryCode":"BG"}""", "TR"),
            // No invoice country: the storefront says it.
            ("{}", "RO"),
            // A domestic address and a domestic storefront: the country is not known.
            ("""{"countryCode":"TR"}""", "TR"),
            ("not json", null),
        ]);

        Assert.Equal(2, counts["SA"]);
        Assert.Equal(1, counts["BG"]);
        Assert.Equal(1, counts["RO"]);
        Assert.Equal(2, counts[""]);
        Assert.Equal(6, counts.Values.Sum());
    }

    [Fact]
    public void NoMicroExportOrdersGiveNoCountries()
    {
        Assert.Empty(MarketplaceSalesService.MicroExportCountryCounts([]));
    }
}
