using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PayoutBuilderTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);
    private static readonly Guid Store = Guid.CreateVersion7();
    private static readonly Dictionary<Guid, PayoutStore> Stores = new() { [Store] = new(Store, "TRENDYOL", "Ravencia", false) };

    // Due on a day in Istanbul: Trendyol's paymentDate is the day's midnight there (21:00 UTC the day before).
    private static DateTimeOffset Due(DateOnly day) => PayoutBuilder.DayStart(day);

    private static PayoutRow Sale(string id, decimal credit, decimal revenue, DateOnly due, string? payout = null, string country = "Türkiye", string affiliate = "TRENDYOLTR") => new()
    {
        ConnectionId = Store,
        PlatformCode = "TRENDYOL",
        Source = TrendyolFinanceRequestContract.Settlements,
        ExternalId = id,
        RequestedType = "Sale",
        Category = MarketplaceFinanceCategories.Sale,
        TransactionDate = Due(due.AddDays(-14)),
        Credit = credit,
        SellerRevenue = revenue,
        PaymentOrderId = payout,
        PaymentDate = Due(due),
        Country = country,
        Affiliate = affiliate
    };

    private static PayoutRow Deduction(string id, string type, string category, decimal debt, DateOnly due, string? payout = null) => new()
    {
        ConnectionId = Store,
        PlatformCode = "TRENDYOL",
        Source = TrendyolFinanceRequestContract.OtherFinancials,
        ExternalId = id,
        RequestedType = TrendyolFinanceCategorizer.DeductionInvoices,
        TransactionType = type,
        Category = category,
        TransactionDate = Due(due.AddDays(-3)),
        Debt = debt,
        PaymentOrderId = payout,
        PaymentDate = Due(due),
        Country = "Türkiye",
        Affiliate = "TRENDYOLTR"
    };

    [Theory]
    [InlineData("TRENDYOL", "Türkiye", "TRENDYOLTR", PayoutBuilder.TrGulf)]
    [InlineData("TRENDYOL", "Suudi Arabistan", "TRENDYOLTR", PayoutBuilder.TrGulf)]
    [InlineData("TRENDYOL", "Bulgaristan", "TRENDYOLTR", PayoutBuilder.Europe)]
    // Azerbaijan's affiliate decides, whatever the country (live: 35 records "Türkiye" / TRENDYOLAZJV).
    [InlineData("TRENDYOL", "Türkiye", "TRENDYOLAZJV", PayoutBuilder.Azerbaijan)]
    // A country no rule names is not counted as Türkiye.
    [InlineData("TRENDYOL", "Kazakistan", "TRENDYOLTR", PayoutBuilder.UnknownRegion)]
    [InlineData("TRENDYOL", null, null, PayoutBuilder.UnknownRegion)]
    // The other marketplaces pay without regions.
    [InlineData("PAZARAMA", null, null, PayoutBuilder.NoRegion)]
    public void A_record_falls_in_its_storefront_region(string platform, string? country, string? affiliate, string expected) =>
        Assert.Equal(expected, PayoutBuilder.Region(platform, country, affiliate));

    [Fact]
    public void The_balance_is_the_unpaid_seller_revenue_with_deductions_and_without_commission_invoices()
    {
        PayoutRow[] rows =
        [
            Sale("s1", 1000m, 800m, Today.AddDays(1)),
            // A return takes its seller revenue back.
            Sale("r1", 0m, 200m, Today.AddDays(1)) with { RequestedType = "Return", Category = MarketplaceFinanceCategories.Return, Credit = 0m, Debt = 250m },
            Deduction("k1", "Kargo Faturası", MarketplaceFinanceCategories.Cargo, 20m, Today.AddDays(1)),
            // The commission is already off the seller revenue; the invoice documenting it does not count again.
            Deduction("c1", "Komisyon Faturası", MarketplaceFinanceCategories.Commission, 200m, Today.AddDays(1)),
            Sale("s2", 500m, 400m, Today.AddDays(8), country: "Bulgaristan"),
            // Paid: not expected.
            Sale("s3", 100m, 80m, Today.AddDays(1), payout: "PO-9"),
            // Due day passed, no payout seen: unknown, not unpaid.
            Sale("s4", 300m, 240m, Today.AddDays(-2))
        ];
        var view = PayoutBuilder.Build(rows, [], Stores, Today, 90, []);
        // The balance is every record not paid yet, the overdue one too (Trendyol's "Toplam Güncel Bakiye" is this sum).
        Assert.Equal(800m - 200m - 20m + 400m + 240m, view.Expected);
        Assert.Equal(5, view.ExpectedRecords);
        Assert.Equal(240m, view.Unknown);
        // Türkiye-Körfez on the estimated payout day: Thursday 5 Nov takes the sales due Thursday to Sunday, the deduction
        // due that day and what waits since Monday.
        Assert.Equal(Today.AddDays(1), view.Next!.Day);
        Assert.Equal(800m - 200m - 20m + 240m, view.Next.Amount);
        Assert.Equal("PAYOUT_DAY", view.Next.Kind);
        var europe = Assert.Single(view.Regions, region => region.Region == PayoutBuilder.Europe);
        Assert.Equal(400m, europe.Expected);
        Assert.Equal(Today.AddDays(8), europe.NextDay);
        Assert.Equal(PayoutBuilder.Unknown, PayoutBuilder.Status(rows[6], Today));
        Assert.False(PayoutBuilder.Counts(rows[3]));
    }

    [Fact]
    public void A_payout_shows_the_marketplace_amount_and_what_its_read_records_add_up_to()
    {
        PayoutRow[] rows = [Sale("s1", 1000m, 800m, Today.AddDays(-6), payout: "PO-1"), Deduction("k1", "Kargo Faturası", MarketplaceFinanceCategories.Cargo, 20m, Today.AddDays(-6), payout: "PO-1")];
        PayoutOrderRow[] payouts = [new(Store, "PO-1", 780m, Due(Today.AddDays(-5)), "TR", "Türkiye"), new(Store, "PO-2", 50m, Due(Today.AddDays(-12)), "AZ", "Azerbaycan")];
        var view = PayoutBuilder.Build(rows, payouts, Stores, Today, 90, []);
        Assert.Equal(2, view.Past.Count);
        var first = view.Past[0];
        Assert.Equal("PAYOUT", first.Kind);
        Assert.Equal(780m, first.Amount);
        Assert.Equal(780m, first.LinesAmount);
        Assert.Equal(PayoutBuilder.TrGulf, first.Region);
        Assert.Null(view.Past[1].LinesAmount);
        Assert.Equal(PayoutBuilder.Azerbaijan, view.Past[1].Region);
        Assert.Equal(first, view.LastPaid);
        Assert.Equal(0m, view.Expected);
    }

    [Fact]
    public void Pazarama_says_itself_whether_an_agreement_was_paid()
    {
        var row = new PayoutRow
        {
            ConnectionId = Store,
            PlatformCode = "PAZARAMA",
            Source = PazaramaFinanceSyncJobHandler.Source,
            ExternalId = "p1",
            RequestedType = PazaramaFinanceSyncJobHandler.RequestedType,
            TransactionType = "Hakediş Ödemesi Bekliyor",
            Category = MarketplaceFinanceCategories.Sale,
            Credit = 2697m,
            SellerRevenue = 2228.23m,
            PaymentDate = Due(Today.AddDays(-2))
        };
        Assert.Equal(PayoutBuilder.Expected, PayoutBuilder.Status(row, Today));
        Assert.Equal(2228.23m, PayoutBuilder.Net(row));
        Assert.Equal(PayoutBuilder.Paid, PayoutBuilder.Status(row with { TransactionType = "Ödeme Yapıldı" }, Today));
    }

    [Fact]
    public void A_due_day_is_the_istanbul_calendar_day()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 21, 0, 0, TimeSpan.Zero), PayoutBuilder.DayStart(Today));
        Assert.Equal(Today, PayoutBuilder.Day(new DateTimeOffset(2026, 11, 3, 21, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    // Sales: Monday's payout takes Monday to Wednesday, Thursday's Thursday to Sunday (Trendyol, Oct-Nov 2026).
    [InlineData("2026-11-10", true, "2026-11-09")]
    [InlineData("2026-11-11", true, "2026-11-09")]
    [InlineData("2026-11-15", true, "2026-11-12")]
    // A deduction goes with the first payout on or after its due day.
    [InlineData("2026-11-06", false, "2026-11-09")]
    [InlineData("2026-11-10", false, "2026-11-12")]
    // A day already past moves to the next payout to come (today is Wednesday 4 Nov).
    [InlineData("2026-11-03", true, "2026-11-05")]
    public void Turkiye_records_are_expected_on_the_payout_day_that_takes_them(string due, bool sale, string expected)
    {
        var day = DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture);
        var row = sale ? Sale("s", 100m, 80m, day) : Deduction("k", "Kargo Faturası", MarketplaceFinanceCategories.Cargo, 20m, day);
        var (payoutDay, kind) = PayoutBuilder.Expect(row, PayoutBuilder.Line(row, Today), Today);
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), payoutDay);
        Assert.Equal("PAYOUT_DAY", kind);
        // Another region's records stand on their due day; past it, they wait apart (OVERDUE), out of the next payout.
        Assert.Equal(day < Today ? (Today, "OVERDUE") : (day, "DUE_DAY"), PayoutBuilder.Expect(row with { Country = "Bulgaristan" }, PayoutBuilder.Line(row with { Country = "Bulgaristan" }, Today), Today));
    }

    [Theory]
    [InlineData("TR", "Türkiye, Körfez", PayoutBuilder.TrGulf)]
    [InlineData("AZ", "Azerbaycan", PayoutBuilder.Azerbaijan)]
    [InlineData("CEE", "Avrupa", PayoutBuilder.Europe)]
    [InlineData("XX", "Bilinmeyen", PayoutBuilder.UnknownRegion)]
    public void A_payout_region_is_read_from_trendyols_code(string code, string name, string expected) =>
        Assert.Equal(expected, PayoutBuilder.PayoutRegion(code, name));

    [Fact]
    public void An_overdue_record_without_a_known_payout_day_stays_in_the_balance_but_out_of_the_next_payout()
    {
        // Avrupa, paid about monthly: due last week, no payout yet. Türkiye-Körfez is paid on Thursday 5 Nov.
        PayoutRow[] rows = [Sale("eu", 1000m, 800m, Today.AddDays(-7), country: "Bulgaristan"), Sale("tr", 500m, 400m, Today.AddDays(1))];
        var view = PayoutBuilder.Build(rows, [], Stores, Today, 90, []);
        Assert.Equal(1200m, view.Expected);
        Assert.Equal(800m, view.Unknown);
        Assert.Equal((Today.AddDays(1), 400m), (view.Next!.Day, view.Next.Amount));
        var waiting = Assert.Single(view.Upcoming, group => group.Kind == "OVERDUE");
        Assert.Equal((PayoutBuilder.Europe, 800m), (waiting.Region, waiting.Amount));
        var europe = Assert.Single(view.Regions, region => region.Region == PayoutBuilder.Europe);
        Assert.Null(europe.NextDay);
        Assert.Equal(800m, europe.Unknown);
    }

    [Fact]
    public void Each_marketplace_has_its_own_total_and_the_totals_add_up_to_the_balance()
    {
        var pazarama = Guid.CreateVersion7();
        var stores = new Dictionary<Guid, PayoutStore>(Stores) { [pazarama] = new(pazarama, "PAZARAMA", "Pazarama", false) };
        PayoutRow[] rows =
        [
            Sale("tr", 500m, 400m, Today.AddDays(1)), Sale("eu", 300m, 250m, Today.AddDays(-7), country: "Bulgaristan"),
            new()
            {
                ConnectionId = pazarama, PlatformCode = "PAZARAMA", Source = PazaramaFinanceSyncJobHandler.Source, ExternalId = "p1", RequestedType = PazaramaFinanceSyncJobHandler.RequestedType,
                TransactionType = "Hakediş Ödemesi Bekliyor", Category = MarketplaceFinanceCategories.Sale, Credit = 120m, SellerRevenue = 100m, PaymentDate = Due(Today.AddDays(3))
            }
        ];
        var view = PayoutBuilder.Build(rows, [], stores, Today, 90, []);
        Assert.Equal(["TRENDYOL", "PAZARAMA"], view.Platforms.Select(x => x.PlatformCode));
        Assert.Equal(view.Expected, view.Platforms.Sum(x => x.Expected));
        var trendyol = view.Platforms[0];
        Assert.Equal((650m, 250m, Today.AddDays(1), 400m), (trendyol.Expected, trendyol.Unknown, trendyol.NextDay!.Value, trendyol.NextAmount!.Value));
        Assert.Equal((100m, Today.AddDays(3)), (view.Platforms[1].Expected, view.Platforms[1].NextDay!.Value));
    }
}
