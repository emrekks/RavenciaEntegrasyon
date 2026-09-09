using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
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

    [Fact]
    public void APackageFragmentCanBeValidatedBeforeSiblingPackagesAreLoaded()
    {
        var accepted = PackageIngestionSafety.TryNormalizeAll(
            new Dictionary<string, decimal> { ["line-1"] = 2m },
            [new("line-1", 1m, 0m, 1m, 0m, 0m)],
            MarketplaceHub.Domain.ShipmentPackageStatus.Shipped,
            out var normalized);

        Assert.True(accepted);
        Assert.Equal(1m, normalized["line-1"].ActiveAllocatedQuantity);
        Assert.Equal(1m, normalized["line-1"].ShippedQuantity);
    }

    [Fact]
    public void SplitPackages_SumTheLatestAllocationOfEachCurrentPackage()
    {
        var lineId = Guid.NewGuid();
        var firstAt = DateTimeOffset.Parse("2026-09-09T10:00:00Z");
        var secondAt = DateTimeOffset.Parse("2026-09-09T10:01:00Z");
        var first = Package("package-1", firstAt);
        var second = Package("package-2", secondAt);

        var result = PackageLineProjectionPolicy.Recalculate(
            [first, second],
            [
                Allocation(first, lineId, firstAt, cancelled: 1),
                Allocation(second, lineId, secondAt, cancelled: 1)
            ]);

        Assert.Equal(2m, result[lineId].CancelledQuantity);
    }

    [Fact]
    public void ReplayedAndOutOfOrderAllocations_DoNotChangeTheLatestProjection()
    {
        var lineId = Guid.NewGuid();
        var oldAt = DateTimeOffset.Parse("2026-09-09T10:00:00Z");
        var currentAt = DateTimeOffset.Parse("2026-09-09T10:01:00Z");
        var package = Package("package-1", currentAt);

        var result = PackageLineProjectionPolicy.Recalculate(
            [package],
            [
                Allocation(package, lineId, oldAt, shipped: 1),
                Allocation(package, lineId, currentAt, delivered: 1)
            ]);

        Assert.Equal(0m, result[lineId].ShippedQuantity);
        Assert.Equal(1m, result[lineId].DeliveredQuantity);
    }

    [Fact]
    public void ReplacementPackage_DoesNotDoubleCountTheParentAllocation()
    {
        var lineId = Guid.NewGuid();
        var parentAt = DateTimeOffset.Parse("2026-09-09T10:00:00Z");
        var replacementAt = DateTimeOffset.Parse("2026-09-09T10:01:00Z");
        var parent = Package("parent", parentAt);
        var replacement = Package("replacement", replacementAt, origin: "parent");

        var result = PackageLineProjectionPolicy.Recalculate(
            [parent, replacement],
            [
                Allocation(parent, lineId, parentAt, delivered: 1),
                Allocation(replacement, lineId, replacementAt, delivered: 1)
            ]);

        Assert.Equal(1m, result[lineId].DeliveredQuantity);
    }

    [Fact]
    public void SplitPackageOrder_MergesLineQuantityAcrossCurrentPackages()
    {
        var occurredAt = DateTimeOffset.Parse("2026-09-09T10:00:00Z");
        var line = new RemoteOrderLine("line-1", "SKU-1", "BAR-1", "Ürün", 1, 10, 20, "Delivered");
        var firstPackage = new RemotePackage(
            "package-1",
            null,
            "Delivered",
            occurredAt,
            null,
            null,
            [new RemotePackageAllocation("line-1", 1, 0, 1, 1, 0)]);
        var secondPackage = firstPackage with
        {
            ExternalPackageId = "package-2",
            OccurredAt = occurredAt.AddMinutes(1)
        };
        var first = new RemoteOrder("order-1", "order-1", occurredAt, occurredAt, "TRY", 10, 0, 10, "{}", "{}", "{}", [line], [firstPackage], "{}");
        var second = first with { LastModifiedAt = occurredAt.AddMinutes(1), Packages = [secondPackage] };

        var merged = TrendyolJsonMapper.MergeOrderPackages([first, second], "order-1");

        Assert.NotNull(merged);
        Assert.Equal(2m, Assert.Single(merged!.Lines).Quantity);
        Assert.True(PackageIngestionSafety.TryNormalizeOrder(
            new Dictionary<string, decimal> { ["line-1"] = 2m },
            merged.Packages,
            out var normalized));
        Assert.Equal(2m, normalized["line-1"].ActiveAllocatedQuantity);
    }

    private static ShipmentPackage Package(string externalId, DateTimeOffset occurredAt, string? origin = null) => new()
    {
        Id = Guid.NewGuid(),
        ExternalPackageId = externalId,
        OriginExternalPackageId = origin,
        StatusOccurredAt = occurredAt,
        Status = ShipmentPackageStatus.Delivered,
        RawStatus = "Delivered"
    };

    private static PackageLineAllocation Allocation(ShipmentPackage package, Guid lineId, DateTimeOffset occurredAt, decimal cancelled = 0, decimal shipped = 0, decimal delivered = 0) => new()
    {
        Id = Guid.NewGuid(),
        PackageId = package.Id,
        OrderLineId = lineId,
        SourceEventId = PackageIngestionSafety.EventId(package.ExternalPackageId, occurredAt),
        CancelledQuantity = cancelled,
        ShippedQuantity = shipped,
        DeliveredQuantity = delivered
    };
}
