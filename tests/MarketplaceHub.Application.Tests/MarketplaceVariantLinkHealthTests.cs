using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceVariantLinkHealthTests
{
    [Theory]
    [InlineData(0, 10, false)]
    [InlineData(4, 10, false)]
    [InlineData(5, 10, true)]
    [InlineData(10, 10, true)]
    [InlineData(1, 0, true)]
    public void AFullScanMarksMissingCodesOnlyWhenItSawEnoughOfTheLinks(int seen, int healthy, bool marks) =>
        Assert.Equal(marks, MarketplaceVariantLinkHealth.FullScanCanMarkMissing(seen, healthy));

    [Theory]
    [InlineData("Listing bulunamadı", null)]
    [InlineData("REMOTE_VALIDATION_FAILED", "Listing not found")]
    [InlineData("invalid sku", null)]
    [InlineData("PTTAVM_PRODUCT_UPDATE_PARTIAL", "Ürün bulunamadı.")]
    [InlineData("N11_SKU_REJECTED", "Barkod bulunamadı")]
    [InlineData("Barcode not found", null)]
    public void ARefusalForAnUnknownCodeBreaksTheLink(string code, string? message) =>
        Assert.True(MarketplaceVariantLinkHealth.MeansUnknownCode(code, message));

    [Theory]
    [InlineData("REMOTE_VALIDATION_FAILED", null)]
    [InlineData("Product'a ait attribute bulunamadı!", null)]
    [InlineData("Kategori bulunamadı", null)]
    [InlineData("Fiyat ürün bulunamadı nedeniyle güncellenemedi", null)]
    [InlineData("PAZARAMA_PRICE_REJECTED", "Liste fiyatı satış fiyatından küçük olamaz")]
    [InlineData("TOO_MANY_REQUESTS", null)]
    [InlineData(null, null)]
    public void OtherRefusalsLeaveTheLinkAlone(string? code, string? message) =>
        Assert.False(MarketplaceVariantLinkHealth.MeansUnknownCode(code, message));

    [Fact]
    public void ASeenLinkIsHealthyAgainAndARefusalBreaksItOnce()
    {
        var at = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
        var link = new MarketplaceVariantLink { ExternalId = "HBCV1", HealthStatus = MarketplaceVariantLinkHealth.NotFound, HealthReason = "x" };
        MarketplaceVariantLinkHealth.MarkSeen(link, at);
        Assert.Null(link.HealthStatus);
        Assert.Null(link.HealthReason);
        Assert.Equal(at, link.LastSeenAt);
        Assert.Equal(at, link.HealthChangedAt);

        MarketplaceVariantLinkHealth.MarkRejected(link, "Listing bulunamadı", at.AddHours(1));
        MarketplaceVariantLinkHealth.MarkRejected(link, "Listing bulunamadı", at.AddHours(2));
        Assert.Equal(MarketplaceVariantLinkHealth.Rejected, link.HealthStatus);
        Assert.Equal(at.AddHours(1), link.HealthChangedAt);
        Assert.True(MarketplaceVariantLinkHealth.IsBroken(link.HealthStatus));
    }

    [Fact]
    public void OnlyATwiceRefusedCodeIsLeftOutOfBulkSendsAndASuspectOrUnverifiedOneIsNotBroken()
    {
        Assert.True(MarketplaceVariantLinkHealth.SkipsBulkPush(MarketplaceVariantLinkHealth.Rejected));
        Assert.False(MarketplaceVariantLinkHealth.SkipsBulkPush(MarketplaceVariantLinkHealth.NotFound));
        Assert.False(MarketplaceVariantLinkHealth.SkipsBulkPush(MarketplaceVariantLinkHealth.Suspect));
        Assert.False(MarketplaceVariantLinkHealth.SkipsBulkPush(null));
        Assert.False(MarketplaceVariantLinkHealth.IsBroken(MarketplaceVariantLinkHealth.Suspect));
        Assert.False(MarketplaceVariantLinkHealth.IsBroken(MarketplaceVariantLinkHealth.Unverified));
    }
}
