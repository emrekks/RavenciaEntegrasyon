using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReportGeographyTests
{
    [Theory]
    [InlineData("SA", "TR", "SA")]
    [InlineData("TR", "TR", "TR")]
    [InlineData(null, "tr", "TR")]
    [InlineData(null, "DE", "DE")]
    [InlineData(null, null, "TR")]
    [InlineData(" az ", null, "AZ")]
    [InlineData("TRENDYOL", "", "TR")]
    public void An_order_goes_to_its_storefront_country_else_its_address_country_else_turkey(string? storeFront, string? address, string expected) =>
        Assert.Equal(expected, ReportBuckets.Country(storeFront, address));

    [Fact]
    public void A_cross_border_storefront_order_counts_in_its_country_and_not_in_istanbul()
    {
        var store = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 15);
        var start = new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);
        var at = start.AddHours(12);
        var domestic = Guid.NewGuid();
        var saudi = Guid.NewGuid();
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [
                new() { Id = domestic, ConnectionId = store, OrderNumber = "1", DerivedStatus = "DELIVERED", Amount = 100m, LineValue = 100m, RealAt = at, OrderedAt = at, StoreFrontCode = "TR" },
                new() { Id = saudi, ConnectionId = store, OrderNumber = "2", DerivedStatus = "DELIVERED", Amount = 300m, LineValue = 300m, RealAt = at, OrderedAt = at, StoreFrontCode = "SA" }
            ],
            new Dictionary<Guid, ReportOrderExtra>
            {
                [domestic] = new() { Id = domestic, City = "Ankara", CountryCode = "TR" },
                // Trendyol's export depot.
                [saudi] = new() { Id = saudi, City = "İstanbul", CountryCode = "TR" }
            },
            [], new Dictionary<Guid, ReportLineExtra>(), [], [], new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        var previous = input with { Period = new ReportPeriod(day.AddDays(-1), day.AddDays(-1), start.AddDays(-1), start), Orders = [] };

        var report = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());

        var city = Assert.Single(report.Cities);
        Assert.Equal("Ankara", city.Label);
        Assert.Equal(0, report.Summary.OrdersWithoutCity);
        Assert.Equal(1, report.Geography.AbroadOrders);
        Assert.Equal(["SA", "TR"], report.Geography.Countries.Select(x => x.Key).Order().ToArray());
        var abroad = report.Geography.Countries.Single(x => x.Key == "SA");
        Assert.Equal("Suudi Arabistan", abroad.Label);
        Assert.Equal(300m, abroad.NetRevenue);
        Assert.Empty(report.Geography.PreviousCities);
        Assert.Contains(report.DataNotes, note => note.Contains("1 sipariş yurt dışına gitti (Suudi Arabistan 1)", StringComparison.Ordinal));
    }
}
