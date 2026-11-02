using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OrderLineCancelTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 17, 0, 0, TimeSpan.Zero);
    private static OrderLine Line(decimal ordered, decimal cancelled) => new() { ExternalLineId = "L1", Sku = "S", TitleSnapshot = "T", RawStatus = "x", OrderedQuantity = ordered, CancelledQuantity = cancelled };

    [Fact]
    public void ACancellationSeenOnAHeldLineTakesTheMarketplaceTimeAndTheTimeItWasSeen()
    {
        var line = Line(2, 1);
        var reported = new DateTimeOffset(2026, 11, 1, 18, 30, 0, TimeSpan.FromHours(3));
        OrderLineCancelTime.Apply(line, previousCancelled: 0, reported, Now);
        Assert.Equal(reported, line.CancelledAt);
        Assert.Equal(TimeSpan.Zero, line.CancelledAt!.Value.Offset);
        Assert.Equal(Now, line.CancelSeenAt);
    }

    [Fact]
    public void WithoutAMarketplaceTimeTheCancellationTimeStaysUnknown()
    {
        var line = Line(1, 1);
        OrderLineCancelTime.Apply(line, previousCancelled: 0, reported: null, Now);
        Assert.Null(line.CancelledAt);
        Assert.Equal(Now, line.CancelSeenAt);
    }

    [Fact]
    public void ALineThatCameInCancelledWasNotSeenBeingCancelled()
    {
        var line = Line(1, 1);
        OrderLineCancelTime.Apply(line, previousCancelled: null, reported: null, Now);
        Assert.Null(line.CancelledAt);
        Assert.Null(line.CancelSeenAt);
    }

    [Fact]
    public void ALaterReadFillsAnUnknownTimeButNeverReplacesAKnownOne()
    {
        var first = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
        var line = Line(1, 1);
        line.CancelSeenAt = Now.AddDays(-1);
        OrderLineCancelTime.Apply(line, previousCancelled: 1, first, Now);
        Assert.Equal(first, line.CancelledAt);
        OrderLineCancelTime.Apply(line, previousCancelled: 1, first.AddDays(1), Now);
        Assert.Equal(first, line.CancelledAt);
        Assert.Equal(Now.AddDays(-1), line.CancelSeenAt);
    }

    [Fact]
    public void ALineWithNothingCancelledHasNoCancellationTime()
    {
        var line = Line(2, 0);
        line.CancelledAt = Now; line.CancelSeenAt = Now;
        OrderLineCancelTime.Apply(line, previousCancelled: 1, Now, Now);
        Assert.Null(line.CancelledAt);
        Assert.Null(line.CancelSeenAt);
    }

    [Fact]
    public void TheLineTimeWinsOverThePackageTimeAndAPackageCountsOnlyForTheLinesItHolds()
    {
        var lineTime = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var packageTime = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        RemotePackage Package(string line) => new("P-" + line, null, "Cancelled", packageTime, null, null, [new RemotePackageAllocation(line, 0, 1, 0, 0, 0)], CancelledAt: packageTime);
        RemoteOrderLine RemoteLine(string id, DateTimeOffset? at) => new(id, "S", null, "T", 1, 10, 20, "x", CancelledQuantity: 1, CancelledAt: at);
        var remote = new RemoteOrder("O", "O", Now, Now, "TRY", 0, 0, 0, "{}", "{}", "{}", [RemoteLine("A", lineTime), RemoteLine("B", null), RemoteLine("C", null)], [Package("A"), Package("B")], "{}");
        Assert.Equal(lineTime, OrderLineCancelTime.Reported("A", remote));
        Assert.Equal(packageTime, OrderLineCancelTime.Reported("B", remote));
        Assert.Null(OrderLineCancelTime.Reported("C", remote));
    }

    [Fact]
    public void TheDayCountsWholeAndPartialCancellationsApartAndUnknownTimesAside()
    {
        var store = Guid.NewGuid();
        Guid order1 = Guid.NewGuid(), order2 = Guid.NewGuid(), order3 = Guid.NewGuid(), order4 = Guid.NewGuid();
        var rows = DashboardReadService.CancellationRows([
            // Cancelled whole: two lines, three units.
            new(order1, store, "CANCELLED", 2, true), new(order1, store, "CANCELLED", 1, true),
            // Open with one product fewer.
            new(order2, store, "SHIPPED", 1, true),
            // A known line makes the order known; its line of unknown time stays apart in the units.
            new(order3, store, "PARTIALLY_CANCELLED", 1, true), new(order3, store, "PARTIALLY_CANCELLED", 2, false),
            // Time unknown (PTT AVM): counted apart.
            new(order4, store, "CANCELLED", 4, false),
        ]);
        var row = Assert.Single(rows);
        Assert.Equal(1, row.CancelledOrders);
        Assert.Equal(2, row.PartialOrders);
        Assert.Equal(5, row.CancelledUnits);
        Assert.Equal(3, row.WholeUnits);
        Assert.Equal(2, row.PartialUnits);
        Assert.Equal(1, row.UnknownTimeOrders);
        Assert.Equal(6, row.UnknownTimeUnits);
    }

    [Theory]
    // The history step is real UTC; Trendyol's orderDate runs three hours ahead and is not used.
    [InlineData("""{"status":"Cancelled","orderDate":1762264800000,"lastModifiedDate":1762260000000,"packageHistories":[{"status":"Created","createdDate":1762250000000},{"status":"Cancelled","createdDate":1762255000000}]}""", 1762255000000L)]
    [InlineData("""{"status":"UnSupplied","lastModifiedDate":1762260000000,"packageHistories":[{"status":"Created","createdDate":1762250000000}]}""", 1762260000000L)]
    [InlineData("""{"status":"UnPacked","lastModifiedDate":1762260000000,"packageHistories":[{"status":"Cancelled","createdDate":1762255000000}]}""", null)]
    [InlineData("""{"status":"Delivered","lastModifiedDate":1762260000000}""", null)]
    public void TrendyolGivesTheCancellationTimeOfAPackageThatEndsCancelled(string json, long? expectedMilliseconds)
    {
        using var document = JsonDocument.Parse(json);
        var at = TrendyolJsonMapper.CancelledAt(document.RootElement, document.RootElement.GetProperty("status").GetString()!);
        Assert.Equal(expectedMilliseconds is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, at);
    }
}
