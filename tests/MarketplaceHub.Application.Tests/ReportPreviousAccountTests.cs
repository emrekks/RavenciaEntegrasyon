using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReportPreviousAccountTests
{
    [Fact]
    public void The_previous_period_carries_its_own_commission_and_cost_so_its_profit_can_be_compared()
    {
        var store = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 15);
        var start = new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);
        var channels = new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) };
        var names = new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>());
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            channels, [], new Dictionary<Guid, ReportOrderExtra>(), [], new Dictionary<Guid, ReportLineExtra>(), [], [], names, false);
        // The day before: two units at 100 with a cost of 40 each and Trendyol's 15 % commission on the line.
        var at = start.AddHours(-12);
        var order = Guid.NewGuid();
        var line = Guid.NewGuid();
        var previous = input with
        {
            Period = new ReportPeriod(day.AddDays(-1), day.AddDays(-1), start.AddDays(-1), start),
            Orders = [new() { Id = order, ConnectionId = store, OrderNumber = "1", DerivedStatus = "DELIVERED", Amount = 200m, LineValue = 200m, RealAt = at, OrderedAt = at }],
            Lines = [new() { Id = line, OrderId = order, Title = "Kılıf", Sku = "K-1", UnitPrice = 100m, OrderedQuantity = 2m, CostPrice = 40m }],
            LineExtras = new Dictionary<Guid, ReportLineExtra> { [line] = new() { Id = line, Commission = "15" } }
        };

        var report = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());

        var account = Assert.IsType<ReportPreviousAccountView>(report.PreviousAccount);
        Assert.Equal(30m, account.Commission.EstimatedCommission);
        Assert.Equal(80m, account.Cost.CostTotal);
        Assert.Equal(1m, account.Cost.Coverage);
        Assert.Equal("estimate", account.Costs.Source);
        // The period's own figures are untouched by the previous period's.
        Assert.Equal(0m, report.Commission.EstimatedCommission);
    }
}
