using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MappingTemplateStoreTests
{
    [Theory]
    [InlineData("1182/47", "47")]
    [InlineData("1182/348", "348")]
    [InlineData("1182", null)]
    [InlineData("1182/", null)]
    [InlineData("/47", null)]
    public void ValueScope_YieldsTheMarketplaceAttribute(string scope, string? expected)
    {
        Assert.Equal(expected, MappingTemplateStore.AttributeOfValueScope(scope));
    }
}
