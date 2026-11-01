using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The two invoices stuck on 1 Nov 2026: K4 (issued, document fetch failed, never delivered, shown as removed) and K5
// (delivered and confirmed, sent back to review by a read without references).
public sealed class InvoiceStuckDeliveryFixTests
{
    private const string K5Number = "DAP2026000000448";
    private const string K5Link = "https://apigateway.trendyolecozum.com/api/invoice/documents/download/5072747";
    private static readonly DateTimeOffset At = new(2026, 11, 1, 15, 15, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/octet-stream")]
    [InlineData("binary/octet-stream")]
    [InlineData("application/x-pdf")]
    [InlineData(null)]
    public void A_pdf_body_is_accepted_whatever_its_declared_type(string? mediaType) =>
        Assert.Null(SafeRemoteDocumentDownloader.ContentRejection(mediaType, Encoding.ASCII.GetBytes("%PDF-1.7\n...")));

    [Theory]
    [InlineData("text/html", "<html>hazırlanıyor</html>", SafeRemoteDocumentDownloader.ContentTypeInvalidCode)]
    [InlineData("application/json", "{\"status\":\"PENDING\"}", SafeRemoteDocumentDownloader.ContentTypeInvalidCode)]
    [InlineData("application/pdf", "not a pdf", SafeRemoteDocumentDownloader.PdfSignatureInvalidCode)]
    [InlineData("application/pdf", "", SafeRemoteDocumentDownloader.PdfSignatureInvalidCode)]
    public void A_body_that_is_not_a_pdf_is_still_rejected(string mediaType, string body, string expected) =>
        Assert.Equal(expected, SafeRemoteDocumentDownloader.ContentRejection(mediaType, Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void A_document_that_is_not_ready_is_retried_not_parked_for_review()
    {
        Assert.True(SafeRemoteDocumentDownloader.IsDocumentNotReady(SafeRemoteDocumentDownloader.ContentTypeInvalidCode));
        Assert.True(SafeRemoteDocumentDownloader.IsDocumentNotReady(SafeRemoteDocumentDownloader.PdfSignatureInvalidCode));
        Assert.False(SafeRemoteDocumentDownloader.IsDocumentNotReady("DOCUMENT_HOST_REJECTED"));
        Assert.False(SafeRemoteDocumentDownloader.IsDocumentNotReady("DOCUMENT_TOO_LARGE"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("marketplace-observation:01a18813b3147e7f9737e36c7233dfe4:01a178cfbd1b776f8342088666e75c5e", false)]
    [InlineData("delivery:01a173ae415f74fa96d8facd238c23fe:01a15163cb3975c499c802e59756757f", true)]
    [InlineData("remote-invoice-proof:a:b", true)]
    [InlineData("operator-confirmed:a:b", true)]
    public void Only_a_state_row_from_a_delivery_a_proof_or_an_operator_is_a_delivery_attempt(string? stateKey, bool expected) =>
        Assert.Equal(expected, InvoiceDeliveryRecoveryPolicy.HasDeliveryAttempt(stateKey));

    [Theory]
    [InlineData(InvoiceStatus.Accepted, null)]
    [InlineData(InvoiceStatus.Accepted, SafeRemoteDocumentDownloader.ContentTypeInvalidCode)]
    [InlineData(InvoiceStatus.MarketplacePending, null)]
    [InlineData(InvoiceStatus.MarketplaceFailed, InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode)]
    public void An_invoice_nothing_was_sent_for_is_never_redelivered_as_removed(InvoiceStatus status, string? lastErrorCode)
    {
        Assert.False(InvoiceDeliveryRecoveryPolicy.ShouldQueueAutomaticRedelivery(status, lastErrorCode, MarketplaceInvoiceStatus.NotInvoiced, hasDeliveryAttempt: false));
        // The same invoice after a real delivery keeps its recovery.
        Assert.True(InvoiceDeliveryRecoveryPolicy.ShouldQueueAutomaticRedelivery(status, lastErrorCode, MarketplaceInvoiceStatus.NotInvoiced, hasDeliveryAttempt: true));
    }

    [Fact]
    public void A_completed_invoice_that_vanished_is_still_recovered()
    {
        Assert.True(InvoiceDeliveryRecoveryPolicy.ShouldQueueAutomaticRedelivery(InvoiceStatus.Completed, null, MarketplaceInvoiceStatus.NotInvoiced, hasDeliveryAttempt: false));
        Assert.True(InvoiceDeliveryRecoveryPolicy.ShouldMarkMarketplaceInvoiceNotPresent(InvoiceStatus.Completed, null, MarketplaceInvoiceStatus.NotInvoiced));
    }

    [Fact]
    public void The_K4_record_wrongly_marked_removed_is_repaired_to_not_delivered()
    {
        Assert.True(InvoiceDeliveryRecoveryPolicy.IsNeverDeliveredMarkedNotPresent(
            InvoiceStatus.MarketplaceFailed, InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode, hasDeliveryAttempt: false, hasDocument: false));
        // A real earlier delivery, or a document that is there, keeps the existing reading.
        Assert.False(InvoiceDeliveryRecoveryPolicy.IsNeverDeliveredMarkedNotPresent(
            InvoiceStatus.MarketplaceFailed, InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode, hasDeliveryAttempt: true, hasDocument: false));
        Assert.False(InvoiceDeliveryRecoveryPolicy.IsNeverDeliveredMarkedNotPresent(
            InvoiceStatus.MarketplaceFailed, InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode, hasDeliveryAttempt: false, hasDocument: true));
        Assert.False(InvoiceDeliveryRecoveryPolicy.IsNeverDeliveredMarkedNotPresent(
            InvoiceStatus.MarketplaceFailed, InvoiceDeliveryRecoveryPolicy.DocumentMissingNotDeliveredErrorCode, hasDeliveryAttempt: false, hasDocument: false));
    }

    [Theory]
    [InlineData("REVOKED", "marketplace-observation:a:b", "NOT_SENT")]
    [InlineData("REVOKED", "delivery:a:b", "REVOKED")]
    [InlineData("REVIEW_REQUIRED", "marketplace-observation:a:b", "REVIEW_REQUIRED")]
    [InlineData(null, null, null)]
    public void A_read_only_removal_of_an_invoice_never_sent_reads_as_not_sent(string? status, string? key, string? expected) =>
        Assert.Equal(expected, InvoiceDeliveryRecoveryPolicy.DisplayDeliveryStatus(status, key));

    [Fact]
    public void The_K5_record_heals_on_the_next_read_without_references()
    {
        // Hepsiburada's missing-invoice list says only "invoiced"; the package already holds our exact number and link.
        Assert.True(InvoiceDeliveryRecoveryPolicy.StoredReferencesProveDelivery(K5Number, K5Link, K5Number, K5Link));
        Assert.True(InvoiceDeliveryRecoveryPolicy.StoredReferencesProveDelivery($" {K5Number} ", K5Link, K5Number, $"{K5Link} "));

        // The heal goes through the exact-proof path: every package confirmed reads Completed with no error.
        var invoice = StuckInvoice();
        Assert.True(MarketplaceJobProcessor.SetInvoiceDeliveryState(invoice, InvoiceStatus.Completed, null, At));
        Assert.Equal(InvoiceStatus.Completed, invoice.Status);
        Assert.Null(invoice.LastErrorCode);
        Assert.Equal(202, invoice.Version);
    }

    [Theory]
    [InlineData(null, K5Link)]
    [InlineData(K5Number, null)]
    [InlineData("DAP2026000000999", K5Link)]
    [InlineData(K5Number, "https://apigateway.trendyolecozum.com/api/invoice/documents/download/other")]
    public void Stored_references_prove_nothing_unless_number_and_link_both_match(string? packageNumber, string? packageLink) =>
        Assert.False(InvoiceDeliveryRecoveryPolicy.StoredReferencesProveDelivery(packageNumber, packageLink, K5Number, K5Link));

    [Fact]
    public void A_weaker_read_does_not_undo_a_confirmed_delivery()
    {
        Assert.True(InvoiceDeliveryRecoveryPolicy.ShouldPreservePreviouslyConfirmedDelivery(InvoiceStatus.MarketplacePending, "CONFIRMED", null));
        Assert.True(InvoiceDeliveryRecoveryPolicy.ShouldPreservePreviouslyConfirmedDelivery(InvoiceStatus.Completed, "CONFIRMED", " "));
        // A read that names a different invoice still goes to review.
        Assert.False(InvoiceDeliveryRecoveryPolicy.ShouldPreservePreviouslyConfirmedDelivery(InvoiceStatus.Completed, "CONFIRMED", "DAP2026000000999"));
    }

    [Fact]
    public void An_unchanged_invoice_is_not_written_again()
    {
        var invoice = StuckInvoice();

        Assert.False(MarketplaceJobProcessor.SetInvoiceDeliveryState(invoice, InvoiceStatus.MarketplacePending,
            InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode, At));
        Assert.Equal(201, invoice.Version);
        Assert.Equal(At.AddDays(-1), invoice.UpdatedAt);

        Assert.True(MarketplaceJobProcessor.SetInvoiceDeliveryState(invoice, InvoiceStatus.MarketplacePending, null, At));
        Assert.Equal(202, invoice.Version);
        Assert.Equal(At, invoice.UpdatedAt);
    }

    // P4: the orders filter and the row label read an issued invoice with the same rules.
    [Theory]
    [InlineData(InvoiceStatus.ManualReview, "PLATFORM_INVOICE_ALREADY_PRESENT", "remote-1", MarketplaceSalesService.InvoiceNeedsReviewLabel)]
    [InlineData(InvoiceStatus.ManualReview, "EFATURAM_FISCAL_PAYLOAD_INVALID", null, null)]
    [InlineData(InvoiceStatus.MarketplacePending, InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode, "remote-1", "FATURA_PLATFORMA_AKTARILMADI")]
    [InlineData(InvoiceStatus.Accepted, SafeRemoteDocumentDownloader.ContentTypeInvalidCode, "remote-1", "FATURA_PLATFORMA_AKTARILMADI")]
    [InlineData(InvoiceStatus.MarketplaceFailed, InvoiceDeliveryRecoveryPolicy.DocumentMissingNotDeliveredErrorCode, "remote-1", "FATURA_PLATFORMA_AKTARILMADI")]
    [InlineData(InvoiceStatus.MarketplacePending, null, "remote-1", null)]
    [InlineData(InvoiceStatus.Completed, null, "remote-1", null)]
    public void The_filter_and_the_label_share_one_reading_of_an_issued_invoice(InvoiceStatus status, string? lastErrorCode, string? externalReference, string? expected)
    {
        var invoice = StuckInvoice();
        invoice.Status = status;
        invoice.LastErrorCode = lastErrorCode;
        invoice.ExternalReference = externalReference;

        Assert.Equal(expected, MarketplaceSalesService.IssuedInvoiceLabel(invoice));
        Assert.Equal(expected == MarketplaceSalesService.InvoiceNeedsReviewLabel, MarketplaceSalesService.IssuedInvoiceNeedsReview.Compile()(invoice));
        Assert.Equal(expected == "FATURA_PLATFORMA_AKTARILMADI", MarketplaceSalesService.IssuedInvoiceNotOnPlatform.Compile()(invoice));
        // The row label shows the same reading (an issued invoice waiting for review is never "refused").
        if (expected is not null)
            Assert.Equal(expected, MarketplaceSalesService.PackageInvoiceLabel(null, invoice, "{}", [], "HEPSIBURADA"));
    }

    private static Invoice StuckInvoice() => new()
    {
        InvoiceType = "EARSIVFATURA",
        SequencePurpose = "SALE",
        Currency = "TRY",
        Note = string.Empty,
        IdempotencyKey = "stuck-invoice",
        InvoiceNumber = K5Number,
        Status = InvoiceStatus.MarketplacePending,
        LastErrorCode = InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode,
        Version = 201,
        UpdatedAt = At.AddDays(-1)
    };
}
