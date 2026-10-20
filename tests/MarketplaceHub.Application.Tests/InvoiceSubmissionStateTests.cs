using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceSubmissionStateTests
{
    [Theory]
    [InlineData("EFATURAM_OPERATION_FORBIDDEN")]
    [InlineData("CAPABILITY_NOT_VERIFIED")]
    [InlineData("EFATURAM_SIGNIN_UNAVAILABLE")]
    [InlineData("EFATURAM_RATE_LIMITED")]
    public void Provider_refusal_before_any_invoice_exists_is_retryable_and_shown_as_an_error(string errorCode)
    {
        Assert.True(InvoicingBillingService.IsStoppedBeforeProvider(InvoiceStatus.Submitting, errorCode, null));
        Assert.True(InvoicingBillingService.CanRetryPreProviderFailure(InvoiceStatus.Submitting, errorCode, null));
        Assert.Equal("FATURA_REDDEDILDI", InvoicingBillingService.FiscalSubmissionLabel(InvoiceStatus.Submitting, errorCode, null));
        Assert.Equal("SUBMIT_RETRY", InvoicingBillingService.ExistingInvoiceNextAction(InvoiceStatus.Submitting, errorCode, null));
    }

    [Fact]
    public void Submission_in_flight_or_with_a_remote_reference_is_not_retried()
    {
        Assert.False(InvoicingBillingService.CanRetryPreProviderFailure(InvoiceStatus.Submitting, null, null));
        Assert.False(InvoicingBillingService.CanRetryPreProviderFailure(InvoiceStatus.Submitting, "EFATURAM_OPERATION_FORBIDDEN", "remote-1"));
        Assert.Equal("FATURA_ISLENIYOR", InvoicingBillingService.FiscalSubmissionLabel(InvoiceStatus.Submitting, null, null));
        Assert.Equal("WAIT", InvoicingBillingService.ExistingInvoiceNextAction(InvoiceStatus.Submitting, null, null));
        Assert.Equal("WAIT", InvoicingBillingService.ExistingInvoiceNextAction(InvoiceStatus.UnknownResult, "INVOICE_SUBMISSION_OUTCOME_UNKNOWN", null));
    }

    [Fact]
    public void Manual_job_retry_resets_a_pre_provider_refusal()
    {
        var now = DateTimeOffset.Parse("2026-10-20T09:00:00Z");
        var invoice = new Invoice
        {
            Status = InvoiceStatus.Submitting,
            LastErrorCode = "EFATURAM_OPERATION_FORBIDDEN",
            InvoiceType = "EARSIVFATURA",
            SequencePurpose = "SALE",
            Currency = "TRY",
            Note = string.Empty,
            IdempotencyKey = "pre-provider-refusal"
        };

        Assert.True(JobOperationsService.PrepareInvoiceSubmitRetry(invoice, now));
        Assert.Equal(InvoiceStatus.Submitting, invoice.Status);
        Assert.Null(invoice.LastErrorCode);
    }

    [Fact]
    public void Lost_provider_answer_is_labelled_uncertain_not_rejected()
    {
        Assert.Equal("FATURA_BELIRSIZ", InvoicingBillingService.FiscalSubmissionLabel(InvoiceStatus.UnknownResult, "INVOICE_SUBMISSION_OUTCOME_UNKNOWN", null));
        Assert.Equal("FATURA_BELIRSIZ", MarketplaceSalesService.InvoiceLabel(InvoiceStatus.UnknownResult, null, "{}", []));
        Assert.Equal("FATURA_BELIRSIZ", MarketplaceSalesService.InvoiceLabel(InvoiceStatus.UnknownResult, null, MarketplaceInvoiceStatus.NotInvoiced, "{}", []));
        Assert.Equal("FATURA_REDDEDILDI", MarketplaceSalesService.InvoiceLabel(InvoiceStatus.Rejected, null, "{}", []));
        // It stays in the work queue so the operator resolves it.
        Assert.True(InvoicingBillingService.IsWorkspaceInvoiceUninvoiced("FATURA_BELIRSIZ", canCreateInvoice: false));
    }

    [Fact]
    public void Provider_status_check_needs_a_remote_reference()
    {
        Assert.False(InvoicingBillingService.CanReconcileWithProvider(InvoiceStatus.UnknownResult, null));
        Assert.False(InvoicingBillingService.CanReconcileWithProvider(InvoiceStatus.UnknownResult, " "));
        Assert.True(InvoicingBillingService.CanReconcileWithProvider(InvoiceStatus.UnknownResult, "remote-1"));
        Assert.True(InvoicingBillingService.CanReconcileWithProvider(InvoiceStatus.CancellationPending, "remote-1"));
        Assert.False(InvoicingBillingService.CanReconcileWithProvider(InvoiceStatus.Completed, "remote-1"));
    }
}
