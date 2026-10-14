using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceVariantLinkCoverageTests
{
    [Fact]
    public void IsComplete_IgnoresCaseWhitespaceAndDuplicateRequiredIds()
    {
        var complete = MarketplaceVariantLinkCoverage.IsComplete(
            [" HBCV0000DTYGZD ", "hbcv0000dtygzd"],
            ["hbcv0000dtygzd"]);

        Assert.True(complete);
    }

    [Fact]
    public void IsComplete_ReturnsFalseWhenAnyExternalVariantIsUnlinked()
    {
        var complete = MarketplaceVariantLinkCoverage.IsComplete(
            ["HBCV0000DTYGZD", "HBCV0000OTHER"],
            ["HBCV0000DTYGZD"]);

        Assert.False(complete);
    }

    [Fact]
    public void IsComplete_TreatsAnEmptyRemoteVariantSetAsComplete()
    {
        var complete = MarketplaceVariantLinkCoverage.IsComplete([], ["unrelated"]);

        Assert.True(complete);
    }
}
