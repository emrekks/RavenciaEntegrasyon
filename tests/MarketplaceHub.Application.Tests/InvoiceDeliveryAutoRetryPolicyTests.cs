using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Y4 (2 Nov 2026): an issued invoice whose document could not be fetched, or whose delivery job ended without delivering
// it, is retried by the scheduler with backoff, then stops; the manual "Tekrar Dene" starts the count again.
public sealed class InvoiceDeliveryAutoRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 10, 0, TimeSpan.Zero);

    [Fact]
    public void FiveAttemptsOverAboutADayThenNone()
    {
        Assert.Equal(5, InvoiceDeliveryAutoRetryPolicy.MaxAttempts);
        Assert.Equal(new double?[] { 1, 3, 6, 12, null },
            Enumerable.Range(1, 5).Select(attempt => InvoiceDeliveryAutoRetryPolicy.DelayAfter(attempt)?.TotalHours));
        // Attempts at 0, 1, 4, 10 and 22 hours.
        Assert.Equal(22, Enumerable.Range(1, 4).Sum(attempt => InvoiceDeliveryAutoRetryPolicy.DelayAfter(attempt)!.Value.TotalHours));
    }

    [Fact]
    public void AMissingDocumentIsFetchedAgainEvenAfterAReadMarkedTheInvoiceNotPresent()
    {
        // DAP2026000000452: MarketplaceFailed, no document, a read-only REVOKED state.
        Assert.Equal(InvoiceDeliveryRetryNeed.FetchDocument,
            InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.MarketplaceFailed, true, false, ["REVOKED"], false, InvoiceDeliveryRecoveryPolicy.DocumentMissingNotDeliveredErrorCode));
        Assert.Equal(InvoiceDeliveryRetryNeed.FetchDocument,
            InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.Accepted, true, false, [], false, "DOCUMENT_CONTENT_TYPE_INVALID"));
    }

    [Theory]
    [InlineData("STARTED")]
    [InlineData("SUBMITTED")]
    [InlineData("CONFIRMED")]
    [InlineData("CONFIRMATION_RETRYABLE")]
    [InlineData("UNKNOWN")]
    [InlineData("FAILED")]
    [InlineData(MarketplaceInvoiceLinkRevocationPolicy.Required)]
    [InlineData(MarketplaceInvoiceLinkRevocationPolicy.Removed)]
    public void ADeliveryThatMayHaveReachedTheMarketplaceIsNeverRetriedFromHere(string state)
    {
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.Accepted, true, false, [state], true, null));
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.Accepted, true, true, [state], true, null));
    }

    [Theory]
    [InlineData(InvoiceStatus.Submitted)]
    [InlineData(InvoiceStatus.MarketplacePending)]
    [InlineData(InvoiceStatus.Completed)]
    [InlineData(InvoiceStatus.ManualReview)]
    [InlineData(InvoiceStatus.CancellationPending)]
    [InlineData(InvoiceStatus.Cancelled)]
    [InlineData(InvoiceStatus.Rejected)]
    public void OnlyAnIssuedInvoiceWaitingForItsDeliveryIsRetried(InvoiceStatus status) =>
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(status, true, false, [], true, null));

    [Fact]
    public void WithoutAProviderReferenceNothingIsRetried() =>
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.Accepted, false, false, [], true, null));

    [Fact]
    public void AStoredDocumentIsDeliveredAgainOnlyAfterADeliveryJobEndedWithoutDeliveringIt()
    {
        Assert.Equal(InvoiceDeliveryRetryNeed.Deliver, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.Accepted, true, true, [], true, null));
        Assert.Equal(InvoiceDeliveryRetryNeed.Deliver, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.Accepted, true, true, ["RETRYABLE_FAILURE"], true, null));
        // Never queued: the never-queued recovery sends it.
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.Accepted, true, true, [], false, null));
        // Skipped on purpose: nothing to retry.
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.Accepted, true, true, [], true, InvoiceMarketplaceAutoDeliveryPolicy.PackageMissingCode));
        // A failed one with a retryable state is the delivery recovery's.
        Assert.Equal(InvoiceDeliveryRetryNeed.None, InvoiceDeliveryAutoRetryPolicy.Need(InvoiceStatus.MarketplaceFailed, true, true, ["RETRYABLE_FAILURE"], true, null));
    }

    [Fact]
    public void AnInvoiceWithAttemptsLeftShowsTheNextScanTime()
    {
        var issued = Now.AddDays(-4);
        var first = InvoiceDeliveryAutoRetryPolicy.Describe(0, null, issued, Now.AddDays(-1), false, InvoiceDeliveryRetryNeed.FetchDocument, "INVOICE_DOCUMENT_MISSING_NOT_DELIVERED", Now, 1800);
        Assert.Equal(InvoiceDeliveryAutoRetryPolicy.Scheduled, first.State);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 30, 0, TimeSpan.Zero), first.NextAttemptAt);
        Assert.Equal("FETCH_DOCUMENT", first.Need);

        var later = InvoiceDeliveryAutoRetryPolicy.Describe(2, Now.AddHours(3).AddMinutes(5), issued, Now, false, InvoiceDeliveryRetryNeed.Deliver, null, Now, 1800);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 15, 30, 0, TimeSpan.Zero), later.NextAttemptAt);
    }

    [Fact]
    public void UsedUpAttemptsOrAnOldInvoiceAskForTheManualRetry()
    {
        var spent = InvoiceDeliveryAutoRetryPolicy.Describe(5, null, Now.AddDays(-2), Now, false, InvoiceDeliveryRetryNeed.FetchDocument, "DOCUMENT_CONTENT_TYPE_INVALID", Now, 1800);
        Assert.Equal(InvoiceDeliveryAutoRetryPolicy.Exhausted, spent.State);
        Assert.Null(spent.NextAttemptAt);
        Assert.Equal("DOCUMENT_CONTENT_TYPE_INVALID", spent.LastErrorCode);
        Assert.Equal(InvoiceDeliveryAutoRetryPolicy.Exhausted,
            InvoiceDeliveryAutoRetryPolicy.Describe(0, null, Now.AddDays(-15), Now, false, InvoiceDeliveryRetryNeed.FetchDocument, null, Now, 1800).State);
        Assert.Equal(InvoiceDeliveryAutoRetryPolicy.InProgress,
            InvoiceDeliveryAutoRetryPolicy.Describe(5, null, Now.AddDays(-2), Now, true, InvoiceDeliveryRetryNeed.FetchDocument, null, Now, 1800).State);
    }

    [Fact]
    public void AutomaticAttemptsCountUpAndAManualOneStartsAgain()
    {
        var invoice = new Invoice { InvoiceType = "EARSIVFATURA", SequencePurpose = "SALE", Currency = "TRY", Note = "", IdempotencyKey = "k" };
        for (var attempt = 1; attempt <= 5; attempt++) InvoiceDeliveryRetryReader.RecordAutomaticAttempt(invoice, Now);
        Assert.Equal(5, invoice.DeliveryRetryAttempts);
        Assert.Null(invoice.DeliveryRetryNextAt);

        InvoiceDeliveryRetryReader.RecordManualAttempt(invoice, Now);
        Assert.Equal(0, invoice.DeliveryRetryAttempts);
        Assert.Equal(Now.AddHours(1), invoice.DeliveryRetryNextAt);
    }

    [Fact]
    public void TheRetryKeyTravelsInTheFetchPayload()
    {
        var invoice = new Invoice { Id = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), ProviderConnectionId = Guid.CreateVersion7(), InvoiceType = "EARSIVFATURA", SequencePurpose = "SALE", Currency = "TRY", Note = "", IdempotencyKey = "k" };
        var job = InvoiceDeliveryRetryReader.NewDocumentFetchJob(invoice, "delivery-retry:1:100", 3, "corr", Now);
        Assert.Equal($"{InvoicingJobTypes.InvoiceDocumentFetch}:{invoice.Id}:delivery-retry:1:100", job.JobDedupKey);
        Assert.Equal(invoice.ProviderConnectionId, job.ConnectionId);
        Assert.Equal("delivery-retry:1:100", InvoiceDeliveryRetryReader.ReadRetryKey(job.PayloadJson));
        Assert.Equal(string.Empty, InvoiceDeliveryRetryReader.ReadRetryKey($$"""{"invoiceId":"{{invoice.Id}}"}"""));
    }

    // O8: every cancellation request gets its own reconcile; a retry of the same request keeps its key.
    [Fact]
    public void EachCancellationRequestHasItsOwnReconcileKey()
    {
        Assert.Equal(InvoicingJobProcessor.CancellationReconcileSuffix("trace-1"), InvoicingJobProcessor.CancellationReconcileSuffix("trace-1"));
        Assert.NotEqual(InvoicingJobProcessor.CancellationReconcileSuffix("trace-1"), InvoicingJobProcessor.CancellationReconcileSuffix("trace-2"));
        Assert.StartsWith("after-cancellation:", InvoicingJobProcessor.CancellationReconcileSuffix("trace-1"), StringComparison.Ordinal);
    }
}
