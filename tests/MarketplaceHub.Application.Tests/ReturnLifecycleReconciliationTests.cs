using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReturnLifecycleReconciliationTests
{
    [Theory]
    [InlineData(ReturnClaimStatus.Approved, ReturnClaimStatus.Rejected, true)]
    [InlineData(ReturnClaimStatus.Rejected, ReturnClaimStatus.Approved, true)]
    [InlineData(ReturnClaimStatus.InTransit, ReturnClaimStatus.Rejected, true)]
    [InlineData(ReturnClaimStatus.Completed, ReturnClaimStatus.Approved, false)]
    [InlineData(ReturnClaimStatus.Cancelled, ReturnClaimStatus.Approved, false)]
    [InlineData(ReturnClaimStatus.Approved, ReturnClaimStatus.ActionRequired, false)]
    [InlineData(ReturnClaimStatus.Approved, ReturnClaimStatus.Approved, false)]
    public void Marketplace_outcome_is_authoritative_but_never_reopens_a_closed_claim(ReturnClaimStatus current, ReturnClaimStatus remote, bool expected) =>
        Assert.Equal(expected, ReturnClaimStateMachine.IsAuthoritativeRemoteOutcome(current, remote));

    [Fact]
    public void Unclear_decision_is_settled_by_the_claim_status()
    {
        Assert.Equal(("SUCCEEDED", (string?)null), MarketplaceJobProcessor.ReconciledReturnDecision("APPROVE", "MANUAL_REVIEW", ReturnClaimStatus.Approved, "Accepted"));
        Assert.Equal(("MANUAL_REVIEW", "RETURN_ACTION_READBACK_CONFLICT"), MarketplaceJobProcessor.ReconciledReturnDecision("APPROVE", "MANUAL_REVIEW", ReturnClaimStatus.Cancelled, "Cancelled"));
        // Still waiting: nothing changes, it is not promoted to "submitted".
        Assert.Null(MarketplaceJobProcessor.ReconciledReturnDecision("APPROVE", "MANUAL_REVIEW", ReturnClaimStatus.ActionRequired, "AwaitingAction"));
    }

    [Fact]
    public void Unresolved_decision_only_moves_on_to_a_confirmed_outcome()
    {
        Assert.Equal(("SUCCEEDED", (string?)null), MarketplaceJobProcessor.ReconciledReturnDecision("REJECT", "FAILED", ReturnClaimStatus.Rejected, "Rejected"));
        Assert.Null(MarketplaceJobProcessor.ReconciledReturnDecision("REJECT", "FAILED", ReturnClaimStatus.ActionRequired, "AwaitingAction"));
        Assert.Null(MarketplaceJobProcessor.ReconciledReturnDecision("REJECT", "FAILED", ReturnClaimStatus.Approved, "Accepted"));
    }

    [Fact]
    public void Sent_decision_keeps_its_previous_reconciliation()
    {
        Assert.Equal(("SUBMITTED", (string?)null), MarketplaceJobProcessor.ReconciledReturnDecision("APPROVE", "PENDING", ReturnClaimStatus.ActionRequired, "WaitingInAction"));
        Assert.Equal(("SUCCEEDED", (string?)null), MarketplaceJobProcessor.ReconciledReturnDecision("PREAPPROVAL_CONFIRM", "SUBMITTED", ReturnClaimStatus.ActionRequired, "AwaitingAction"));
    }

    [Fact]
    public void Hepsiburada_follow_up_reads_claims_sent_for_review()
    {
        Assert.Contains("PREAPPROVAL_CONFIRM_SUBMITTED", MarketplaceJobProcessor.HepsiburadaOpenClaimStatuses);
    }

    [Fact]
    public void Rejection_evidence_follows_the_marketplace_reason_list()
    {
        IReadOnlyList<ReturnIssueReason> reasons = [new("10", "İade paketi boş geldi", false), new("2", "Gelen ürün defolu", true)];

        Assert.False(MarketplaceSalesService.RejectEvidenceRequired("10", reasons, "PAZARAMA"));
        Assert.True(MarketplaceSalesService.RejectEvidenceRequired("2", reasons, "PAZARAMA"));
        // A Trendyol reason id means nothing on another marketplace.
        Assert.True(MarketplaceSalesService.RejectEvidenceRequired("1651", reasons, "PAZARAMA"));
        Assert.False(MarketplaceSalesService.RejectEvidenceRequired("1651", null, "TRENDYOL"));
        Assert.True(MarketplaceSalesService.RejectEvidenceRequired("1651", null, "N11"));
    }

    [Fact]
    public void Derived_return_deadline_is_marked_as_an_estimate()
    {
        var modifiedAt = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);
        var derived = new ReturnClaim { ExternalClaimId = "c1", RawStatus = "WaitingInAction", LastRemoteModifiedAt = modifiedAt };
        var storedEstimate = new ReturnClaim { ExternalClaimId = "c2", RawStatus = "Created", ActionDueAt = modifiedAt.AddDays(7), ActionDueAtEstimated = true, LastRemoteModifiedAt = modifiedAt };
        var fromMarketplace = new ReturnClaim { ExternalClaimId = "c3", RawStatus = "WaitingInAction", ActionDueAt = modifiedAt.AddHours(24), LastRemoteModifiedAt = modifiedAt };

        Assert.True(MarketplaceSalesService.IsEstimatedReturnDueAt("TRENDYOL", derived));
        Assert.True(MarketplaceSalesService.IsEstimatedReturnDueAt("TRENDYOL", storedEstimate));
        Assert.False(MarketplaceSalesService.IsEstimatedReturnDueAt("TRENDYOL", fromMarketplace));
        Assert.False(MarketplaceSalesService.IsEstimatedReturnDueAt("HEPSIBURADA", derived));
    }

    [Fact]
    public void New_claim_keeps_the_estimate_flag()
    {
        var remote = new RemoteReturnClaim("c1", "o1", "WaitingInAction", null, null, DateTimeOffset.UnixEpoch.AddHours(48), DateTimeOffset.UnixEpoch, [], "{}", ActionDueAtEstimated: true);

        Assert.True(MarketplaceJobProcessor.NewReturnClaim(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), remote, ReturnClaimStatus.ActionRequired, DateTimeOffset.UnixEpoch).ActionDueAtEstimated);
    }
}
