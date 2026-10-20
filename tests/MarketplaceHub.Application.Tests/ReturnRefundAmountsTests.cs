using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-10-19 İ13: a return's amount is calculated from its order lines.
public sealed class ReturnRefundAmountsTests
{
    [Fact]
    public void Without_a_discount_a_returned_unit_is_worth_its_unit_price()
    {
        var ratio = ReturnRefundAmounts.DiscountRatio(300m, 0m, 300m, 0m);

        Assert.Equal(0m, ratio);
        Assert.Equal(200m, ReturnRefundAmounts.LineRefund(2m, 100m, ratio));
    }

    [Fact]
    public void A_partial_return_refunds_only_the_returned_quantity()
    {
        // Three units ordered, one returned.
        Assert.Equal(49.90m, ReturnRefundAmounts.LineRefund(1m, 49.90m, 0m));
    }

    [Fact]
    public void The_order_discount_is_spread_over_the_lines_like_the_invoice_spreads_it()
    {
        // 1 × 100 + 2 × 50 = 200 gross, 40 discount: every line pays 80 % of its gross.
        var ratio = ReturnRefundAmounts.DiscountRatio(200m, 40m, 160m, 0m);

        Assert.Equal(0.2m, ratio);
        Assert.Equal(80m, ReturnRefundAmounts.PaidUnitPrice(100m, ratio));
        Assert.Equal(40m, ReturnRefundAmounts.LineRefund(1m, 50m, ratio));
        // Returning everything gives back exactly what was paid.
        Assert.Equal(160m, ReturnRefundAmounts.LineRefund(1m, 100m, ratio) + ReturnRefundAmounts.LineRefund(2m, 50m, ratio));
    }

    [Fact]
    public void A_discount_known_only_from_the_payable_total_is_used_and_a_missing_payable_is_not_read_as_a_full_discount()
    {
        Assert.Equal(0.25m, ReturnRefundAmounts.DiscountRatio(400m, 0m, 300m, 0m));
        Assert.Equal(0m, ReturnRefundAmounts.DiscountRatio(400m, 0m, 0m, 0m));
        // No order gross: the active lines' gross stands in for it.
        Assert.Equal(0.1m, ReturnRefundAmounts.DiscountRatio(0m, 10m, 90m, 100m));
        Assert.Equal(0m, ReturnRefundAmounts.DiscountRatio(0m, 10m, 90m, 0m));
    }

    [Fact]
    public void Amounts_are_rounded_to_kurus_and_never_negative()
    {
        var ratio = ReturnRefundAmounts.DiscountRatio(300m, 100m, 200m, 0m);

        Assert.Equal(66.67m, ReturnRefundAmounts.LineRefund(1m, 100m, ratio));
        Assert.Equal(0m, ReturnRefundAmounts.LineRefund(-1m, 100m, ratio));
        Assert.Equal(1m, ReturnRefundAmounts.DiscountRatio(100m, 150m, 0m, 0m));
    }

    [Fact]
    public void The_claim_total_adds_the_lines_with_an_amount_and_is_empty_without_any()
    {
        Assert.Equal(120m, ReturnRefundAmounts.ClaimTotal([80m, null, 40m]));
        Assert.Null(ReturnRefundAmounts.ClaimTotal([null, null]));
        Assert.Null(ReturnRefundAmounts.ClaimTotal([]));
    }
}
