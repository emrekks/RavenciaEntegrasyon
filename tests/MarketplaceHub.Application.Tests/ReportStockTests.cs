using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Stok raporu: capital at cost, value at the panel price, the missing ones counted apart, the cover days at the
// period's pace and the dead stock.
public sealed class ReportStockTests
{
    private static readonly DateOnly Today = new(2026, 10, 31);
    private static readonly DateTimeOffset Old = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Trendyol = Guid.NewGuid(), N11 = Guid.NewGuid();
    private static readonly Dictionary<Guid, ReportChannelView> Stores = new()
    {
        [Trendyol] = new(Trendyol, "TRENDYOL", "Trendyol", false),
        [N11] = new(N11, "N11", "n11", false)
    };

    private static ReportStockVariantRow Variant(Guid product, string color, string size, decimal onHand, decimal? cost, decimal? price = null, string category = "Bluz", DateTimeOffset? created = null) =>
        new(Guid.NewGuid(), product, "Bluz " + color, "M1", category, color, size, $"RENK:{color}|BEDEN:{size}", true, cost, 10m, onHand, onHand, created ?? Old, price);

    private static ReportStockOfferRow Offer(ReportStockVariantRow variant, Guid store, decimal price, string status = "ACTIVE") =>
        new(variant.VariantId, store, price, "TRY", status);

    [Fact]
    public void Capital_value_and_margin_count_only_what_is_known()
    {
        var product = Guid.NewGuid();
        var black = Variant(product, "Siyah", "M", 4m, 300m, 800m);
        var white = Variant(product, "Beyaz", "M", 2m, null, 900m);
        var empty = Variant(product, "Siyah", "L", 0m, 300m);
        // The stores' own prices value only their own rows; the stock is valued at the panel price.
        var offers = new[] { Offer(black, Trendyol, 790m), Offer(white, Trendyol, 880m), Offer(black, N11, 850m) };

        var view = ReportStock.Build([black, white, empty], offers, Stores, false, new Dictionary<Guid, decimal> { [black.VariantId] = 3m },
            new Dictionary<Guid, decimal> { [black.VariantId] = 3m }, Today.AddDays(-29), Today, Today);

        var summary = view.Summary;
        Assert.Equal((6m, 1200m, 1, 2m), (summary.Units, summary.Capital, summary.MissingCostVariants, summary.MissingCostUnits));
        // 1,200 incl. 10 % VAT: 1,090.91 net.
        Assert.Equal(1090.91m, summary.CapitalExVat);
        Assert.Equal((5000m, 6m, 0m), (summary.Value!.Value, summary.PricedUnits, summary.MissingPriceUnits));
        // Profit over the black ones only (both cost and price): 4 × (800 − 300) = 2,000 of 3,200.
        Assert.Equal((2000m, 0.625m), (summary.Profit!.Value, summary.Margin!.Value));
        Assert.Equal((3, 2, 1), (summary.Variants, summary.StockedVariants, summary.EmptyVariants));
        // 3 kept in 30 days: 0.1 a day, 6 units last 60 days.
        Assert.Equal((3m, 0.1m, 60m), (summary.SoldUnits, summary.DailySales!.Value, summary.CoverDays!.Value));
        // The white one is old and did not sell in 90 days: dead, but with no cost its capital is not counted.
        Assert.Equal((1, 2m, 0m), (summary.DeadVariants, summary.DeadUnits, summary.DeadCapital));

        var n11 = view.Platforms.Single(x => x.ConnectionId == N11);
        Assert.Equal((4m, 2m, 3400m), (n11.ListedUnits, n11.UnlistedUnits, n11.Value!.Value));
        Assert.Equal(4920m, view.Platforms.Single(x => x.ConnectionId == Trendyol).Value);
    }

    [Fact]
    public void Breakdowns_sort_known_buckets_first_and_put_unpriced_stock_last()
    {
        var product = Guid.NewGuid();
        var cheap = Variant(product, "Siyah", "S", 2m, 100m, 450m);
        var dear = Variant(product, "Bej", "M", 1m, 500m, 1500m, category: "Elbise");
        var unpriced = Variant(product, "Bej", "L", 5m, 200m, category: "");

        var view = ReportStock.Build([cheap, dear, unpriced], [Offer(cheap, Trendyol, 450m), Offer(unpriced, Trendyol, 300m)], Stores, false,
            new Dictionary<Guid, decimal>(), new Dictionary<Guid, decimal>(), Today.AddDays(-6), Today, Today);

        Assert.Equal(["Bluz", "Elbise", "Bilinmiyor"], view.Categories.Select(x => x.Label));
        Assert.Equal(["500 TL altı", "1.500 – 2.000 TL", "Fiyatı Yok"], view.PriceBands.Select(x => x.Label));
        Assert.Equal(5m, view.PriceBands[^1].Units);
        Assert.Equal(["Bej", "Siyah"], view.Colors.Select(x => x.Label));
        // Nothing sold in the period: every stocked variant is in the last cover band.
        Assert.Equal(("unsold", 8m), (Assert.Single(view.Cover).Key, view.Cover[0].Units));
        var top = Assert.Single(view.TopCapital);
        Assert.Equal((8m, 1700m, 2400m), (top.Units, top.Capital!.Value, top.Value!.Value));
    }

    [Fact]
    public void A_platform_filter_counts_only_the_variants_on_sale_there()
    {
        var product = Guid.NewGuid();
        var listed = Variant(product, "Siyah", "M", 3m, 100m, 400m);
        var passive = Variant(product, "Siyah", "L", 7m, 100m, 400m);
        var n11Only = new Dictionary<Guid, ReportChannelView> { [N11] = Stores[N11] };

        var view = ReportStock.Build([listed, passive], [Offer(listed, N11, 400m), Offer(passive, N11, 400m, "INACTIVE"), Offer(passive, Trendyol, 400m)], n11Only, true,
            new Dictionary<Guid, decimal>(), new Dictionary<Guid, decimal>(), Today, Today, Today);

        Assert.Equal((3m, 300m, 1200m), (view.Summary.Units, view.Summary.Capital, view.Summary.Value!.Value));
        var store = Assert.Single(view.Platforms);
        Assert.Equal((1, 1, 3m), (store.Offers, store.ActiveOffers, store.ListedUnits));
    }

    [Fact]
    public void A_new_variant_is_not_dead_and_a_half_empty_colour_is_low_stock()
    {
        var product = Guid.NewGuid();
        var fresh = Variant(product, "Siyah", "M", 2m, 100m, created: new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
        var gone = Variant(product, "Siyah", "L", 0m, 100m);

        var view = ReportStock.Build([fresh, gone], [], Stores, false, new Dictionary<Guid, decimal>(), new Dictionary<Guid, decimal>(), Today, Today, Today);

        Assert.Equal(0, view.Summary.DeadVariants);
        Assert.Equal(1, view.Summary.LowStockProducts);
        Assert.Null(view.Summary.Value);
    }
}
