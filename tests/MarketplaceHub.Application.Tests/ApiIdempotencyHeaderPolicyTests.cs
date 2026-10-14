using MarketplaceHub.Api.Catalog;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ApiIdempotencyHeaderPolicyTests
{
    [Theory]
    [InlineData("Idempotency-Key")]
    [InlineData("X-Idempotency-Key")]
    public void ReadsStandardAndLegacyHeaders(string headerName)
    {
        var headers = new HeaderDictionary { [headerName] = "same-request-key" };

        var found = ApiIdempotencyHeaderPolicy.TryGetKey(headers, out var key, out var conflictingKeys);

        Assert.True(found);
        Assert.False(conflictingKeys);
        Assert.Equal("same-request-key", key);
    }

    [Fact]
    public void RejectsDifferentValuesInBothHeaders()
    {
        var headers = new HeaderDictionary
        {
            ["Idempotency-Key"] = "first-key",
            ["X-Idempotency-Key"] = "second-key"
        };

        var found = ApiIdempotencyHeaderPolicy.TryGetKey(headers, out _, out var conflictingKeys);

        Assert.False(found);
        Assert.True(conflictingKeys);
    }

    [Fact]
    public void ReportsMissingKey()
    {
        Assert.False(ApiIdempotencyHeaderPolicy.TryGetKey(new HeaderDictionary(), out var key, out var conflictingKeys));
        Assert.Empty(key);
        Assert.False(conflictingKeys);
    }
}
