using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 18 Nov 2026: the panel-only "Değişim Onaylandı" mark of a Trendyol exchange request Trendyol answered Rejected.
public sealed class ExchangeMarkPolicyTests
{
    [Theory]
    [InlineData("TRENDYOL", "CHANGE", ReturnClaimStatus.Rejected, null, true)]
    [InlineData("TRENDYOL", "CHANGE", ReturnClaimStatus.Rejected, "CHANGE", false)]
    [InlineData("TRENDYOL", "CHANGE", ReturnClaimStatus.Approved, null, false)]
    [InlineData("TRENDYOL", null, ReturnClaimStatus.Rejected, null, false)]
    [InlineData("HEPSIBURADA", "CHANGE", ReturnClaimStatus.Rejected, null, false)]
    public void The_mark_is_offered_for_a_rejected_Trendyol_exchange_request_without_an_outcome(string platform, string? request, ReturnClaimStatus status, string? resolvedWith, bool allowed) =>
        Assert.Equal(allowed, ReturnCustomerRequestPolicy.ManualExchangeMarkAllowed(platform, request, status, resolvedWith));

    [Fact]
    public void A_read_keeps_the_mark_unless_it_names_an_outcome_and_a_refund_over_it_is_reported()
    {
        // Nothing named: the owner's mark stays.
        Assert.Equal(("CHANGE", "MANUAL", false), ReturnCustomerRequestPolicy.AfterRead("CHANGE", "MANUAL", null, "Rejected"));
        // The marketplace confirms the exchange: its own word, no longer the owner's.
        Assert.Equal(("CHANGE", null, false), ReturnCustomerRequestPolicy.AfterRead("CHANGE", "MANUAL", "CHANGE", "Rejected"));
        // A refund over the mark: a return again, reported.
        Assert.Equal(("REFUND", null, true), ReturnCustomerRequestPolicy.AfterRead("CHANGE", "MANUAL", "REFUND", "Rejected"));
        Assert.Equal(("REFUND", null, true), ReturnCustomerRequestPolicy.AfterRead("CHANGE", "MANUAL", null, "Refunded"));
        // Without a mark the raw status Refunded writes nothing (ResolvedWith stays the marketplace's own field).
        Assert.Equal((null, null, false), ReturnCustomerRequestPolicy.AfterRead(null, null, null, "Refunded"));
        Assert.True(ReturnCustomerRequestPolicy.CountsAsReturn("CHANGE", "Rejected", "REFUND"));
    }

    [Theory]
    [InlineData(ReturnClaimStatus.Rejected, "Rejected", true, true)]
    [InlineData(ReturnClaimStatus.Rejected, "Rejected", false, false)]
    [InlineData(ReturnClaimStatus.Approved, "Accepted", false, true)]
    public void Stock_stays_open_on_an_exchange_made_and_a_plain_rejection_keeps_its_rule(ReturnClaimStatus status, string line, bool exchangeMade, bool allowed) =>
        Assert.Equal(allowed, ReturnCustomerRequestPolicy.StockDispositionAllowed(status, line, exchangeMade));
}
