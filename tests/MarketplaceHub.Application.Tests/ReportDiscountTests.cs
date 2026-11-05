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

    // Live, order 11772267262: Trendyol's discount (lineTyDiscount, per unit) is read from the line; a line without
    // the field leaves it unknown, never 0.
    [Fact]
    public void The_trendyol_mapper_reads_trendyols_own_discount_per_unit_and_leaves_a_missing_one_unknown()
    {
        const string json = """
            {"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"packageGrossAmount":1127.17,"packageSellerDiscount":76.28,"packageTyDiscount":150,"packageTotalDiscount":226.28,"packageTotalPrice":900.89,"lines":[
              {"lineId":"line-1","stockCode":"SKU-1","productName":"Test","quantity":1,"lineUnitPrice":450.44,"lineGrossAmount":563.58,"lineSellerDiscount":38.14,"lineTyDiscount":75.00,"vatRate":10},
              {"lineId":"line-2","stockCode":"SKU-2","productName":"Test","quantity":1,"lineUnitPrice":450.45,"vatRate":10}]}]}
            """;

        var order = Assert.Single(MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping.TrendyolJsonMapper.Orders(json).Items);

        Assert.Equal(75m, order.Lines.Single(line => line.ExternalLineId == "line-1").PlatformDiscount);
        Assert.Null(order.Lines.Single(line => line.ExternalLineId == "line-2").PlatformDiscount);
        Assert.Equal(900.89m, order.NetAmount);
    }

    // The order's amount holds Trendyol's discount (ReportsService.OrderRowsAsync); a line's money and the commission
    // estimate are on the same base, the price before Trendyol's discount.
    [Fact]
    public void A_lines_money_and_commission_include_the_marketplaces_own_discount()
    {
        var store = Guid.NewGuid();
        var day = new DateOnly(2026, 11, 1);
        var start = new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero);
        var at = start.AddHours(12);
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [new() { Id = orderId, ConnectionId = store, OrderNumber = "1", DerivedStatus = "CREATED", Amount = 525.44m, ListAmount = 563.58m, LineValue = 525.44m, RealAt = at, OrderedAt = at }],
            new Dictionary<Guid, ReportOrderExtra>(),
            [new() { Id = lineId, OrderId = orderId, Title = "Test", Sku = "SKU-1", UnitPrice = 450.44m, PlatformDiscount = 75m, OrderedQuantity = 1m }],
            new Dictionary<Guid, ReportLineExtra> { [lineId] = new() { Id = lineId, Commission = "16.1" } }, [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        var previous = input with { Period = new ReportPeriod(day.AddDays(-1), day.AddDays(-1), start.AddDays(-1), start), Orders = [], Lines = [] };

        var report = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());

        Assert.Equal(525.44m, report.Summary.NetRevenue);
        Assert.Equal(38.14m, report.Summary.DiscountAmount);
        Assert.Equal(525.44m, report.Commission.CoveredRevenue);
        Assert.Equal(84.60m, report.Commission.EstimatedCommission);
    }
}
