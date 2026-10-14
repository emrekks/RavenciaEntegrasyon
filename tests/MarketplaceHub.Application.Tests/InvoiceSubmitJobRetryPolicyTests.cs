using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceSubmitJobRetryPolicyTests
{
    [Theory]
    [InlineData("EFATURAM_FISCAL_PAYLOAD_INVALID")]
    [InlineData("EFATURAM_REQUEST_REJECTED")]
    [InlineData("EFATURAM_APPLICATION_NOT_ACTIVE")]
    public void SafePreProviderFailureIsResetBeforeRetry(string errorCode)
    {
        var now = DateTimeOffset.Parse("2026-10-09T18:00:00Z");
        var invoice = NewInvoice(InvoiceStatus.Rejected, errorCode);

        var retryable = JobOperationsService.PrepareInvoiceSubmitRetry(invoice, now);

        Assert.True(retryable);
        Assert.Equal(InvoiceStatus.Submitting, invoice.Status);
        Assert.Null(invoice.LastErrorCode);
        Assert.Equal(now, invoice.IssuedAt);
        Assert.Equal(now, invoice.UpdatedAt);
        Assert.Equal(2, invoice.Version);
    }

    [Theory]
    [InlineData(InvoiceStatus.Accepted, "EFATURAM_REQUEST_REJECTED", null)]
    [InlineData(InvoiceStatus.Rejected, "EFATURAM_FISCAL_PAYLOAD_INVALID", "remote-invoice-id")]
    [InlineData(InvoiceStatus.Submitting, "INVOICE_CREATION_DISABLED", null)]
    [InlineData(InvoiceStatus.Completed, null, null)]
    public void UnsafeOrUnrelatedInvoiceStateCannotBeRetried(InvoiceStatus status, string? errorCode, string? externalReference)
    {
        var invoice = NewInvoice(status, errorCode);
        invoice.ExternalReference = externalReference;

        var retryable = JobOperationsService.PrepareInvoiceSubmitRetry(invoice, DateTimeOffset.UtcNow);

        Assert.False(retryable);
        Assert.Equal(status, invoice.Status);
        Assert.Equal(errorCode, invoice.LastErrorCode);
        Assert.Equal(1, invoice.Version);
    }

    [Fact]
    public void AlreadyQueuedSubmissionCanBeRetriedWithoutRewritingInvoiceState()
    {
        var invoice = NewInvoice(InvoiceStatus.Submitting, errorCode: null);

        var retryable = JobOperationsService.PrepareInvoiceSubmitRetry(invoice, DateTimeOffset.UtcNow);

        Assert.True(retryable);
        Assert.Equal(InvoiceStatus.Submitting, invoice.Status);
        Assert.Equal(1, invoice.Version);
    }

    [Fact]
    public void InvoiceSubmitJobsUseHighestQueuePriority()
    {
        Assert.Equal(-1, InvoicingBillingService.InvoiceJobPriority("INVOICE_SUBMIT"));
        Assert.Equal(0, InvoicingBillingService.InvoiceJobPriority("INVOICE_RECONCILE"));
    }

    private static Invoice NewInvoice(InvoiceStatus status, string? errorCode) => new()
    {
        Status = status,
        LastErrorCode = errorCode,
        InvoiceType = "EARSIVFATURA",
        SequencePurpose = "MANUAL",
        Currency = "TRY",
        Note = string.Empty,
        IdempotencyKey = "invoice-submit-retry-test"
    };
}
