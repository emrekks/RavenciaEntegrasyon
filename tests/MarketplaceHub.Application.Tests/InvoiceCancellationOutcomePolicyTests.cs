using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// G-3 (29 Oct 2026): only the provider's refusal of a cancellation is final, and a refused one can be asked for again.
public sealed class InvoiceCancellationOutcomePolicyTests
{
    [Theory]
    [InlineData(AdapterErrorClass.Authentication)]
    [InlineData(AdapterErrorClass.NotFound)]
    [InlineData(AdapterErrorClass.BusinessConflict)]
    [InlineData(AdapterErrorClass.RateLimit)]
    [InlineData(AdapterErrorClass.TransientNetwork)]
    [InlineData(AdapterErrorClass.Remote5xx)]
    public void An_answer_that_is_not_a_refusal_keeps_the_cancellation_pending(AdapterErrorClass errorClass) =>
        Assert.Equal(InvoiceStatus.CancellationPending, InvoiceCancellationOutcomePolicy.StatusAfterFailure(errorClass));

    [Fact]
    public void Only_the_providers_refusal_is_a_refused_cancellation()
    {
        Assert.Equal(InvoiceStatus.CancellationRejected, InvoiceCancellationOutcomePolicy.StatusAfterFailure(AdapterErrorClass.Validation));
        Assert.Equal(InvoiceStatus.ManualReview, InvoiceCancellationOutcomePolicy.StatusAfterFailure(AdapterErrorClass.ContractViolation));
        Assert.True(InvoiceCancellationOutcomePolicy.RetriesLater(AdapterErrorClass.Authentication));
        Assert.False(InvoiceCancellationOutcomePolicy.RetriesLater(AdapterErrorClass.Validation));
    }

    [Fact]
    public void A_refused_or_stalled_cancellation_can_be_requested_again()
    {
        Assert.True(InvoiceStateMachine.CanTransition(InvoiceStatus.CancellationRejected, InvoiceStatus.CancellationPending));
        Assert.True(InvoiceCancellationOutcomePolicy.CanRequest(InvoiceStatus.CancellationRejected, jobRunning: false));
        Assert.True(InvoiceCancellationOutcomePolicy.CanRequest(InvoiceStatus.CancellationPending, jobRunning: false));
        Assert.False(InvoiceCancellationOutcomePolicy.CanRequest(InvoiceStatus.CancellationPending, jobRunning: true));
        Assert.False(InvoiceCancellationOutcomePolicy.CanRequest(InvoiceStatus.Cancelled, jobRunning: false));
    }
}
