using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductApprovalReconciliationPolicyTests
{
    [Fact]
    public void ResetsOnlyWhenEveryVariantIsConfirmedMissing()
    {
        var allMissing = new[]
        {
            new RemotePublicationStatus("100", "NOT_FOUND", null, null, null, "{}"),
            new RemotePublicationStatus("200", "not_found", null, null, null, "{}")
        };
        var partiallyFound = new[]
        {
            new RemotePublicationStatus("100", "NOT_FOUND", null, null, null, "{}"),
            new RemotePublicationStatus("200", "APPROVED", "content-1", "variant-1", null, "{}")
        };

        Assert.True(ProductApprovalReconciliationPolicy.ShouldResetMissingPublication(2, allMissing));
        Assert.False(ProductApprovalReconciliationPolicy.ShouldResetMissingPublication(2, partiallyFound));
        Assert.False(ProductApprovalReconciliationPolicy.ShouldResetMissingPublication(3, allMissing));
        Assert.False(ProductApprovalReconciliationPolicy.ShouldResetMissingPublication(0, Array.Empty<RemotePublicationStatus>()));
    }

    [Fact]
    public void DetectsMultipleApprovedContentIdsAndOnlyBlocksWritesForThatCondition()
    {
        var split = new[]
        {
            new RemotePublicationStatus("100", "APPROVED", "content-1", "variant-1", null, "{}"),
            new RemotePublicationStatus("200", "approved", "content-2", "variant-2", null, "{}"),
            new RemotePublicationStatus("300", "PENDING_APPROVAL", "content-3", "variant-3", null, "{}")
        };
        var oneContent = new[]
        {
            new RemotePublicationStatus("100", "APPROVED", "content-1", "variant-1", null, "{}"),
            new RemotePublicationStatus("200", "APPROVED", "content-1", "variant-2", null, "{}")
        };

        Assert.True(ProductApprovalReconciliationPolicy.HasSplitApprovedContents(split));
        Assert.False(ProductApprovalReconciliationPolicy.HasSplitApprovedContents(oneContent));
        Assert.True(ProductApprovalReconciliationPolicy.RequiresContentSplitReview(" product_approval_content_split "));
        Assert.False(ProductApprovalReconciliationPolicy.RequiresContentSplitReview("PRODUCT_APPROVAL_PENDING"));
    }
}
