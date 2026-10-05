using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReturnLifecyclePolicyTests
{
    [Theory]
    [InlineData(ReturnClaimStatus.InTransit, ReturnClaimStatus.ActionRequired)]
    [InlineData(ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Approved)]
    [InlineData(ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Rejected)]
    [InlineData(ReturnClaimStatus.ActionRequired, ReturnClaimStatus.AwaitingShipment)]
    [InlineData(ReturnClaimStatus.AwaitingShipment, ReturnClaimStatus.ActionRequired)]
    [InlineData(ReturnClaimStatus.Disputed, ReturnClaimStatus.Approved)]
    [InlineData(ReturnClaimStatus.Disputed, ReturnClaimStatus.Rejected)]
    [InlineData(ReturnClaimStatus.Approved, ReturnClaimStatus.Completed)]
    [InlineData(ReturnClaimStatus.Rejected, ReturnClaimStatus.Completed)]
    public void StateMachine_AllowsOperationalReturnTransitions(ReturnClaimStatus current, ReturnClaimStatus next)
    {
        Assert.True(ReturnClaimStateMachine.CanTransition(current, next));
    }

    [Theory]
    [InlineData(ReturnClaimStatus.InTransit, ReturnClaimStatus.Rejected)]
    [InlineData(ReturnClaimStatus.AwaitingShipment, ReturnClaimStatus.Approved)]
    [InlineData(ReturnClaimStatus.Completed, ReturnClaimStatus.ActionRequired)]
    [InlineData(ReturnClaimStatus.Cancelled, ReturnClaimStatus.ActionRequired)]
    public void StateMachine_RejectsInvalidOrTerminalReopening(ReturnClaimStatus current, ReturnClaimStatus next)
    {
        Assert.False(ReturnClaimStateMachine.CanTransition(current, next));
    }

    [Theory]
    [InlineData(ReturnClaimStatus.Requested, true)]
    [InlineData(ReturnClaimStatus.InTransit, true)]
    [InlineData(ReturnClaimStatus.ActionRequired, true)]
    [InlineData(ReturnClaimStatus.Disputed, true)]
    [InlineData(ReturnClaimStatus.Completed, false)]
    [InlineData(ReturnClaimStatus.Cancelled, false)]
    public void OpenLifecyclePolicy_PollsOnlyNonTerminalReturns(ReturnClaimStatus status, bool expected)
    {
        Assert.Equal(expected, OpenReturnLifecyclePolicy.ShouldPoll(status));
    }

    [Theory]
    [InlineData(ReturnClaimStatus.Requested, true)]
    [InlineData(ReturnClaimStatus.Approved, true)]
    [InlineData(ReturnClaimStatus.Rejected, true)]
    [InlineData(ReturnClaimStatus.Cancelled, false)]
    public void ReturnClaimStoragePolicy_DoesNotPersistCancelledClaims(ReturnClaimStatus status, bool expected)
    {
        Assert.Equal(expected, ReturnClaimStoragePolicy.ShouldPersist(status));
    }

    [Fact]
    public void HepsiburadaAwaitingPreApproval_ExposesOnlyDocumentedActionsAndHidesThemWhilePending()
    {
        Assert.True(HepsiburadaReturnActionPolicy.IsAwaitingPreApproval("AwaitingPreApproval"));
        Assert.True(HepsiburadaReturnActionPolicy.IsAwaitingPreApproval("AWAITING_PRE_APPROVAL"));
        Assert.Equal(new[] { "PREAPPROVAL_CONFIRM", "APPROVE", "REJECT" }, HepsiburadaReturnActionPolicy.AllowedActions("AwaitingPreApproval", false));
        Assert.Equal(new[] { "APPROVE", "REJECT" }, HepsiburadaReturnActionPolicy.AllowedActions("AwaitingAction", false));
        Assert.Empty(HepsiburadaReturnActionPolicy.AllowedActions("AwaitingPreApproval", true));
    }
}
