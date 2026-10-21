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
}
