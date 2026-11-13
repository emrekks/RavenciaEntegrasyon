using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Trendyol's Ödeme Özeti of Saturday 7 Nov 2026 against the Hakediş: the shapes of the live records (amounts as they
// were, order and package numbers made up).
public sealed class PayoutTrendyolEstimateTests
{
    private static readonly DateOnly Today = new(2026, 11, 7);
    private static readonly Guid Store = Guid.CreateVersion7();
    private static readonly Dictionary<Guid, PayoutStore> Stores = new() { [Store] = new(Store, "TRENDYOL", "Ravencia", false) };

    private static PayoutRow Line(string id, string package, string type, decimal credit, decimal debt, decimal revenue, DateOnly due, string country = "Türkiye", string affiliate = "TRENDYOLTR") => new()
    {
        ConnectionId = Store,
        PlatformCode = "TRENDYOL",
        Source = TrendyolFinanceRequestContract.Settlements,
        ExternalId = id,
        RequestedType = type,
        Category = type switch { "Sale" => MarketplaceFinanceCategories.Sale, "Return" => MarketplaceFinanceCategories.Return, "Coupon" => MarketplaceFinanceCategories.Coupon, _ => MarketplaceFinanceCategories.Discount },
        TransactionDate = PayoutBuilder.DayStart(due.AddDays(-28)),
        OrderNumber = $"O-{package}",
        ShipmentPackageId = package,
        Barcode = "B-1",
        Credit = credit,
        Debt = debt,
        SellerRevenue = revenue,
        PaymentDate = PayoutBuilder.DayStart(due),
        Country = country,
        Affiliate = affiliate
    };

    private static PayoutRow Invoice(string id, string type, string category, decimal debt, DateOnly due) => new()
    {
        ConnectionId = Store,
        PlatformCode = "TRENDYOL",
        Source = TrendyolFinanceRequestContract.OtherFinancials,
        ExternalId = id,
        RequestedType = TrendyolFinanceCategorizer.DeductionInvoices,
        TransactionType = type,
        Category = category,
        TransactionDate = PayoutBuilder.DayStart(due),
        InvoiceNumber = id,
        Debt = debt,
        PaymentDate = PayoutBuilder.DayStart(due),
        Country = "Türkiye",
        Affiliate = "TRENDYOLTR"
    };

    private static PayoutForecast Forecast(HashSet<(Guid, string)>? shippingBilled = null, Dictionary<(Guid, string, string), decimal>? vat = null) =>
        new(new Dictionary<Guid, decimal> { [Store] = 98.34m }, shippingBilled ?? [], new HashSet<(Guid, string)>(), vat ?? []);

    private static readonly DateOnly Monday9 = new(2026, 11, 9);
    private static readonly DateOnly Thursday12 = new(2026, 11, 12);

    // Azerbaycan: Monday 9 Nov took the sales due 9-11 Nov, Thursday 12 Nov those due 12-15 Nov (Trendyol: 858.08 and 209.08).
    private static PayoutRow[] Azerbaijan() =>
    [
        Line("a1", "P1", "Sale", 899m, 0m, 705.72m, Monday9, affiliate: "TRENDYOLAZJV", country: "Azerbaycan"),
        Line("a2", "P1", "Discount", 0m, 109.12m, 85.67m, Monday9, affiliate: "TRENDYOLAZJV", country: "Azerbaycan"),
        Line("a3", "P2", "Sale", 799m, 0m, 627.21m, Monday9.AddDays(2), affiliate: "TRENDYOLAZJV", country: "Azerbaycan"),
        Line("a4", "P2", "Discount", 0m, 96.98m, 76.13m, Monday9.AddDays(2), affiliate: "TRENDYOLAZJV", country: "Azerbaycan"),
        Line("a5", "P2", "Coupon", 0m, 6.73m, 5.28m, Monday9.AddDays(2), affiliate: "TRENDYOLAZJV", country: "Azerbaycan"),
        Line("a6", "P3", "Sale", 525m, 0m, 412.12m, Thursday12, affiliate: "TRENDYOLAZJV", country: "Azerbaycan"),
        Line("a7", "P3", "Discount", 0m, 85.81m, 67.36m, Thursday12, affiliate: "TRENDYOLAZJV", country: "Azerbaycan")
    ];

    [Fact]
    public void Azerbaycan_is_paid_on_mondays_and_thursdays_as_turkiye_less_its_cargo_platform_fee_and_international_fee()
    {
        var view = PayoutBuilder.Build(Azerbaijan(), [], Stores, Today, 90, [], forecast: Forecast());
        var monday = Assert.Single(view.Upcoming, x => x.Day == Monday9);
        var thursday = Assert.Single(view.Upcoming, x => x.Day == Thursday12);
        Assert.Equal((PayoutBuilder.Azerbaijan, "PAYOUT_DAY"), (monday.Region, monday.Kind));
        // The records: 620.05 due Monday + 545.80 due Wednesday; less 2 parcels' cargo (98.34), 2 platform fees (10.99 on
        // Azerbaycan) and 6 % of the sales net of discounts (89.11).
        Assert.Equal(1165.85m, monday.LinesAmount);
        Assert.Equal(858.08m, monday.Amount);
        Assert.Equal(209.08m, thursday.Amount);
        Assert.DoesNotContain(monday.Estimates!, x => x.Kind == "STOPPAGE");
        Assert.Equal(89.11m, Assert.Single(monday.Estimates!, x => x.Kind == "INTERNATIONAL_FEE").Amount);
        // The balance stays the records not paid yet.
        Assert.Equal(1165.85m + 344.76m, view.Expected);
    }

    [Fact]
    public void A_turkiye_payout_day_loses_cargo_platform_fee_withholding_tax_and_the_gulf_international_fee()
    {
        PayoutRow[] rows =
        [
            Line("t1", "P1", "Sale", 1100m, 0m, 863.50m, Thursday12),
            Line("t2", "P2", "Sale", 525m, 0m, 412.12m, Thursday12.AddDays(1), country: "Suudi Arabistan"),
            Line("t3", "P2", "Discount", 0m, 63.72m, 50.02m, Thursday12.AddDays(1), country: "Suudi Arabistan"),
            // A return: its revenue back, and its return parcel.
            Line("t4", "P3", "Sale", 599m, 0m, 470.21m, Thursday12.AddDays(3)),
            Line("t5", "P3", "Return", 0m, 599m, 470.21m, Thursday12.AddDays(3))
        ];
        var vat = new Dictionary<(Guid, string, string), decimal> { [(Store, "O-P1", "B-1")] = 10m, [(Store, "O-P3", "B-1")] = 10m };
        var day = Assert.Single(PayoutBuilder.Build(rows, [], Stores, Today, 90, [], forecast: Forecast(vat: vat)).Upcoming);
        Assert.Equal(1225.60m, day.LinesAmount);
        var estimate = day.Estimates!.ToDictionary(x => x.Kind, x => x.Amount);
        Assert.Equal(3 * 98.34m, estimate["CARGO"]);
        Assert.Equal(98.34m, estimate["RETURN_CARGO"]);
        Assert.Equal(3 * 13.19m, estimate["PLATFORM_FEE"]);
        Assert.Equal(27.68m, estimate["INTERNATIONAL_FEE"]);
        // 1 % of 1100 without its 10 % VAT (the returned sale nets to nothing) + 1 % of the Gulf sale net of its discount.
        Assert.Equal(10.00m + 4.61m, estimate["STOPPAGE"]);
        Assert.Equal(1225.60m - 295.02m - 98.34m - 39.57m - 27.68m - 14.61m, day.Amount);
    }

    [Fact]
    public void A_sale_without_its_vat_rate_is_left_out_of_the_withholding_tax_and_counted_as_missing()
    {
        var day = Assert.Single(PayoutBuilder.Build([Line("t1", "P1", "Sale", 1100m, 0m, 863.50m, Thursday12)], [], Stores, Today, 90, [], forecast: Forecast()).Upcoming);
        Assert.DoesNotContain(day.Estimates!, x => x.Kind == "STOPPAGE");
    }

    [Fact]
    public void Billed_cargo_and_a_kind_already_invoiced_for_the_day_are_not_estimated_again()
    {
        PayoutRow[] rows =
        [
            Line("t1", "P1", "Sale", 1100m, 0m, 863.50m, Monday9),
            Line("t2", "P2", "Sale", 1100m, 0m, 863.50m, Monday9),
            // Billed the evening before Monday's payout: the platform fee is no longer an estimate.
            Invoice("f1", "Platform Hizmet Bedeli", MarketplaceFinanceCategories.ServiceFee, 26.38m, Monday9.AddDays(-1))
        ];
        var day = Assert.Single(PayoutBuilder.Build(rows, [], Stores, Today, 90, [], forecast: Forecast(shippingBilled: [(Store, "O-P1")])).Upcoming);
        Assert.Equal(1727m - 26.38m, day.LinesAmount);
        Assert.Equal((1, 98.34m), Assert.Single(day.Estimates!, x => x.Kind == "CARGO") is var cargo ? (cargo.Count, cargo.Amount) : default);
        Assert.DoesNotContain(day.Estimates!, x => x.Kind == "PLATFORM_FEE");
    }

    [Fact]
    public void Avrupa_on_its_monthly_payout_loses_the_unbilled_parcels_cargo_and_fee_and_its_international_fee_and_withholding_tax()
    {
        // Live, 11 Nov 2026: a Greek parcel billed already (its cargo invoice stands in Avrupa), a Romanian one not yet; all
        // due before the payout and none past its due day (a record past it waits apart, out of the payout).
        var billed = Line("g1", "P1", "Sale", 1000m, 0m, 785m, new DateOnly(2026, 11, 13), country: "Yunanistan");
        var open = Line("r1", "P2", "Sale", 500m, 0m, 392.50m, new DateOnly(2026, 11, 20), country: "Romanya");
        var cargo = Invoice("k1", "Kargo Fatura", MarketplaceFinanceCategories.Cargo, 98.34m, new DateOnly(2026, 11, 12)) with { RegionOverride = PayoutBuilder.Europe };
        var view = PayoutBuilder.Build([billed, open, cargo], [], Stores, new DateOnly(2026, 11, 11), 90, [], forecast: Forecast(shippingBilled: [(Store, "O-P1")]));
        var day = Assert.Single(view.Upcoming);
        Assert.Equal((new DateOnly(2026, 11, 23), PayoutBuilder.Europe), (day.Day, day.Region));
        Assert.Equal([("CARGO", 98.34m), ("PLATFORM_FEE", 13.19m), ("INTERNATIONAL_FEE", 90m), ("STOPPAGE", 15m)], day.Estimates!.Select(x => (x.Kind, x.Amount)));
        Assert.Equal(785m + 392.50m - 98.34m - 98.34m - 13.19m - 90m - 15m, day.Amount);
    }

    [Fact]
    public void A_deduction_a_later_payout_of_its_region_left_out_waits_apart()
    {
        // The cargo invoice of Avrupa parcels written "Türkiye", due Thursday 29 Oct, skipped by the payouts up to 5 Nov.
        var skipped = Invoice("k1", "Kargo Fatura", MarketplaceFinanceCategories.Cargo, 393.36m, new DateOnly(2026, 10, 29));
        PayoutOrderRow[] payouts = [new(Store, "PO-1", 25118m, PayoutBuilder.DayStart(new DateOnly(2026, 11, 5)), "TR", "Türkiye, Körfez")];
        var view = PayoutBuilder.Build([skipped, Line("t1", "P1", "Sale", 1100m, 0m, 863.50m, Monday9)], payouts, Stores, Today, 90, []);
        Assert.Equal(863.50m, view.Next!.Amount);
        Assert.Equal(-393.36m, Assert.Single(view.Upcoming, x => x.Kind == "OVERDUE").Amount);
        // Without a payout read since, the deduction is still past its due day: it counts in Vadesi Geçmiş Bekleyen alone,
        // never in Sıradaki Ödeme too (owner, 13 Nov 2026).
        Assert.Equal(863.50m, PayoutBuilder.Build([skipped, Line("t1", "P1", "Sale", 1100m, 0m, 863.50m, Monday9)], [], Stores, Today, 90, []).Next!.Amount);
        // A cargo invoice whose parcels are Avrupa orders stands in Avrupa.
        Assert.Equal(PayoutBuilder.Europe, PayoutBuilder.Line(skipped with { RegionOverride = PayoutBuilder.Europe }, Today).Region);
    }
}
