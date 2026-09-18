using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolWebhookIdentityTests
{
    [Fact]
    public void PropertyOrderAndWhitespaceDoNotCreateANewEvent()
    {
        const string first = "{\"totalElements\":1,\"content\":[{\"id\":\"pkg-1\",\"orderNumber\":\"ord-1\",\"status\":\"Created\",\"lastModifiedDate\":1760000000000}]}";
        const string reordered = "{\n  \"content\": [ { \"lastModifiedDate\": 1760000000000, \"status\": \"Created\", \"orderNumber\": \"ord-1\", \"id\": \"pkg-1\" } ],\n  \"page\": 0\n}";

        var firstIdentity = TrendyolWebhookIdentity.Create(System.Text.Encoding.UTF8.GetBytes(first));
        var reorderedIdentity = TrendyolWebhookIdentity.Create(System.Text.Encoding.UTF8.GetBytes(reordered));

        Assert.NotNull(firstIdentity);
        Assert.NotNull(reorderedIdentity);
        Assert.Equal(firstIdentity.Value.ExternalMessageId, reorderedIdentity.Value.ExternalMessageId);
        Assert.NotEqual(firstIdentity.Value.PayloadHash, reorderedIdentity.Value.PayloadHash);
    }

    [Fact]
    public void PackageChangeCreatesANewEvent()
    {
        const string created = "{\"content\":[{\"id\":\"pkg-1\",\"orderNumber\":\"ord-1\",\"status\":\"Created\",\"lastModifiedDate\":1760000000000}]}";
        const string shipped = "{\"content\":[{\"id\":\"pkg-1\",\"orderNumber\":\"ord-1\",\"status\":\"Shipped\",\"lastModifiedDate\":1760000100000}]}";

        var createdIdentity = TrendyolWebhookIdentity.Create(System.Text.Encoding.UTF8.GetBytes(created));
        var shippedIdentity = TrendyolWebhookIdentity.Create(System.Text.Encoding.UTF8.GetBytes(shipped));

        Assert.NotNull(createdIdentity);
        Assert.NotNull(shippedIdentity);
        Assert.NotEqual(createdIdentity.Value.ExternalMessageId, shippedIdentity.Value.ExternalMessageId);
    }

    [Fact]
    public void PackageCollectionOrderDoesNotCreateANewEvent()
    {
        const string first = "{\"content\":[{\"id\":\"pkg-1\",\"orderNumber\":\"ord-1\"},{\"id\":\"pkg-2\",\"orderNumber\":\"ord-1\"}]}";
        const string reversed = "{\"content\":[{\"orderNumber\":\"ord-1\",\"id\":\"pkg-2\"},{\"orderNumber\":\"ord-1\",\"id\":\"pkg-1\"}]}";

        var firstIdentity = TrendyolWebhookIdentity.Create(System.Text.Encoding.UTF8.GetBytes(first));
        var reversedIdentity = TrendyolWebhookIdentity.Create(System.Text.Encoding.UTF8.GetBytes(reversed));

        Assert.NotNull(firstIdentity);
        Assert.NotNull(reversedIdentity);
        Assert.Equal(firstIdentity.Value.ExternalMessageId, reversedIdentity.Value.ExternalMessageId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"content\":null}")]
    [InlineData("{\"content\":\"not-an-array\"}")]
    [InlineData("not-json")]
    public void InvalidWebhookContractHasNoIdentity(string payload)
    {
        Assert.Null(TrendyolWebhookIdentity.Create(System.Text.Encoding.UTF8.GetBytes(payload)));
    }
}
