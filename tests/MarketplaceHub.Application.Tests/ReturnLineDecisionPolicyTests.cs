using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Order findings S-2: a Trendyol return is decided per line. Approving two of three lines leaves the third open
// for its own decision, a decision is settled by its own lines, and stock comes back only on approved lines.
public sealed class ReturnLineDecisionPolicyTests
{
    private static readonly Guid First = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid Second = Guid.Parse("01900000-0000-7000-8000-0000000000a2");
    private static readonly Guid Third = Guid.Parse("01900000-0000-7000-8000-0000000000a3");

    [Fact]
    public void An_open_decision_holds_only_the_lines_it_covers()
    {
        Assert.True(ReturnDecisionLinePolicy.Overlaps([Second, Third], [Third]));
        Assert.False(ReturnDecisionLinePolicy.Overlaps([Second, Third], [First]));
        // A decision recorded before its lines were kept covers the whole claim.
        Assert.True(ReturnDecisionLinePolicy.Overlaps(null, [First]));
        Assert.True(ReturnDecisionLinePolicy.Overlaps([], [First]));
    }

    [Fact]
    public void A_decision_is_judged_by_its_own_lines_not_by_the_first_line_of_the_claim()
    {
        // Trendyol's claim status is its first item's: the undecided line still waits.
        Assert.Equal(ReturnClaimStatus.Approved, ReturnDecisionLinePolicy.Outcome(ReturnClaimStatus.ActionRequired, ["Accepted", "Accepted"]));
        Assert.Equal(ReturnClaimStatus.Rejected, ReturnDecisionLinePolicy.Outcome(ReturnClaimStatus.ActionRequired, ["Rejected"]));
        Assert.Equal(ReturnClaimStatus.ActionRequired, ReturnDecisionLinePolicy.Outcome(ReturnClaimStatus.Approved, ["Accepted", "WaitingInAction"]));
        // A line the marketplace did not report leaves the claim-level status to decide, as before.
        Assert.Equal(ReturnClaimStatus.ActionRequired, ReturnDecisionLinePolicy.Outcome(ReturnClaimStatus.ActionRequired, ["Accepted", null]));
        Assert.Equal(ReturnClaimStatus.Completed, ReturnDecisionLinePolicy.Outcome(ReturnClaimStatus.Completed, []));
        Assert.Equal(ReturnClaimStatus.Disputed, ReturnDecisionLinePolicy.Outcome(ReturnClaimStatus.Disputed, ["Accepted", "Rejected"]));
    }

    [Fact]
    public void The_claim_read_settles_a_partial_approval_by_its_lines()
    {
        // Two of three lines accepted, the first one still waiting: the claim stays actionable, the decision succeeded.
        var target = ReturnDecisionLinePolicy.Outcome(ReturnClaimStatus.ActionRequired, ["Accepted", "Accepted"]);
        Assert.Equal(("SUCCEEDED", (string?)null), MarketplaceJobProcessor.ReconciledReturnDecision("APPROVE", "SUBMITTED", target, "WaitingInAction"));
        // Before it was judged by the claim status and stayed SUBMITTED for good.
        Assert.Equal(("SUBMITTED", (string?)null), MarketplaceJobProcessor.ReconciledReturnDecision("APPROVE", "SUBMITTED", ReturnClaimStatus.ActionRequired, "WaitingInAction"));
        var waiting = ReturnDecisionLinePolicy.Outcome(ReturnClaimStatus.ActionRequired, ["WaitingInAction"]);
        Assert.Equal(("SUBMITTED", (string?)null), MarketplaceJobProcessor.ReconciledReturnDecision("REJECT", "SUBMITTED", waiting, "WaitingInAction"));
    }

    [Theory]
    [InlineData(ReturnClaimStatus.ActionRequired, "Accepted", true)]
    [InlineData(ReturnClaimStatus.ActionRequired, "WaitingInAction", false)]
    [InlineData(ReturnClaimStatus.ActionRequired, null, false)]
    [InlineData(ReturnClaimStatus.Approved, "Rejected", false)]
    [InlineData(ReturnClaimStatus.Approved, "Accepted", true)]
    [InlineData(ReturnClaimStatus.Approved, null, true)]
    [InlineData(ReturnClaimStatus.Completed, "Completed", true)]
    [InlineData(ReturnClaimStatus.Rejected, "Rejected", false)]
    [InlineData(ReturnClaimStatus.Rejected, null, false)]
    public void Stock_is_decided_only_on_an_approved_line(ReturnClaimStatus claimStatus, string? lineStatus, bool allowed)
    {
        Assert.Equal(allowed, ReturnDecisionLinePolicy.StockDispositionAllowed(claimStatus, lineStatus));
    }
}
