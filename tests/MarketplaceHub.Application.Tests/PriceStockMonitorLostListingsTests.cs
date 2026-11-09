using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PriceStockMonitorLostListingsTests
{
    [Fact]
    public void LostListings_GroupsByProductAndHealth_MostBrokenVariantsFirst_WithSortedCodesAndTheFirstBreak()
    {
        var connection = Guid.CreateVersion7();
        var yonca = Guid.CreateVersion7();
        var kazak = Guid.CreateVersion7();
        var at = DateTimeOffset.Parse("2026-11-03T00:14:48Z");
        PriceStockMonitorService.LostLink Link(Guid product, string title, string health, string sku, int seconds) =>
            new(connection, product, title, health, sku, at.AddSeconds(seconds));
        var lost = PriceStockMonitorService.LostListings([
            Link(kazak, "Kazak", MarketplaceVariantLinkHealth.NotFound, "RYP006-01", 0),
            Link(yonca, "Yonca", MarketplaceVariantLinkHealth.NotFound, "MZ050YNC-02", 3),
            Link(yonca, "Yonca", MarketplaceVariantLinkHealth.NotFound, "MZ050YNC-01", 1),
            Link(yonca, "Yonca", MarketplaceVariantLinkHealth.Rejected, "MZ050YNC-03", 9)
        ]);

        Assert.Equal(3, lost.Count);
        Assert.Equal((yonca, MarketplaceVariantLinkHealth.NotFound, 2), (lost[0].ProductId, lost[0].HealthStatus, lost[0].Variants));
        Assert.Equal(["MZ050YNC-01", "MZ050YNC-02"], lost[0].Skus);
        Assert.Equal(at.AddSeconds(1), lost[0].Since);
        Assert.Equal(["Kazak", "Yonca"], lost.Skip(1).Select(x => x.Title));
    }

    [Fact]
    public void LostListings_KeepsAtMostTheLimitOfProducts()
    {
        var connection = Guid.CreateVersion7();
        var links = Enumerable.Range(0, PriceStockMonitorService.LostListingProductLimit + 5)
            .Select(i => new PriceStockMonitorService.LostLink(connection, Guid.CreateVersion7(), $"Ürün {i:D2}", MarketplaceVariantLinkHealth.NotFound, $"SKU-{i}", null));
        Assert.Equal(PriceStockMonitorService.LostListingProductLimit, PriceStockMonitorService.LostListings(links).Count);
    }
}
