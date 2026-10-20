using MarketplaceHub.Api.Catalog;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ApiInputLimitsTests
{
    [Theory]
    [InlineData(null, "GALLERY")]
    [InlineData("  ", "GALLERY")]
    [InlineData(" primary ", "PRIMARY")]
    public void MediaRole_NormalisesTheFormValue(string? value, string expected)
    {
        Assert.Equal(expected, CatalogEndpoints.MediaRole(value));
    }

    [Fact]
    public void MediaRole_NeverExceedsTheColumnLength()
    {
        // product_media.MediaRole is varchar(32); a longer form value used to reach the database and fail with 500.
        var role = CatalogEndpoints.MediaRole(new string('x', 5_000));

        Assert.Equal(32, role.Length);
    }

    [Theory]
    [InlineData(null, 200)]
    [InlineData(50, 50)]
    [InlineData(0, 1)]
    [InlineData(5_000, 1_000)]
    public void JobList_DefaultsToTwoHundredAndCapsAtOneThousand(int? requested, int expected)
    {
        Assert.Equal(expected, new JobListQuery(Limit: requested).EffectiveLimit);
    }
}
