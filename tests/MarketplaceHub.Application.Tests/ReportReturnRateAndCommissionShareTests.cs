using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan C-9: the real commission of an order (net of what was given back) was shared over its lines by every unit sold, so
// a returned line carried a part of it; and İade Oranı was returned ÷ ordered (cancelled units included) on the summary
// but returned ÷ sold on every platform, city, brand and product row, so the total disagreed with its own rows.
public sealed class ReportReturnRateAndCommissionShareTests
{
    private static readonly Guid Store = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 21, 0, 0, TimeSpan.Zero);

    private static ReportInput Input(string platform, IReadOnlyList<ReportOrderRow> orders, IReadOnlyList<ReportLineRow> lines, IReadOnlyDictionary<Guid, decimal>? commissions = null) =>
        new(new ReportPeriod(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4), Start, Start.AddDays(1)), ReportBuckets.Day,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [Store] = new(Store, platform, platform, false) },
            orders, new Dictionary<Guid, ReportOrderExtra>(), lines,
            lines.ToDictionary(line => line.Id, line => new ReportLineExtra { Id = line.Id, CommissionRate = "20" }),
            [], [], new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false,
            OrderCommissions: commissions);

    private static ReportOrderRow Order(Guid id, string status, decimal amount, decimal lineBack = 0m) =>
        new() { Id = id, ConnectionId = Store, OrderNumber = id.ToString("N")[..8], DerivedStatus = status, Amount = amount, LineValue = amount, LineBack = lineBack, RealAt = Start.AddHours(12), OrderedAt = Start.AddHours(12) };

    [Fact]
    public void The_real_commission_goes_to_the_kept_units_only()
    {
        var order = Guid.NewGuid();
        var (returned, kept) = (Guid.NewGuid(), Guid.NewGuid());
        // 600 + 400 sold; the 600 line came back, so the 80 net commission was charged on the 400 kept.
        var input = Input("N11", [Order(order, "DELIVERED", 1000m, lineBack: 600m)],
        [
            new() { Id = returned, OrderId = order, Title = "Ceket", Sku = "A", UnitPrice = 600m, OrderedQuantity = 1m, ReturnedQuantity = 1m, VatRate = 20m },
            new() { Id = kept, OrderId = order, Title = "Gömlek", Sku = "B", UnitPrice = 400m, OrderedQuantity = 1m, VatRate = 20m }
        ], new Dictionary<Guid, decimal> { [order] = 80m });

        var lines = ReportBuilder.Facts(input).Orders.Single().Lines.ToDictionary(line => line.Row.Id);

        Assert.Equal(0m, lines[returned].Commission);
        Assert.Equal(80m, lines[kept].Commission);
    }

    [Fact]
    public void Return_rate_is_returned_over_sold_on_the_summary_and_every_row()
    {
        var (delivered, cancelled) = (Guid.NewGuid(), Guid.NewGuid());
        // 4 units sold, 1 back, 1 more cancelled on the line and a whole cancelled order of 2 units.
        var input = Input("TRENDYOL", [Order(delivered, "DELIVERED", 500m, lineBack: 100m), Order(cancelled, "CANCELLED", 200m)],
        [
            new() { Id = Guid.NewGuid(), OrderId = delivered, Title = "Kılıf", Sku = "K", UnitPrice = 100m, OrderedQuantity = 5m, CancelledQuantity = 1m, ReturnedQuantity = 1m, VatRate = 20m },
            new() { Id = Guid.NewGuid(), OrderId = cancelled, Title = "Kılıf", Sku = "K", UnitPrice = 100m, OrderedQuantity = 2m, VatRate = 20m }
        ]);
        var previous = input with { Orders = [], Lines = [], LineExtras = new Dictionary<Guid, ReportLineExtra>() };

        var report = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());

        Assert.Equal(0.25m, report.Summary.ReturnRate);
        Assert.Equal(report.Summary.ReturnRate, report.Platforms.Single().ReturnRate);
        Assert.Equal(report.Summary.ReturnRate, report.TopProducts.Single().ReturnRate);
        // İptal Oranı keeps its own basis: every unit ordered (3 of 7).
        Assert.Equal(0.4286m, report.Summary.CancelRate);
    }
}
