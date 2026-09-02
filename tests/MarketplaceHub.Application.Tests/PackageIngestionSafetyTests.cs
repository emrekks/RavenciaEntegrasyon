using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PackageIngestionSafetyTests
{
    [Fact]
    public void MissingAllocationsAreRejectedWhenOrderHasLines()
    {
        var accepted = PackageIngestionSafety.TryNormalizeAll(
            new Dictionary<string, decimal> { ["line-1"] = 1m },
            [],
            MarketplaceHub.Domain.ShipmentPackageStatus.Delivered,
            out _);

        Assert.False(accepted);
    }

    [Fact]
    public void ValidAllocationsAreNormalized()
    {
        var accepted = PackageIngestionSafety.TryNormalizeAll(
            new Dictionary<string, decimal> { ["line-1"] = 1m },
            [new("line-1", 1m, 0m, 1m, 1m, 0m)],
            MarketplaceHub.Domain.ShipmentPackageStatus.Delivered,
            out var normalized);

        Assert.True(accepted);
        Assert.Equal(1m, normalized["line-1"].DeliveredQuantity);
    }
}
