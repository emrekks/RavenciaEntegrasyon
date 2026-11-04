using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A "Stoğa Ekle" kept while the marketplace's answer to an approval was unclear: applied once the line is approved
// (by the panel's call or by the marketplace itself), dropped when the line is rejected or cancelled or the decision
// clearly did not reach the marketplace, kept waiting otherwise.
public sealed class ReturnPendingStockPolicyTests
{
    [Theory]
    // Confirmed later: a read shows the line accepted, whatever state the unclear decision is in.
    [InlineData("MANUAL_REVIEW", "EXTERNAL_EFFECT_VERIFYING", ReturnClaimStatus.Approved, "Accepted", ReturnPendingStockVerdict.Apply)]
    [InlineData("SUCCEEDED", null, ReturnClaimStatus.Approved, "Accepted", ReturnPendingStockVerdict.Apply)]
    [InlineData("FAILED", "EXTERNAL_EFFECT_UNRESOLVED", ReturnClaimStatus.Approved, "Accepted", ReturnPendingStockVerdict.Apply)]
    // The marketplace approved the claim itself (Trendyol auto-approval): the line takes stock.
    [InlineData("SUBMITTED", null, ReturnClaimStatus.Approved, null, ReturnPendingStockVerdict.Apply)]
    [InlineData("SUBMITTED", null, ReturnClaimStatus.Completed, null, ReturnPendingStockVerdict.Apply)]
    // Still unclear: the line waits for the seller, or is on its way.
    [InlineData("MANUAL_REVIEW", "EXTERNAL_EFFECT_VERIFYING", ReturnClaimStatus.ActionRequired, "WaitingInAction", ReturnPendingStockVerdict.Wait)]
    [InlineData("MANUAL_REVIEW", "EXTERNAL_EFFECT_VERIFYING", ReturnClaimStatus.Disputed, "Unresolved", ReturnPendingStockVerdict.Wait)]
    // Rejected or cancelled: no stock.
    [InlineData("MANUAL_REVIEW", "RETURN_ACTION_READBACK_CONFLICT", ReturnClaimStatus.Rejected, "Rejected", ReturnPendingStockVerdict.Drop)]
    [InlineData("MANUAL_REVIEW", "EXTERNAL_EFFECT_VERIFYING", ReturnClaimStatus.Rejected, null, ReturnPendingStockVerdict.Drop)]
    [InlineData("MANUAL_REVIEW", "EXTERNAL_EFFECT_VERIFYING", ReturnClaimStatus.Cancelled, "Accepted", ReturnPendingStockVerdict.Drop)]
    // A decision that clearly did not reach the marketplace drops its choice even on a later approval.
    [InlineData("FAILED", "EXTERNAL_EFFECT_NOT_APPLIED", ReturnClaimStatus.Approved, "Accepted", ReturnPendingStockVerdict.Drop)]
    [InlineData("FAILED", "TRENDYOL_VALIDATION", ReturnClaimStatus.Approved, "Accepted", ReturnPendingStockVerdict.Drop)]
    public void A_pending_stock_choice_follows_the_line_outcome(string decisionStatus, string? errorCode, ReturnClaimStatus claimStatus, string? lineStatus, ReturnPendingStockVerdict expected) =>
        Assert.Equal(expected, ReturnPendingStockPolicy.Settle(decisionStatus, errorCode, claimStatus, lineStatus));

    [Fact]
    public void A_line_rejected_on_an_approved_claim_takes_no_stock()
    {
        Assert.Equal(ReturnPendingStockVerdict.Drop, ReturnPendingStockPolicy.Settle("SUBMITTED", null, ReturnClaimStatus.Approved, "Rejected"));
        Assert.Equal(ReturnPendingStockVerdict.Wait, ReturnPendingStockPolicy.Settle("SUBMITTED", null, ReturnClaimStatus.Approved, "WaitingInAction"));
    }
}
