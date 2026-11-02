using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class JobVariantResultPolicyTests
{
    [Fact]
    public void Trendyol_batch_line_keeps_every_failure_reason()
    {
        var status = TrendyolJsonMapper.Batch("""{"batchRequestId":"b-1","status":"COMPLETED","items":[{"requestItem":{"barcode":"A"},"status":"SUCCESS","failureReasons":[]},{"requestItem":{"barcode":"B"},"status":"FAILED","failureReasons":["Görsel eksik","Kategori özelliği zorunlu"]}]}""", "b-1");

        Assert.Null(status.Lines[0].Reasons);
        Assert.Equal(["Görsel eksik", "Kategori özelliği zorunlu"], status.Lines[1].Reasons);
        Assert.Equal("Görsel eksik", status.Lines[1].ErrorCode);
        Assert.Equal("Görsel eksik · Kategori özelliği zorunlu", JobVariantResultPolicy.Reason([.. status.Lines[1].Reasons!, status.Lines[1].ErrorCode]));
    }

    [Theory]
    [InlineData(MarketplaceJobTypes.ProductApprovalReconcile, "LIVE", JobVariantResultPolicy.Approved)]
    [InlineData(MarketplaceJobTypes.ProductApprovalReconcile, "APPROVAL_PENDING", JobVariantResultPolicy.Pending)]
    [InlineData(MarketplaceJobTypes.ProductApprovalReconcile, "REJECTED", JobVariantResultPolicy.Rejected)]
    [InlineData(MarketplaceJobTypes.ProductApprovalReconcile, "CREATE_REJECTED", JobVariantResultPolicy.Rejected)]
    [InlineData(MarketplaceJobTypes.ProductCreate, "CREATE_ACCEPTED", JobVariantResultPolicy.Sent)]
    [InlineData(MarketplaceJobTypes.ProductCreate, "CREATE_REJECTED", JobVariantResultPolicy.Rejected)]
    [InlineData(MarketplaceJobTypes.ProductUpdate, "UPDATE_REJECTED", JobVariantResultPolicy.Rejected)]
    [InlineData(MarketplaceJobTypes.ProductCreate, ProductBatchResultPolicy.ResultUnknownStatus, JobVariantResultPolicy.Pending)]
    [InlineData(MarketplaceJobTypes.ProductCreate, "MANUAL_REVIEW", JobVariantResultPolicy.Pending)]
    [InlineData(MarketplaceJobTypes.ProductCreate, "BLOCKED", JobVariantResultPolicy.Rejected)]
    public void A_listing_status_names_the_job_outcome(string jobType, string listingStatus, string expected) =>
        Assert.Equal(expected, JobVariantResultPolicy.StatusFor(jobType, listingStatus));

    [Fact]
    public void An_older_send_read_from_the_card_knows_only_its_own_refusal()
    {
        // The card was refused later at approval; the create job had sent it.
        Assert.Equal(JobVariantResultPolicy.Sent, JobVariantResultPolicy.StatusFromCardState(MarketplaceJobTypes.ProductCreate, "REJECTED"));
        Assert.Equal(JobVariantResultPolicy.Sent, JobVariantResultPolicy.StatusFromCardState(MarketplaceJobTypes.ProductCreate, "LIVE"));
        Assert.Equal(JobVariantResultPolicy.Approved, JobVariantResultPolicy.StatusFromCardState(MarketplaceJobTypes.ProductApprovalReconcile, "LIVE"));
    }

    [Fact]
    public void A_short_create_batch_answer_is_waited_for_then_settles_as_unknown()
    {
        // Live 2026-11-02: COMPLETED with 21 of 28 items, 16 s after the send.
        var status = TrendyolJsonMapper.Batch("""{"batchRequestId":"b-1","status":"COMPLETED","itemCount":28,"failedItemCount":0,"items":[{"requestItem":{"barcode":"A"},"status":"SUCCESS","failureReasons":[]}]}""", "b-1");
        Assert.Equal(28, status.ReportedItemCount);
        Assert.Null(TrendyolJsonMapper.Batch("""{"batchRequestId":"b-1","status":"COMPLETED","items":[]}""", "b-1").ReportedItemCount);

        Assert.True(ProductBatchResultPolicy.KeepWaiting(7, TimeSpan.FromSeconds(16)));
        Assert.False(ProductBatchResultPolicy.KeepWaiting(7, ProductBatchResultPolicy.IncompleteGrace));
        Assert.False(ProductBatchResultPolicy.KeepWaiting(0, TimeSpan.FromSeconds(16)));
        Assert.True(JobWaitingPolicy.IsWaitingCode("PRODUCT_BATCH_PENDING"));
        Assert.Equal("Trendyol sonucu henüz gönderilen 28 varyantın 21 tanesini listeliyor; kalanların sonucu bekleniyor.", ProductBatchResultPolicy.WaitingMessage("Trendyol", 21, 28));
        // An older job read from the card: a manual review or an unlisted variant is not "sent", it is not known yet.
        Assert.Equal(JobVariantResultPolicy.Pending, JobVariantResultPolicy.StatusFromCardState(MarketplaceJobTypes.ProductCreate, "MANUAL_REVIEW"));
        Assert.Equal(JobVariantResultPolicy.Pending, JobVariantResultPolicy.StatusFromCardState(MarketplaceJobTypes.ProductCreate, ProductBatchResultPolicy.ResultUnknownStatus));
    }

    [Fact]
    public void A_trendyol_refusal_reason_reaches_the_job_summary()
    {
        var status = TrendyolJsonMapper.Batch("""{"batchRequestId":"b-1","status":"COMPLETED","items":[{"requestItem":{"barcode":"MZ-22"},"status":"FAILED","failureReasons":["Web Color alanı zorunludur."]},{"requestItem":{"barcode":"MZ-01"},"status":"SUCCESS","failureReasons":[]}]}""", "b-1");

        Assert.Equal(" Reddedilen: MZ-22 (Web Color alanı zorunludur.)", MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.RejectionReasons(status.Lines));
    }

    [Fact]
    public void Options_read_in_their_order_and_no_reason_is_invented()
    {
        Assert.Equal("Beden: L · Renk: Bordo", JobVariantResultPolicy.OptionsText([(2, "Renk", "Bordo"), (1, "Beden", "L")]));
        Assert.Null(JobVariantResultPolicy.OptionsText([]));
        Assert.Null(JobVariantResultPolicy.Reason([null, " "]));
    }
}
