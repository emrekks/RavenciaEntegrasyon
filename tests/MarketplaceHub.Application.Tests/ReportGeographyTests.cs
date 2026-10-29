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

    // The names seen in the live store (23 Oct 2026), one company each.
    [Theory]
    [InlineData("PTT Kargo Marketplace", "PTT Kargo")]
    [InlineData("PTT Kargo", "PTT Kargo")]
    [InlineData("ptt", "PTT Kargo")]
    [InlineData("PTT Posta", "PTT Kargo")]
    [InlineData("Aras Kargo Marketplace", "Aras Kargo")]
    [InlineData("Aras Kargo", "Aras Kargo")]
    [InlineData("Trendyol Express Marketplace", "Trendyol Express")]
    [InlineData("hepsiJET", "HepsiJET")]
    [InlineData("Yurtiçi Kargo", "Yurtiçi Kargo")]
    [InlineData("YURTICI KARGO", "Yurtiçi Kargo")]
    [InlineData("FANEX", "FANEX")]
    [InlineData("Other", "Diğer")]
    [InlineData("Bilinmeyen Firma Marketplace", "Bilinmeyen Firma")]
    [InlineData("***", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void A_cargo_company_has_one_name_whatever_marketplace_wrote_it(string? raw, string? expected) =>
        Assert.Equal(expected, ReportBuckets.CargoCompany(raw));

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

    // A Trendyol order built from a return claim whose storefront no read named (getClaims has none; its address is the
    // Istanbul export depot): its country is unknown, so it is neither in Istanbul nor abroad. A claim-built order whose
    // storefront was read counts in that storefront's country.
    [Fact]
    public void A_claim_built_trendyol_order_without_a_storefront_has_no_country_and_no_city()
    {
        var store = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 15);
        var start = new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);
        var at = start.AddHours(12);
        var unknown = Guid.NewGuid();
        var read = Guid.NewGuid();
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [
                new() { Id = unknown, ConnectionId = store, OrderNumber = "1", DerivedStatus = "DELIVERED", Amount = 300m, LineValue = 300m, RealAt = at, OrderedAt = at },
                new() { Id = read, ConnectionId = store, OrderNumber = "2", DerivedStatus = "DELIVERED", Amount = 200m, LineValue = 200m, RealAt = at, OrderedAt = at, StoreFrontCode = "AZ" }
            ],
            new Dictionary<Guid, ReportOrderExtra>
            {
                [unknown] = new() { Id = unknown, City = "İstanbul", CountryCode = "TR", ClaimBuilt = true },
                [read] = new() { Id = read, City = "İstanbul", CountryCode = "TR", ClaimBuilt = true }
            },
            [], new Dictionary<Guid, ReportLineExtra>(), [], [], new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        var previous = input with { Period = new ReportPeriod(day.AddDays(-1), day.AddDays(-1), start.AddDays(-1), start), Orders = [] };

        var report = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());

        Assert.Empty(report.Cities);
        Assert.Equal(0, report.Summary.OrdersWithoutCity);
        Assert.Equal(1, report.Geography.AbroadOrders);
        var country = report.Geography.Countries.Single(x => x.Key == ReportBuckets.UnknownCountry);
        Assert.False(country.Known);
        Assert.Equal(300m, country.NetRevenue);
        Assert.Equal(200m, report.Geography.Countries.Single(x => x.Key == "AZ").NetRevenue);
        Assert.DoesNotContain(report.Geography.Countries, x => x.Key == ReportBuckets.Turkey);
        Assert.Contains(report.DataNotes, note => note.Contains("1 sipariş yurt dışına gitti (Azerbaycan 1)", StringComparison.Ordinal));
        Assert.Contains(report.DataNotes, note => note.Contains("1 Trendyol siparişi iade kaydından kuruldu", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TRENDYOL", null, true, ReportBuckets.UnknownCountry)]
    [InlineData("TRENDYOL", "SA", true, "SA")]
    [InlineData("TRENDYOL", "TR", true, "TR")]
    [InlineData("TRENDYOL", null, false, "TR")]
    [InlineData("HEPSIBURADA", null, true, "TR")]
    public void Only_a_claim_built_trendyol_order_without_a_storefront_has_an_unknown_country(string platform, string? storeFront, bool claimBuilt, string expected) =>
        Assert.Equal(expected, ReportBuckets.Country(platform, storeFront, "TR", claimBuilt));
}
