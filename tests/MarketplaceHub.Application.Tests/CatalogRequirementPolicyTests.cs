using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CatalogRequirementPolicyTests
{
    [Theory]
    [InlineData("Web Color")]
    [InlineData("Web-Color")]
    [InlineData("Web_Renk")]
    [InlineData("webcolour")]
    public void IsWebColorAttributeName_RecognizesMarketplaceAliases(string value)
    {
        Assert.True(CatalogService.IsWebColorAttributeName(value));
    }

    [Theory]
    [InlineData("Renk")]
    [InlineData("Web Colorway")]
    [InlineData("")]
    public void IsWebColorAttributeName_DoesNotClassifyRegularAttributes(string value)
    {
        Assert.False(CatalogService.IsWebColorAttributeName(value));
    }
}
