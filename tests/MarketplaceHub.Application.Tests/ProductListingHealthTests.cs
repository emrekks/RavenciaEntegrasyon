using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductListingHealthTests
{
    [Theory]
    [InlineData("NOT_FOUND", true)]
    [InlineData("REJECTED", true)]
    [InlineData("SUSPECT", false)]
    [InlineData("UNVERIFIED", false)]
    [InlineData(null, false)]
    public void OnlyAnExplicitBrokenLinkCountsAsMissing(string? health, bool missing) =>
        Assert.Equal(missing, ProductListingHealth.CountsAsMissing(health, productArchived: false, profileStatus: "LIVE"));

    [Fact]
    public void AnArchivedProductOrListingIsNeverMissing()
    {
        Assert.False(ProductListingHealth.CountsAsMissing("NOT_FOUND", productArchived: true, profileStatus: "LIVE"));
        Assert.False(ProductListingHealth.CountsAsMissing("NOT_FOUND", productArchived: false, profileStatus: "ARCHIVED"));
    }

    [Theory]
    // Nothing missing: the status is the one the panel already showed.
    [InlineData("LIVE", 3, 0, 3, "LIVE")]
    [InlineData(null, 3, 0, 3, "LINKED")]
    [InlineData(null, 1, 0, 3, "PARTIAL_LINKED")]
    [InlineData(null, 0, 0, 3, "UNLINKED")]
    // Some variants missing: partly published.
    [InlineData("LIVE", 2, 1, 3, "PARTIAL_LIVE")]
    [InlineData(null, 2, 1, 3, "PARTIAL_LINKED")]
    [InlineData("LINKED", 2, 1, 3, "PARTIAL_LINKED")]
    // Every matched or published variant missing: Bulunamadı.
    [InlineData("LIVE", 0, 3, 3, "NOT_FOUND")]
    [InlineData(null, 0, 2, 3, "NOT_FOUND")]
    [InlineData("UNKNOWN", 0, 1, 1, "NOT_FOUND")]
    // A send in progress, a refusal or an archive keeps its own status.
    [InlineData("QUEUED", 0, 3, 3, "QUEUED")]
    [InlineData("CREATE_REJECTED", 1, 2, 3, "CREATE_REJECTED")]
    [InlineData("ARCHIVED", 0, 3, 3, "ARCHIVED")]
    public void TheProductStatusFollowsTheMissingVariants(string? profileStatus, int matched, int missing, int total, string expected) =>
        Assert.Equal(expected, ProductListingHealth.Status(profileStatus, matched, missing, total));

    [Theory]
    [InlineData("ACTIVE", 3, 3, 0, true)]
    [InlineData("ACTIVE", 3, 2, 1, false)]
    [InlineData("PARTIAL", 3, 2, 1, true)]
    [InlineData("PARTIAL", 3, 0, 3, false)]
    [InlineData("NOT_FOUND", 3, 0, 3, true)]
    [InlineData("NOT_FOUND", 3, 0, 1, true)]
    [InlineData("NOT_FOUND", 3, 1, 2, false)]
    [InlineData("NOT_FOUND", 3, 0, 0, false)]
    [InlineData("PASSIVE", 3, 0, 0, true)]
    [InlineData("PASSIVE", 3, 0, 2, false)]
    public void ThePlatformFilterSeparatesMissingProductsFromPassiveOnes(string state, int total, int matched, int missing, bool matches) =>
        Assert.Equal(matches, ProductListingHealth.MatchesFilter(state, total, matched, missing));
}
