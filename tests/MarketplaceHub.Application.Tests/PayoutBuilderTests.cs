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
        // Avrupa on its monthly payout day, the fourth Monday (23 Nov 2026).
        Assert.Equal(new DateOnly(2026, 11, 23), europe.NextDay);
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
    public void A_pazarama_row_with_no_state_word_is_expected_until_its_day_has_passed()
    {
        var row = new PayoutRow
        {
            ConnectionId = Store,
            PlatformCode = "PAZARAMA",
            Source = PazaramaFinanceSyncJobHandler.Source,
            ExternalId = "p2",
            RequestedType = PazaramaFinanceSyncJobHandler.RequestedType,
            TransactionType = "Satış",
            Category = MarketplaceFinanceCategories.Sale,
            Credit = 100m,
            PaymentDate = Due(Today)
        };
        Assert.Equal(PayoutBuilder.Expected, PayoutBuilder.Status(row, Today));
        Assert.Equal(PayoutBuilder.Expected, PayoutBuilder.Status(row with { TransactionType = "İade", PaymentDate = Due(Today.AddDays(3)) }, Today));
        Assert.Equal(PayoutBuilder.Unknown, PayoutBuilder.Status(row with { PaymentDate = Due(Today.AddDays(-1)) }, Today));
        Assert.Equal(PayoutBuilder.Unknown, PayoutBuilder.Status(row with { PaymentDate = null }, Today));
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
        var (payoutDay, kind, region) = PayoutBuilder.Expect(row, PayoutBuilder.Line(row, Today), Today);
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), payoutDay);
        Assert.Equal(("PAYOUT_DAY", PayoutBuilder.TrGulf), (kind, region));
        // Avrupa's records, whatever their due day in early November, wait for its monthly payout on Monday 23 Nov.
        Assert.Equal((new DateOnly(2026, 11, 23), "PAYOUT_DAY", PayoutBuilder.Europe),
            PayoutBuilder.Expect(row with { Country = "Bulgaristan" }, PayoutBuilder.Line(row with { Country = "Bulgaristan" }, Today), Today));
    }

    [Theory]
    // Trendyol's Avrupa payouts, live: 22 Jun, 27 Jul, 24 Aug, 28 Sep, 26 Oct 2026; its Ödeme Özeti of 11 Nov: 23 Nov.
    [InlineData("2026-06-01", "2026-06-22")]
    [InlineData("2026-06-30", "2026-07-27")]
    [InlineData("2026-07-28", "2026-08-24")]
    [InlineData("2026-08-26", "2026-09-28")]
    [InlineData("2026-09-29", "2026-10-26")]
    // A record due on the payout day goes with it (the 26 Oct payout took the sales due that Monday).
    [InlineData("2026-10-26", "2026-10-26")]
    [InlineData("2026-10-27", "2026-11-23")]
    [InlineData("2026-11-24", "2026-12-28")]
    public void Avrupa_is_paid_on_the_fourth_monday_of_the_month(string due, string expected) =>
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            PayoutBuilder.EuropePayoutDayOnOrAfter(DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void No_trendyol_record_is_expected_on_a_day_trendyol_does_not_pay()
    {
        // Wednesday 11 Nov 2026: Trendyol pays on Mondays and Thursdays (Tuesday and Friday are "Erken Ödeme" only),
        // Avrupa on the fourth Monday. The Greek sales due that Wednesday (431.07) are not "paid today".
        var wednesday = new DateOnly(2026, 11, 11);
        for (var due = wednesday.AddDays(-20); due <= wednesday.AddDays(40); due = due.AddDays(1))
            foreach (var country in new[] { "Türkiye", "Yunanistan", "Azerbaycan" })
                foreach (var sale in new[] { true, false })
                {
                    var row = sale ? Sale("s", 549.14m, 431.07m, due, country: country, affiliate: country == "Azerbaycan" ? "TRENDYOLAZJV" : "TRENDYOLTR")
                        : Deduction("k", "Kargo Fatura", MarketplaceFinanceCategories.Cargo, 98.34m, due);
                    var (day, kind, _) = PayoutBuilder.Expect(row, PayoutBuilder.Line(row, wednesday), wednesday);
                    Assert.Equal("PAYOUT_DAY", kind);
                    Assert.True(day >= wednesday && day.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Thursday, $"{country} {due} -> {day}");
                }
    }

    [Fact]
    public void A_deduction_the_turkiye_payout_left_out_goes_with_the_next_avrupa_payout()
    {
        // Live, 11 Nov 2026: the platform fee invoices of Avrupa parcels and a micro-export return fee, written "Türkiye",
        // skipped by the Türkiye payouts of 5 and 9 Nov. Every such deduction of Aug-Nov 2026 was paid by Avrupa.
        var today = new DateOnly(2026, 11, 11);
        PayoutRow[] rows =
        [
            Deduction("pf", "Platform Hizmet Bedeli", MarketplaceFinanceCategories.ServiceFee, 52.76m, new(2026, 10, 29)),
            Deduction("yo", "Yurtdışı Operasyon Iade Bedeli", MarketplaceFinanceCategories.OtherDeduction, 192.20m, new(2026, 11, 3)),
            Sale("gr", 549.14m, 431.07m, today, country: "Yunanistan")
        ];
        PayoutOrderRow[] tr = [new(Store, "TR-1", 17777.82m, Due(new DateOnly(2026, 11, 9)), "TR", "Türkiye, Körfez")];
        PayoutOrderRow[] withEurope = [.. tr, new(Store, "CEE-1", 19620.22m, Due(new DateOnly(2026, 10, 26)), "CEE", "Avrupa")];
        var view = PayoutBuilder.Build(rows, withEurope, Stores, today, 90, []);
        var europe = Assert.Single(view.Upcoming);
        Assert.Equal((new DateOnly(2026, 11, 23), PayoutBuilder.Europe, "PAYOUT_DAY", 431.07m - 52.76m - 192.20m), (europe.Day, europe.Region, europe.Kind, europe.Amount));
        // A store never paid in Avrupa keeps them apart, OVERDUE.
        var apart = PayoutBuilder.Build(rows, tr, Stores, today, 90, []);
        Assert.Equal(-244.96m, Assert.Single(apart.Upcoming, x => x.Kind == "OVERDUE").Amount);
    }

    [Theory]
    [InlineData("TR", "Türkiye, Körfez", PayoutBuilder.TrGulf)]
    [InlineData("AZ", "Azerbaycan", PayoutBuilder.Azerbaijan)]
    [InlineData("CEE", "Avrupa", PayoutBuilder.Europe)]
    [InlineData("XX", "Bilinmeyen", PayoutBuilder.UnknownRegion)]
    public void A_payout_region_is_read_from_trendyols_code(string code, string name, string expected) =>
        Assert.Equal(expected, PayoutBuilder.PayoutRegion(code, name));

    [Fact]
    public void An_avrupa_record_past_its_due_day_waits_for_the_monthly_payout_out_of_the_next_one()
    {
        // Avrupa, paid on the fourth Monday: due last week, no payout yet. Türkiye-Körfez is paid on Thursday 5 Nov.
        PayoutRow[] rows = [Sale("eu", 1000m, 800m, Today.AddDays(-7), country: "Bulgaristan"), Sale("tr", 500m, 400m, Today.AddDays(1))];
        var view = PayoutBuilder.Build(rows, [], Stores, Today, 90, []);
        Assert.Equal(1200m, view.Expected);
        Assert.Equal(800m, view.Unknown);
        Assert.Equal((Today.AddDays(1), 400m), (view.Next!.Day, view.Next.Amount));
        var europeDay = Assert.Single(view.Upcoming, group => group.Region == PayoutBuilder.Europe);
        Assert.Equal((new DateOnly(2026, 11, 23), "PAYOUT_DAY", 800m), (europeDay.Day, europeDay.Kind, europeDay.Amount));
        var europe = Assert.Single(view.Regions, region => region.Region == PayoutBuilder.Europe);
        Assert.Equal(new DateOnly(2026, 11, 23), europe.NextDay);
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

    [Fact]
    public void Hepsiburada_pays_on_tuesdays_its_total_record_is_the_payout_and_a_day_netting_to_nothing_is_no_next_payout()
    {
        var store = Guid.CreateVersion7();
        var stores = new Dictionary<Guid, PayoutStore> { [store] = new(store, "HEPSIBURADA", "HB", false) };
        PayoutRow Record(string id, string type, string status, decimal credit, decimal debt, DateOnly day) => new()
        {
            ConnectionId = store,
            PlatformCode = "HEPSIBURADA",
            Source = HepsiburadaFinanceSyncJobHandler.Source,
            ExternalId = id,
            RequestedType = "x",
            TransactionType = type,
            TransactionSubType = status,
            Category = MarketplaceFinanceCategories.Sale,
            Credit = credit,
            Debt = debt,
            PaymentDate = Due(day)
        };
        PayoutRow[] rows =
        [
            // Thursday 12 Nov: a sale and its return net to nothing; Monday 16 Nov: a sale; Tuesday 17 Nov: invoices.
            Record("s1", "Payment", "WillBePaid", 600m, 0m, new(2026, 11, 12)), Record("r1", "Return", "WillBePaid", 0m, 600m, new(2026, 11, 12)),
            Record("s2", "Payment", "WillBePaid", 625m, 0m, new(2026, 11, 16)), Record("c1", "Commission", "WillBePaid", 0m, 125m, new(2026, 11, 17)),
            // Tuesday 3 Nov, paid: two records and Hepsiburada's own total of them.
            Record("p1", "Payment", "Paid", 1000m, 0m, new(2026, 11, 3)), Record("p2", "Commission", "Paid", 0m, 200m, new(2026, 11, 3)),
            Record("t1", "TotalPayment", "Paid", 800m, 0m, new(2026, 11, 3))
        ];
        var view = PayoutBuilder.Build(rows, [], stores, Today, 90, []);
        // Everything due 11-17 Nov is paid on Tuesday 17 Nov: one day, 500.
        var day = Assert.Single(view.Upcoming);
        Assert.Equal((new DateOnly(2026, 11, 17), 500m, "PAYOUT_DAY"), (day.Day, day.Amount, day.Kind));
        Assert.Equal(500m, view.Expected);
        // The total record is the payout's figure, not one more record.
        var paid = Assert.Single(view.Past);
        Assert.Equal((800m, 800m, "PAID_DAY"), (paid.Amount, paid.LinesAmount!.Value, paid.Kind));

        // A day netting to nothing or less is no next payout.
        PayoutRow[] nothing = [Record("s3", "Payment", "WillBePaid", 600m, 0m, new(2026, 11, 12)), Record("r3", "Return", "WillBePaid", 0m, 700m, new(2026, 11, 12))];
        Assert.Null(PayoutBuilder.Build(nothing, [], stores, Today, 90, []).Next);
    }

    [Theory]
    [InlineData("TRENDYOL", "TR", PayoutBuilder.TrGulf)]
    [InlineData("TRENDYOL", "SA", PayoutBuilder.TrGulf)]
    [InlineData("TRENDYOL", "RO", PayoutBuilder.Europe)]
    [InlineData("TRENDYOL", "AZ", PayoutBuilder.Azerbaijan)]
    // A storefront not read yet, or one no rule names, is not counted as Türkiye.
    [InlineData("TRENDYOL", null, PayoutBuilder.UnknownRegion)]
    [InlineData("TRENDYOL", "KZ", PayoutBuilder.UnknownRegion)]
    [InlineData("HEPSIBURADA", null, PayoutBuilder.NoRegion)]
    public void An_orders_storefront_decides_its_payout_region(string platform, string? storeFront, string region) =>
        Assert.Equal(region, PayoutBuilder.StoreFrontRegion(platform, storeFront));

    [Fact]
    public void The_orders_not_delivered_yet_stand_apart_from_the_balance_by_marketplace_and_region()
    {
        var hb = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var stores = new Dictionary<Guid, PayoutStore>(Stores) { [hb] = new(hb, "HEPSIBURADA", "HB", false) };
        PendingOrderRow[] pending =
        [
            new(Store, "TR", 1000m), new(Store, "SA", 250.5m), new(Store, "RO", 400m), new(Store, "AZ", 80m),
            new(hb, null, 300m),
            // A store out of view (another filter or a test store) is left out.
            new(other, "TR", 999m)
        ];
        var view = PayoutBuilder.Build([Sale("tr", 500m, 400m, Today.AddDays(1))], [], stores, Today, 90, [], pending);
        Assert.Equal((2030.5m, 5), (view.PendingDelivery, view.PendingDeliveryOrders));
        // Not in the balance: that is the delivered orders' records alone.
        Assert.Equal(400m, view.Expected);
        // Trendyol's own "Onaylanan/Kargolanan Sipariş Tutarı" is its Türkiye-Körfez region's figure.
        var trGulf = Assert.Single(view.Regions, x => x.Region == PayoutBuilder.TrGulf);
        Assert.Equal((1250.5m, 2), (trGulf.PendingDelivery, trGulf.PendingDeliveryOrders));
        Assert.Equal(400m, Assert.Single(view.Regions, x => x.Region == PayoutBuilder.Europe).PendingDelivery);
        Assert.Equal(80m, Assert.Single(view.Regions, x => x.Region == PayoutBuilder.Azerbaijan).PendingDelivery);
        Assert.Equal([(1730.5m, 4), (300m, 1)], view.Platforms.Select(x => (x.PendingDelivery, x.PendingDeliveryOrders)));
    }

    [Fact]
    public void A_day_netting_below_zero_is_carried_into_the_store_next_payout_and_never_into_another_marketplace()
    {
        // Wednesday 4 Nov. Monday 9 Nov holds a deduction alone (-500), Thursday 12 Nov a sale (+2,000): Trendyol takes the
        // Monday's 500 off the Thursday payout.
        PayoutRow[] rows =
        [
            Deduction("k1", "Kargo Faturası", MarketplaceFinanceCategories.Cargo, 500m, new(2026, 11, 9)),
            Sale("s1", 2500m, 2000m, new(2026, 11, 12))
        ];
        var view = PayoutBuilder.Build(rows, [], Stores, Today, 90, []);
        Assert.Equal((new DateOnly(2026, 11, 12), 1500m, -500m), (view.Next!.Day, view.Next.Amount, view.Next.Carried));
        var region = Assert.Single(view.Regions, x => x.Region == PayoutBuilder.TrGulf);
        Assert.Equal((new DateOnly(2026, 11, 12), 1500m), (region.NextDay, region.NextAmount));
        Assert.Equal((new DateOnly(2026, 11, 12), 1500m), (view.Platforms[0].NextDay, view.Platforms[0].NextAmount));
        // The list keeps each day's own amount.
        Assert.Equal([-500m, 2000m], view.Upcoming.Select(x => x.Amount));

        // A Pazarama store paying 600 on the Monday is not taken down by Trendyol's -500 the same day.
        var pazarama = Guid.CreateVersion7();
        var stores = new Dictionary<Guid, PayoutStore>(Stores) { [pazarama] = new(pazarama, "PAZARAMA", "Ravencia PZ", false) };
        PayoutRow agreement = new()
        {
            ConnectionId = pazarama,
            PlatformCode = "PAZARAMA",
            Source = PazaramaFinanceSyncJobHandler.Source,
            ExternalId = "p1",
            RequestedType = "x",
            TransactionType = "Satış",
            Category = MarketplaceFinanceCategories.Sale,
            TransactionDate = Due(Today),
            Credit = 600m,
            SellerRevenue = 600m,
            PaymentDate = Due(new DateOnly(2026, 11, 9))
        };
        var both = PayoutBuilder.Build([.. rows, agreement], [], stores, Today, 90, []);
        Assert.Equal((new DateOnly(2026, 11, 9), 600m), (both.Next!.Day, both.Next.Amount));
        Assert.Null(both.Next.Carried);
    }

    [Fact]
    public void Sales_a_payout_paid_before_its_records_are_read_leave_the_balance_and_the_days_to_come()
    {
        // Wednesday 4 Nov: Monday 2 Nov's Türkiye payout is made but its records are read a day later, so they carry no
        // payout id yet. The sales due 2-4 Nov (its payout day, Monday 2 Nov) are paid; Thursday 5 Nov's is not; a
        // deduction stays open (a payout does not always take one).
        PayoutRow[] rows =
        [
            Sale("s1", 1000m, 800m, new(2026, 11, 2)),
            Sale("s2", 500m, 400m, new(2026, 11, 4)),
            Sale("s3", 300m, 240m, new(2026, 11, 5)),
            Deduction("k1", "Kargo Faturası", MarketplaceFinanceCategories.Cargo, 20m, new(2026, 11, 1))
        ];
        PayoutOrderRow[] payouts = [new(Store, "PO-8", 1200m, Due(new DateOnly(2026, 10, 29)), "TR", "Türkiye"), new(Store, "PO-9", 1180m, Due(new DateOnly(2026, 11, 2)), "TR", "Türkiye", LinesRead: false)];
        var view = PayoutBuilder.Build(rows, payouts, Stores, Today, 90, []);
        Assert.Equal((240m - 20m, 2), (view.Expected, view.ExpectedRecords));
        Assert.Equal((1200m, 2), (view.PaidUnread, view.PaidUnreadRecords));
        Assert.Equal(220m, view.Next!.Amount);

        // Once the payout is read, its records carry its id; nothing is held apart any more.
        var read = PayoutBuilder.Build(rows, [payouts[0], payouts[1] with { LinesRead = true }], Stores, Today, 90, []);
        Assert.Equal((0m, 0), (read.PaidUnread, read.PaidUnreadRecords));
    }
}
