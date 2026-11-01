using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaCancellationsToReadTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APartlyCancelledOrderWhoseListedLinesAreCancelledIsNotReadAgain()
    {
        var listed = new[] { new RemoteCancelledOrderLine("partial", "line-1", Now), new RemoteCancelledOrderLine("full", "line-9", Now.AddMinutes(-5)) };
        var open = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["partial"] = ["line-1"],
            ["full"] = []
        };

        Assert.Equal(["full"], MarketplaceJobProcessor.HepsiburadaCancellationsToRead(listed, open, 25));
    }

    [Fact]
    public void ALineTheListNamesWithoutAnIdOrThePanelDoesNotShowCancelledKeepsTheOrder()
    {
        var listed = new[] { new RemoteCancelledOrderLine("no-line", null, Now), new RemoteCancelledOrderLine("other-line", "line-2", Now) };
        var open = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["no-line"] = ["line-1"],
            ["other-line"] = ["line-1"]
        };

        Assert.Equal(["no-line", "other-line"], MarketplaceJobProcessor.HepsiburadaCancellationsToRead(listed, open, 25));
    }

    [Fact]
    public void AnOrderThePanelAlreadyShowsCancelledOrDoesNotHaveIsSkipped()
    {
        var listed = new[] { new RemoteCancelledOrderLine("cancelled-or-unknown", "line-1", Now) };

        Assert.Empty(MarketplaceJobProcessor.HepsiburadaCancellationsToRead(listed, new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal), 25));
    }

    [Fact]
    public void TheLatestCancellationsComeFirstWithinTheRunLimit()
    {
        var listed = new[]
        {
            new RemoteCancelledOrderLine("old", "a", Now.AddDays(-20)),
            new RemoteCancelledOrderLine("new", "b", Now),
            new RemoteCancelledOrderLine("middle", "c", Now.AddDays(-1))
        };
        var open = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["old"] = [],
            ["new"] = [],
            ["middle"] = []
        };

        Assert.Equal(["new", "middle"], MarketplaceJobProcessor.HepsiburadaCancellationsToRead(listed, open, 2));
    }

    [Fact]
    public void TheListsCancelDateIsReadAsIstanbulTimeWhenItHasNoOffset()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"totalCount":3,"items":[
              {"orderNumber":"o-1","lineItemId":"l-1","cancelDate":"2026-10-18T13:05:07.250"},
              {"orderNumber":"o-1","lineItemId":"l-2","cancelDate":"2026-10-18T10:56:49.315Z"},
              {"orderNumber":"o-2","lineItemId":"l-3"}
            ]}
            """);
        var (items, _) = MarketplaceHub.Infrastructure.Adapters.Hepsiburada.HepsiburadaJsonMapper.CancelledOrderLines(document.RootElement);

        Assert.Equal(new DateTimeOffset(2026, 10, 18, 10, 5, 7, 250, TimeSpan.Zero), items[0].CancelledAt);
        Assert.Equal(TimeSpan.Zero, items[0].CancelledAt!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 10, 56, 49, 315, TimeSpan.Zero), items[1].CancelledAt);
        Assert.Null(items[2].CancelledAt);
    }

    [Fact]
    public void AnOrderReadAgainCarriesTheListsCancelDateOnTheLinesItNames()
    {
        var listed = new[]
        {
            new RemoteCancelledOrderLine("o-1", "l-1", Now.AddHours(-2)),
            new RemoteCancelledOrderLine("o-1", " l-1 ", Now.AddHours(-1)),
            new RemoteCancelledOrderLine("o-1", "l-2", null),
            new RemoteCancelledOrderLine("o-2", "l-3", Now)
        };

        var read = MarketplaceJobProcessor.WithHepsiburadaListedCancelTimes(Order("o-1", "l-1", "l-2", "l-3"), listed);

        Assert.Equal(Now.AddHours(-1), read.Lines[0].CancelledAt);
        Assert.Null(read.Lines[1].CancelledAt);
        Assert.Null(read.Lines[2].CancelledAt);
        Assert.Equal(Now.AddHours(-1), OrderLineCancelTime.Reported("l-1", read));
        Assert.Null(OrderLineCancelTime.Reported("l-2", read));
    }

    [Fact]
    public void AnOrderTheListDoesNotNameKeepsNoCancellationTime()
    {
        var order = Order("o-9", "l-1");

        var read = MarketplaceJobProcessor.WithHepsiburadaListedCancelTimes(order, [new RemoteCancelledOrderLine("o-1", "l-1", Now)]);

        Assert.Same(order, read);
        Assert.Null(OrderLineCancelTime.Reported("l-1", read));
    }

    [Fact]
    public void ListedTimesAreKeptInUtcPerOrderAndLine()
    {
        var istanbul = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(3));
        var times = MarketplaceJobProcessor.HepsiburadaListedCancelTimes(
        [
            new RemoteCancelledOrderLine("o-1", "l-1", istanbul),
            new RemoteCancelledOrderLine("o-1", null, Now),
            new RemoteCancelledOrderLine("o-2", "l-1", null)
        ]);

        var time = Assert.Single(times);
        Assert.Equal(("o-1", "l-1"), time.Key);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero), time.Value);
        Assert.Equal(TimeSpan.Zero, time.Value.Offset);
    }

    private static RemoteOrder Order(string orderNumber, params string[] lineIds) => new(
        orderNumber, orderNumber, Now.AddDays(-1), Now, "TRY", 100m, 0m, 100m, "{}", "{}", "{}",
        [.. lineIds.Select(id => new RemoteOrderLine(id, "sku-" + id, null, "Ürün", 1m, 100m, 20m, "Cancelled", CancelledQuantity: 1m))],
        [], "{}");
}
