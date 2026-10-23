using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReportDiscountTests
{
    [Fact]
    public void The_discount_is_the_list_price_less_what_was_paid_over_the_orders_in_lira()
    {
        var store = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 15);
        var start = new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);
        var at = start.AddHours(12);
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [
                // 110 at list price, 100 paid: 10 of discount.
                new() { Id = Guid.NewGuid(), ConnectionId = store, OrderNumber = "1", DerivedStatus = "DELIVERED", Amount = 100m, ListAmount = 110m, LineValue = 100m, RealAt = at, OrderedAt = at },
                // No list price read: what was paid is its list price, so no discount.
                new() { Id = Guid.NewGuid(), ConnectionId = store, OrderNumber = "2", DerivedStatus = "DELIVERED", Amount = 50m, ListAmount = 0m, LineValue = 50m, RealAt = at, OrderedAt = at },
                // Another currency is left out of the lira amounts, its list price too.
                new() { Id = Guid.NewGuid(), ConnectionId = store, OrderNumber = "3", Currency = "USD", DerivedStatus = "DELIVERED", Amount = 40m, ListAmount = 45m, LineValue = 40m, RealAt = at, OrderedAt = at }
            ],
            new Dictionary<Guid, ReportOrderExtra>(), [], new Dictionary<Guid, ReportLineExtra>(), [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        var previous = input with { Period = new ReportPeriod(day.AddDays(-1), day.AddDays(-1), start.AddDays(-1), start), Orders = [] };

        var report = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());

        Assert.Equal(150m, report.Summary.GrossSales);
        Assert.Equal(160m, report.Summary.ListAmount);
        Assert.Equal(10m, report.Summary.DiscountAmount);
        Assert.Equal(0m, report.PreviousSummary.DiscountAmount);
    }
}
