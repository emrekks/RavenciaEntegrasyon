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

    // SIPARIS-STOK-1: "Teslim Aldım" only where the decision is sent for the whole claim.
    [Theory]
    [InlineData("TRENDYOL", false)]
    [InlineData("N11", false)]
    [InlineData("PAZARAMA", false)]
    [InlineData("PTTAVM", false)]
    [InlineData("HEPSIBURADA", true)]
    [InlineData("SHOPIFY", true)]
    public void ReturnReceipt_OnlyForWholeClaimMarketplaces(string platformCode, bool allowed) =>
        Assert.Equal(allowed, MarketplaceHub.Infrastructure.Persistence.ReturnQueryService.ReturnReceiptAllowed(platformCode));
}
