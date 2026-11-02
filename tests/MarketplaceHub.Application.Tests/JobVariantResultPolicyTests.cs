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
    public void Options_read_in_their_order_and_no_reason_is_invented()
    {
        Assert.Equal("Beden: L · Renk: Bordo", JobVariantResultPolicy.OptionsText([(2, "Renk", "Bordo"), (1, "Beden", "L")]));
        Assert.Null(JobVariantResultPolicy.OptionsText([]));
        Assert.Null(JobVariantResultPolicy.Reason([null, " "]));
    }
}
