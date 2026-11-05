using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Hakediş: the "Son Ödeme" card is the newest payout of any marketplace, the same one whatever order the payouts are
// read in; an unpaid record with no due day still counts in the balance and waits apart.
public sealed class PayoutsLatestPayoutTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);
    private static readonly Guid Ty = Guid.CreateVersion7();
    private static readonly Guid Hb = Guid.CreateVersion7();
    private static readonly Dictionary<Guid, PayoutStore> Stores = new()
    {
        [Ty] = new(Ty, "TRENDYOL", "Ravencia TY", false),
        [Hb] = new(Hb, "HEPSIBURADA", "Ravencia HB", false)
    };

    private static PayoutRow TySale(string id, decimal revenue, DateTimeOffset? due) => new()
    {
        ConnectionId = Ty,
        PlatformCode = "TRENDYOL",
        Source = TrendyolFinanceRequestContract.Settlements,
        ExternalId = id,
        RequestedType = "Sale",
        Category = MarketplaceFinanceCategories.Sale,
        TransactionDate = PayoutBuilder.DayStart(Today.AddDays(-10)),
        Credit = revenue * 1.25m,
        SellerRevenue = revenue,
        PaymentDate = due,
        Country = "Türkiye",
        Affiliate = "TRENDYOLTR"
    };

    [Fact]
    public void An_unpaid_record_without_a_due_day_counts_in_the_balance_and_waits_apart()
    {
        PayoutRow[] rows = [TySale("s1", 800m, PayoutBuilder.DayStart(Today.AddDays(2))), TySale("s2", 400m, null)];

        var view = PayoutBuilder.Build(rows, [], Stores, Today, 90, []);

        Assert.True(PayoutBuilder.Open(PayoutBuilder.Line(rows[1], Today)));
        Assert.Equal((1200m, 2), (view.Expected, view.ExpectedRecords));
        var undated = Assert.Single(view.Upcoming, x => x.Kind == PayoutBuilder.Undated);
        Assert.Equal((400m, 1), (undated.Amount, undated.Records));
        // Never in a payout's estimate, never "overdue".
        Assert.Equal(800m, view.Next!.Amount);
        Assert.Equal(0m, view.Unknown);
    }

    [Fact]
    public void Last_paid_is_the_newest_payout_of_any_marketplace()
    {
        // Live 3-4 Nov 2026: Trendyol TR paid 16,628.95 on 2 Nov, Hepsiburada 8,173.54 (TotalPayment) on 3 Nov.
        var hbPaid = new PayoutRow
        {
            ConnectionId = Hb,
            PlatformCode = "HEPSIBURADA",
            Source = HepsiburadaFinanceSyncJobHandler.Source,
            ExternalId = "hb1",
            RequestedType = "x",
            TransactionType = "Payment",
            TransactionSubType = "Paid",
            Category = MarketplaceFinanceCategories.Sale,
            TransactionDate = PayoutBuilder.DayStart(new DateOnly(2026, 10, 20)),
            Credit = 8173.54m,
            PaymentDate = PayoutBuilder.DayStart(new DateOnly(2026, 11, 3))
        };
        var hbTotal = hbPaid with { ExternalId = "hbT", TransactionType = "TotalPayment" };
        PayoutOrderRow[] payouts = [new(Ty, "78542975", 16628.95m, new DateTimeOffset(2026, 11, 2, 1, 54, 18, TimeSpan.Zero), "TR", "Türkiye, Körfez")];

        var view = PayoutBuilder.Build([hbPaid, hbTotal], payouts, Stores, Today, 90, []);

        Assert.Equal(("HEPSIBURADA", new DateOnly(2026, 11, 3), 8173.54m), (view.LastPaid!.PlatformCode, view.LastPaid.Day, view.LastPaid.Amount));
    }

    [Fact]
    public void Two_payouts_on_one_day_show_the_same_one_in_any_read_order()
    {
        // Live 5 Nov 2026: Trendyol AZ 399.63 and TR 25,118.00 on the same day; the query has no order.
        PayoutOrderRow az = new(Ty, "78663979", 399.63m, new DateTimeOffset(2026, 11, 5, 2, 36, 42, TimeSpan.Zero), "AZ", "Azerbaycan");
        PayoutOrderRow tr = new(Ty, "78619772", 25118m, new DateTimeOffset(2026, 11, 5, 1, 35, 55, TimeSpan.Zero), "TR", "Türkiye, Körfez");
        var today = new DateOnly(2026, 11, 5);

        var first = PayoutBuilder.Build([], [az, tr], Stores, today, 90, []);
        var second = PayoutBuilder.Build([], [tr, az], Stores, today, 90, []);

        Assert.Equal((25118m, 25118m), (first.LastPaid!.Amount, second.LastPaid!.Amount));
        Assert.Equal(first.Past.Select(x => x.Key), second.Past.Select(x => x.Key));
    }
}
