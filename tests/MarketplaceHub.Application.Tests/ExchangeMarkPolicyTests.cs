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
    public void The_mark_lapses_once_the_claim_is_no_longer_rejected_and_only_a_Hepsiburada_exchange_is_no_return()
    {
        Assert.False(ReturnCustomerRequestPolicy.ManualMarkLapses("MANUAL", ReturnClaimStatus.Rejected));
        Assert.True(ReturnCustomerRequestPolicy.ManualMarkLapses("MANUAL", ReturnClaimStatus.Approved));
        Assert.False(ReturnCustomerRequestPolicy.ManualMarkLapses(null, ReturnClaimStatus.Approved));
        // Review, 18 Nov 2026: a Trendyol exchange request accepted counts as a return (it may be a refund).
        Assert.True(ReturnCustomerRequestPolicy.CountsAsReturn("CHANGE", "Accepted", "TRENDYOL"));
        Assert.False(ReturnCustomerRequestPolicy.CountsAsReturn("CHANGE", "Accepted", "HEPSIBURADA"));
        Assert.True(ReturnCustomerRequestPolicy.CountsAsReturn("CHANGE", "Refunded", "HEPSIBURADA"));
    }

    [Theory]
    [InlineData(ReturnClaimStatus.Rejected, "Rejected", true, true)]
    [InlineData(ReturnClaimStatus.Rejected, "Rejected", false, false)]
    [InlineData(ReturnClaimStatus.Approved, "Accepted", false, true)]
    public void Stock_stays_open_on_an_exchange_made_and_a_plain_rejection_keeps_its_rule(ReturnClaimStatus status, string line, bool exchangeMade, bool allowed) =>
        Assert.Equal(allowed, ReturnCustomerRequestPolicy.StockDispositionAllowed(status, line, exchangeMade));
}
