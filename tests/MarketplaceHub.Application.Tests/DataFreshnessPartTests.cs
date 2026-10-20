using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class DataFreshnessPartTests
{
    [Fact]
    public void A_store_that_was_never_read_is_counted_instead_of_ignored()
    {
        var read = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var older = read.AddMinutes(-10);

        var part = DataFreshnessService.Part("ORDERS", [read, null, older], instant: false);

        Assert.Equal(older, part.LastSuccessAt);
        Assert.Equal(3, part.Connections);
        Assert.Equal(1, part.NeverSynced);
    }

    [Fact]
    public void No_store_read_yet_has_no_time()
    {
        var part = DataFreshnessService.Part("QUESTIONS", [null, null], instant: false);

        Assert.Null(part.LastSuccessAt);
        Assert.Equal(2, part.NeverSynced);
    }
}
