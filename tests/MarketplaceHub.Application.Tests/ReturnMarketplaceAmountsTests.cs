using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The Returns page shows the amounts the marketplace wrote for the returned lines, never ones calculated from the
// order's totals (owner, 30 Oct 2026: Trendyol return 11738971444 reads Satış 1.598,00 · İndirim 80,98 · Faturalanmış 1.517,02).
public sealed class ReturnMarketplaceAmountsTests
{
    // Live order line snapshots of Trendyol order 11738971444 (trimmed to the amount fields).
    private const string TrendyolFirstLine = """{"quantity":1,"amount":799.00,"lineGrossAmount":799.00,"discount":40.49,"lineTotalDiscount":40.49,"discountDetails":[{"lineItemId":"6972938408","lineItemPrice":758.51,"lineItemDiscount":40.49}],"price":758.51,"lineUnitPrice":758.51}""";
    private const string TrendyolSecondLine = """{"quantity":1,"amount":799.00,"lineGrossAmount":799.00,"discount":40.49,"lineTotalDiscount":40.49,"discountDetails":[{"lineItemId":"6972938410","lineItemPrice":758.51,"lineItemDiscount":40.49}],"price":758.51,"lineUnitPrice":758.51}""";

    [Fact]
    public void Trendyol_return_reads_the_sale_discount_and_invoiced_amounts_trendyol_shows()
    {
        var lines = new[]
        {
            ReturnMarketplaceAmounts.Line("TRENDYOL", TrendyolFirstLine, 1, "claim-1"),
            ReturnMarketplaceAmounts.Line("TRENDYOL", TrendyolSecondLine, 1, "claim-1")
        };

        Assert.Equal(new ReturnAmountsView(799.00m, 40.49m, 758.51m, null), lines[0]);
        Assert.Equal(new ReturnAmountsView(1598.00m, 80.98m, 1517.02m, null), ReturnMarketplaceAmounts.Total(lines));
    }

    [Fact]
    public void Trendyol_whole_line_sums_its_units_as_trendyol_split_them()
    {
        const string line = """{"quantity":2,"amount":699.00,"discount":41.66,"price":657.34,"discountDetails":[{"lineItemPrice":657.33,"lineItemDiscount":41.67},{"lineItemPrice":657.34,"lineItemDiscount":41.66}]}""";

        Assert.Equal(new ReturnAmountsView(1398.00m, 83.33m, 1314.67m, null), ReturnMarketplaceAmounts.Line("TRENDYOL", line, 2, "c"));
        // One of the two units: the per-unit figures.
        Assert.Equal(new ReturnAmountsView(699.00m, 41.66m, 657.34m, null), ReturnMarketplaceAmounts.Line("TRENDYOL", line, 1, "c"));
    }

    [Fact]
    public void Trendyol_line_built_from_a_claim_knows_only_the_invoiced_price()
    {
        const string claimLine = """{"id":5758367855,"barcode":"RYP00117","price":442.21,"vatBaseAmount":10,"vatRate":10,"lineItems":null}""";

        Assert.Equal(new ReturnAmountsView(null, null, 442.21m, null), ReturnMarketplaceAmounts.Line("TRENDYOL", claimLine, 1, "c"));
    }

    [Fact]
    public void Hepsiburada_reads_the_claims_refund_amount_only_from_that_claim()
    {
        const string claim = """{"number":"714708387","status":"Refunded","quantity":1,"refundAmount":865.72,"refundCurrency":"TRY","priceAmount":865.72}""";

        Assert.Equal(new ReturnAmountsView(null, null, null, 865.72m), ReturnMarketplaceAmounts.Line("HEPSIBURADA", claim, 1, "714708387"));
        Assert.Null(ReturnMarketplaceAmounts.Line("HEPSIBURADA", claim, 1, "999999999"));
        // An order line (no claim number) says nothing about the refund.
        Assert.Null(ReturnMarketplaceAmounts.Line("HEPSIBURADA", """{"lineItemId":"1","totalPrice":{"amount":865.72}}""", 1, "714708387"));
    }

    [Fact]
    public void N11_reads_the_line_figures_of_a_single_unit_and_only_the_sale_price_otherwise()
    {
        const string single = """{"quantity":1,"price":875,"sellerInvoiceAmount":875,"totalSellerDiscountPrice":0,"totalMallDiscountPrice":87.5}""";
        const string two = """{"quantity":2,"price":500,"sellerInvoiceAmount":900,"totalSellerDiscountPrice":100}""";

        Assert.Equal(new ReturnAmountsView(875m, 0m, 875m, null), ReturnMarketplaceAmounts.Line("N11", single, 1, "c"));
        Assert.Equal(new ReturnAmountsView(500m, null, null, null), ReturnMarketplaceAmounts.Line("N11", two, 1, "c"));
    }

    [Theory]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    [InlineData("SHOPIFY")]
    public void Platforms_without_return_amounts_stay_unknown(string platform) =>
        Assert.Null(ReturnMarketplaceAmounts.Line(platform, """{"price":100,"amount":120,"refundAmount":100}""", 1, "c"));

    [Fact]
    public void A_figure_one_line_lacks_is_unknown_for_the_whole_return()
    {
        var total = ReturnMarketplaceAmounts.Total([new ReturnAmountsView(799m, 40.49m, 758.51m, null), new ReturnAmountsView(null, null, 442.21m, null)]);

        Assert.Equal(new ReturnAmountsView(null, null, 1200.72m, null), total);
        Assert.Null(ReturnMarketplaceAmounts.Total([new ReturnAmountsView(799m, 40.49m, 758.51m, null), null]));
        Assert.Null(ReturnMarketplaceAmounts.Total([]));
        Assert.Null(ReturnMarketplaceAmounts.Line("TRENDYOL", "not json", 1, "c"));
        Assert.Null(ReturnMarketplaceAmounts.Line("TRENDYOL", null, 1, "c"));
    }
}
