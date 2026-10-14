using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReturnClaimDecisionPolicyTests
{
    [Fact]
    public void PartialApprovalKeepsClaimInActionRequired()
    {
        var result = ReturnClaimDecisionPolicy.StatusAfterLineDecision(ReturnClaimStatus.ActionRequired, "APPROVE", 1, 2);

        Assert.Equal(ReturnClaimStatus.ActionRequired, result);
    }

    [Fact]
    public void FullApprovalMovesClaimToApproved()
    {
        var result = ReturnClaimDecisionPolicy.StatusAfterLineDecision(ReturnClaimStatus.ActionRequired, "APPROVE", 2, 2);

        Assert.Equal(ReturnClaimStatus.Approved, result);
    }

    [Fact]
    public void PartialRejectionKeepsClaimOpenForRemainingLines()
    {
        var result = ReturnClaimDecisionPolicy.StatusAfterLineDecision(ReturnClaimStatus.ActionRequired, "REJECT", 1, 2);

        Assert.Equal(ReturnClaimStatus.ActionRequired, result);
    }

    [Fact]
    public void FullHepsiburadaPreapprovalConfirmationMovesToAwaitingShipment()
    {
        var result = ReturnClaimDecisionPolicy.StatusAfterLineDecision(ReturnClaimStatus.ActionRequired, "PREAPPROVAL_CONFIRM", 2, 2);

        Assert.Equal(ReturnClaimStatus.AwaitingShipment, result);
    }
}
