using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// FATURA-FINANS-5 (owner, 16 Nov 2026): reports, payouts and ad reports round a computed half kuruş away from zero, as
// the invoice does; before, they rounded it to even (1.225 → 1.22). Only the midpoint rule changed: a figure is still
// rounded where it was (each line's commission, then their sum of the rounded lines).
public sealed class MoneyRoundingMidpointTests
{
    [Theory]
    [InlineData("1.225", "1.23")]
    [InlineData("1.235", "1.24")]
    [InlineData("0.125", "0.13")]
    [InlineData("-1.225", "-1.23")]
    [InlineData("-0.005", "-0.01")]
    [InlineData("1.2249", "1.22")]
    public void Money_rounds_half_a_kurus_away_from_zero_also_below_zero(string value, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), MoneyRounding.Round(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    // Days, hours, units per day and orders per customer are neither money nor a percentage: they keep the rule they had.
    [Theory]
    [InlineData("2.25", 1, "2.2")]
    [InlineData("-2.25", 1, "-2.2")]
    [InlineData("1.125", 2, "1.12")]
    public void Measures_that_are_not_percentages_keep_rounding_to_even(string value, int decimals, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), MeasureRounding.Round(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), decimals));

    // Percentages and rates round half up (owner, 16 Nov 2026): a rate kept as a fraction at four places, a percentage on
    // the 0–100 scale at two. Before, both rounded to even (0.12345 → 0.1234, 12.345 → 12.34).
    [Theory]
    [InlineData("0.12345", "0.1235")]
    [InlineData("0.12355", "0.1236")]
    [InlineData("-0.12345", "-0.1235")]
    [InlineData("0.12344", "0.1234")]
    public void A_rate_at_the_midpoint_rounds_half_up(string value, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), PercentRounding.Rate(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("12.345", "12.35")]
    [InlineData("12.355", "12.36")]
    [InlineData("-12.345", "-12.35")]
    [InlineData("0.005", "0.01")]
    [InlineData("12.3449", "12.34")]
    public void A_percentage_at_the_midpoint_rounds_half_up(string value, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), PercentRounding.Percent(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    // An order's margin on its kept amount at a midpoint: 1.23450 profit on 10 = 12.345 % → 12.35 % (12.34 % before).
    [Fact]
    public void An_order_margin_at_the_midpoint_rounds_half_up() =>
        Assert.Equal(12.35m, PercentRounding.Percent(1.2345m / 10m * 100m));

    [Theory]
    // Trendyol 10 % of 12.25 = 1.225: 1.22 before, 1.23 now.
    [InlineData("TRENDYOL", null, "10", null, null, "12.25", "1", "1.23")]
    [InlineData("N11", null, null, "10", null, "12.25", "1", "1.23")]
    [InlineData("PTTAVM", null, null, null, "10", "12.25", "1", "1.23")]
    // Hepsiburada's per-unit amount with its 20 % VAT: 0.9375 × 1.20 = 1.125 → 1.13 (1.12 before).
    [InlineData("HEPSIBURADA", "object", "0.9375", null, null, "100", "1", "1.13")]
    // Hepsiburada's rate with its VAT: 12.25 × 7.5 % × 1.20 = 1.1025 (no midpoint, unchanged).
    [InlineData("HEPSIBURADA", null, null, "7.5", null, "12.25", "1", "1.10")]
    public void A_lines_estimated_commission_at_a_half_kurus_rounds_up(string platform, string? type, string? commission, string? commissionRate, string? komisyon,
        string kept, string units, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            ReportBuckets.EstimatedCommission(platform, type, commission, commissionRate, komisyon,
                decimal.Parse(kept, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(units, System.Globalization.CultureInfo.InvariantCulture)));

    // Two Trendyol lines of 12.25 at 10 %: each line's commission is rounded first (1.225 → 1.23), then the rounded lines
    // are added (2.46). Before, 1.22 + 1.22 = 2.44; the sum of the unrounded lines (2.45) was never the figure.
    [Fact]
    public void The_report_adds_the_rounded_line_commissions()
    {
        var store = Guid.NewGuid();
        var day = new DateOnly(2026, 11, 1);
        var start = new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero);
        var at = start.AddHours(12);
        var orderId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var input = new ReportInput(new ReportPeriod(day, day, start, start.AddDays(1)), ReportBuckets.Day, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
            new Dictionary<Guid, ReportChannelView> { [store] = new(store, "TRENDYOL", "Trendyol", false) },
            [new() { Id = orderId, ConnectionId = store, OrderNumber = "1", DerivedStatus = "DELIVERED", Amount = 24.50m, ListAmount = 24.50m, LineValue = 24.50m, RealAt = at, OrderedAt = at }],
            new Dictionary<Guid, ReportOrderExtra>(),
            [
                new() { Id = first, OrderId = orderId, Title = "A", Sku = "SKU-1", UnitPrice = 12.25m, OrderedQuantity = 1m },
                new() { Id = second, OrderId = orderId, Title = "B", Sku = "SKU-2", UnitPrice = 12.25m, OrderedQuantity = 1m }
            ],
            new Dictionary<Guid, ReportLineExtra> { [first] = new() { Id = first, Commission = "10" }, [second] = new() { Id = second, Commission = "10" } }, [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        var previous = input with { Period = new ReportPeriod(day.AddDays(-1), day.AddDays(-1), start.AddDays(-1), start), Orders = [], Lines = [] };

        var report = ReportBuilder.Build(input, previous, new Dictionary<Guid, decimal>());

        Assert.Equal(2.46m, report.Commission.EstimatedCommission);
        Assert.Equal(24.50m - 2.46m, report.Commission.EstimatedPayout);
        Assert.All(report.TopProducts, product => Assert.Equal(1.23m, product.EstimatedCommission));
    }

    // Avrupa's withholding tax estimate, 1 % of 12.50 = 0.125: 0.12 before, 0.13 now. The marketplace's own record
    // (12.50) is taken as it came.
    [Fact]
    public void A_payout_estimate_at_a_half_kurus_rounds_up_and_the_marketplace_amount_stays_as_received()
    {
        var store = Guid.CreateVersion7();
        var stores = new Dictionary<Guid, PayoutStore> { [store] = new(store, "TRENDYOL", "Ravencia", false) };
        var due = new DateOnly(2026, 11, 20);
        var sale = new PayoutRow
        {
            ConnectionId = store,
            PlatformCode = "TRENDYOL",
            Source = TrendyolFinanceRequestContract.Settlements,
            ExternalId = "r1",
            RequestedType = "Sale",
            Category = MarketplaceFinanceCategories.Sale,
            TransactionDate = PayoutBuilder.DayStart(due.AddDays(-28)),
            OrderNumber = "O-P1",
            ShipmentPackageId = "P1",
            Barcode = "B-1",
            Credit = 12.50m,
            Debt = 0m,
            SellerRevenue = 12.50m,
            PaymentDate = PayoutBuilder.DayStart(due),
            Country = "Romanya",
            Affiliate = "TRENDYOLTR"
        };
        var forecast = new PayoutForecast(new Dictionary<Guid, decimal> { [store] = 98.34m }, new HashSet<(Guid, string)> { (store, "O-P1") }, new HashSet<(Guid, string)>(),
            new Dictionary<(Guid, string, string), decimal>());

        var day = Assert.Single(PayoutBuilder.Build([sale], [], stores, new DateOnly(2026, 11, 11), 90, [], forecast: forecast).Upcoming);

        Assert.Equal(PayoutBuilder.Europe, day.Region);
        Assert.Equal(12.50m, day.LinesAmount);
        Assert.Equal([("INTERNATIONAL_FEE", 0.75m), ("STOPPAGE", 0.13m)], day.Estimates!.Select(x => (x.Kind, x.Amount)));
        Assert.Equal(12.50m - 0.75m - 0.13m, day.Amount);
    }

    // The cost per click, 1.25 TL over 10 clicks = 0.125: 0.12 before, 0.13 now; the return on ad spend, 12.345, is
    // 12.35 since 16 Nov 2026 (12.34 before, to even).
    [Fact]
    public void The_ad_reports_cost_per_click_and_roas_at_the_midpoint_round_half_up()
    {
        var campaign = new AdCampaignRow("Kampanya", null, null, null, null, null, null, null, 1.00m, null, null, 8L, 100L, null, null, null, null, null, 12.345m, null);
        var clicks = campaign with { Spent = 0.25m, Clicks = 2L, TotalRevenue = null };

        var totals = AdReportService.Totals([campaign, clicks]);

        Assert.Equal(1.25m, totals.Spent);
        Assert.Equal(0.13m, totals.Cpc);
        Assert.Equal(12.35m, totals.Roas);
    }

    // The click-through rate at a midpoint: 12345 clicks of 100000 impressions = 0.12345 → 0.1235 (0.1234 before).
    [Fact]
    public void The_ad_reports_click_through_rate_at_the_midpoint_rounds_half_up()
    {
        var campaign = new AdCampaignRow("Kampanya", null, null, null, null, null, null, null, 1.00m, null, null, 12345L, 100000L, null, null, null, null, null, null, null);

        Assert.Equal(0.1235m, AdReportService.Totals([campaign]).Ctr);
    }
}
