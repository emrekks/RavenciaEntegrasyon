using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaMissingOrderLineTests
{
    [Fact]
    public void OlderOrderDetailCanAddSecondProductMissingFromClaimImport()
    {
        var first = Remote("line-1", "SKU-1");
        var second = Remote("line-2", "SKU-2");
        var existing = new OrderLine { ExternalLineId = "line-1", Sku = "SKU-1", TitleSnapshot = "First", RawStatus = "ClaimCreated" };
        Assert.False(HepsiburadaOrderLineMatcher.CanAddMissingLine(first, [existing], [first, second]));
        Assert.True(HepsiburadaOrderLineMatcher.CanAddMissingLine(second, [existing], [first, second]));
    }

    [Fact]
    public void ClaimLineWithOriginalIdentityIsNotInsertedAgain()
    {
        var remote = Remote("line-1", "SELLER-SKU");
        var existing = new OrderLine { ExternalLineId = "claim-1", Sku = "HB-SKU", TitleSnapshot = "First", RawStatus = "ClaimCreated",
            SourceSnapshotJson = "{\"lineItemId\":\"line-1\"}" };
        Assert.False(HepsiburadaOrderLineMatcher.CanAddMissingLine(remote, [existing], [remote]));
    }

    [Fact]
    public void AmbiguousSameProductLinesAreNotDuplicated()
    {
        var first = Remote("line-1", "SKU-1");
        var second = Remote("line-2", "SKU-1");
        var existing = new OrderLine { ExternalLineId = "claim-1", Sku = "SKU-1", TitleSnapshot = "First", RawStatus = "ClaimCreated" };
        Assert.False(HepsiburadaOrderLineMatcher.CanAddMissingLine(second, [existing], [first, second]));
    }

    private static RemoteOrderLine Remote(string id, string sku) => new(id, sku, null, sku, 1, 100, 20, "Open", "{}");
}
