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
    [InlineData("PAZARAMA", null, null, PayoutBuilder.TrGulf)]
    public void A_record_falls_in_its_storefront_region(string platform, string? country, string? affiliate, string expected) =>
        Assert.Equal(expected, PayoutBuilder.Region(platform, country, affiliate));

    [Fact]
    public void Expected_is_the_unpaid_seller_revenue_due_from_today_with_deductions_and_without_commission_invoices()
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
        Assert.Equal(800m - 200m - 20m + 400m, view.Expected);
        Assert.Equal(4, view.ExpectedRecords);
        Assert.Equal(240m, view.Unknown);
        Assert.Equal(Today.AddDays(1), view.Next!.Day);
        Assert.Equal(580m, view.Next.Amount);
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
}
