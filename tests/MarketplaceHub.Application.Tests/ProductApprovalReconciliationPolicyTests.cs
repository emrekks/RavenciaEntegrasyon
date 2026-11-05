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
    public void ACardTheMarketplaceConfirmedCreatedIsNeverResetForNotBeingListed()
    {
        var allMissing = new[]
        {
            new RemotePublicationStatus("100", "NOT_FOUND", null, null, null, "{}"),
            new RemotePublicationStatus("200", "NOT_FOUND", null, null, null, "{}")
        };

        Assert.False(ProductApprovalReconciliationPolicy.ShouldResetMissingPublication(2, allMissing, createConfirmed: true));
        Assert.True(ProductApprovalReconciliationPolicy.ShouldResetMissingPublication(2, allMissing, createConfirmed: false));
    }

    [Fact]
    public void TrendyolsDuplicateBarcodeRefusalSaysTheProductIsThere()
    {
        // Live 5.11.2026, MZ021Y-CD-34 sent again after its first create had gone through.
        Assert.True(ProductBatchResultPolicy.SaysBarcodeExists("Aynı barkodlu bir ürününüz bulunduğundan yeni ürün oluşturulamaz. Barkod: MZ021Y-CD-34"));
        Assert.False(ProductBatchResultPolicy.SaysBarcodeExists("Web Color alanı zorunludur."));
        Assert.False(ProductBatchResultPolicy.SaysBarcodeExists(null));

        // The job summary names barcodes Trendyol found in the store as found, not as unlisted.
        Assert.Equal("Trendyol 3 varyantın 3 tanesinin barkodunu mağazada zaten buldu; bu varyantların durumu onay kontrolünde barkodla sorgulanıp eşleştiriliyor.",
            ProductBatchResultPolicy.IncompleteSummary("Trendyol", 0, 3, 3));
        Assert.Equal("Trendyol batch sonucu 4 varyantın 1 tanesini listelemedi; Trendyol 4 varyantın 2 tanesinin barkodunu mağazada zaten buldu; bu varyantların durumu onay kontrolünde barkodla sorgulanıp eşleştiriliyor.",
            ProductBatchResultPolicy.IncompleteSummary("Trendyol", 1, 2, 4));
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

    [Theory]
    [InlineData(0, 15)]
    [InlineData(14, 15)]
    [InlineData(15, 15)]
    [InlineData(59, 15)]
    [InlineData(60, 30)]
    [InlineData(5 * 60 + 59, 30)]
    [InlineData(6 * 60, 120)]
    [InlineData(23 * 60 + 59, 120)]
    [InlineData(24 * 60, 120)]
    [InlineData(48 * 60 - 1, 120)]
    [InlineData(48 * 60, 6 * 60)]
    [InlineData(5 * 24 * 60, 6 * 60)]
    public void APendingApprovalIsCheckedLessOftenTheLongerItWaits(int pendingMinutes, int expectedMinutes)
    {
        var started = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

        var delay = ProductApprovalReconciliationPolicy.PendingRecheckDelay(started, started.AddDays(7), started.AddMinutes(pendingMinutes));

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), delay);
    }

    [Fact]
    public void TheLastCheckIsNotPlannedPastTheDeadline()
    {
        var started = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
        var deadline = started.AddDays(7);

        Assert.Equal(TimeSpan.FromMinutes(61), ProductApprovalReconciliationPolicy.PendingRecheckDelay(started, deadline, deadline.AddHours(-1)));
        Assert.Equal(TimeSpan.FromMinutes(1), ProductApprovalReconciliationPolicy.PendingRecheckDelay(started, deadline, deadline.AddMinutes(5)));
    }

    [Fact]
    public void OnlyAnApprovedVariantWithItsLinksIsLeftOutOfTheNextRead()
    {
        Assert.True(ProductApprovalReconciliationPolicy.IsSettledLive("LIVE", hasProductLink: true, hasVariantLink: true));
        Assert.False(ProductApprovalReconciliationPolicy.IsSettledLive("APPROVAL_PENDING", hasProductLink: true, hasVariantLink: true));
        Assert.False(ProductApprovalReconciliationPolicy.IsSettledLive("CREATE_ACCEPTED", hasProductLink: true, hasVariantLink: true));
        Assert.False(ProductApprovalReconciliationPolicy.IsSettledLive("LIVE", hasProductLink: false, hasVariantLink: true));
        Assert.False(ProductApprovalReconciliationPolicy.IsSettledLive("LIVE", hasProductLink: true, hasVariantLink: false));
    }
}
