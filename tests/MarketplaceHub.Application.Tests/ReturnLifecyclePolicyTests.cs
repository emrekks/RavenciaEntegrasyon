using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReturnLifecyclePolicyTests
{
    [Theory]
    [InlineData(ReturnClaimStatus.InTransit, ReturnClaimStatus.ActionRequired)]
    [InlineData(ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Approved)]
    [InlineData(ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Rejected)]
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
}
