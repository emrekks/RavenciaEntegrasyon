using MarketplaceHub.Domain;

namespace MarketplaceHub.Domain.Tests;

public sealed class F4DomainTests
{
    [Fact]
    public void Invoice_state_machine_preserves_unknown_and_manual_review_boundaries()
    {
        Assert.True(InvoiceStateMachine.CanTransition(InvoiceStatus.Ready, InvoiceStatus.Submitting));
        Assert.True(InvoiceStateMachine.CanTransition(InvoiceStatus.Submitting, InvoiceStatus.UnknownResult));
        Assert.True(InvoiceStateMachine.CanTransition(InvoiceStatus.UnknownResult, InvoiceStatus.ManualReview));
        Assert.False(InvoiceStateMachine.CanTransition(InvoiceStatus.UnknownResult, InvoiceStatus.Completed));
        Assert.False(InvoiceStateMachine.CanTransition(InvoiceStatus.Completed, InvoiceStatus.Draft));
    }

    [Fact]
    public void Marketplace_delivery_requires_pending_state_before_completion()
    {
        Assert.True(InvoiceStateMachine.CanTransition(InvoiceStatus.Accepted, InvoiceStatus.MarketplacePending));
        Assert.True(InvoiceStateMachine.CanTransition(InvoiceStatus.MarketplacePending, InvoiceStatus.Completed));
        Assert.False(InvoiceStateMachine.CanTransition(InvoiceStatus.Accepted, InvoiceStatus.Completed));
    }
}
