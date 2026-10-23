using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InstantNotificationPolicyTests
{
    [Theory]
    [InlineData("https://panel.ravencia.com/", "https://panel.ravencia.com")]
    [InlineData("http://panel.ravencia.com", null)]
    [InlineData("https://trendyol-panel.example.com", null)]
    [InlineData("https://dolap.example.com", null)]
    [InlineData("https://localhost:5001", null)]
    [InlineData("", null)]
    public void ReceivingAddressMustBeHttpsAndAvoidWordsTrendyolRejects(string configured, string? expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketplace:PublicBaseUrl"] = configured }).Build();
        var service = new InstantNotificationService(null!, null!, new TokenHasher(new byte[32]), new EphemeralDataProtectionProvider(), configuration, TimeProvider.System, NullLogger<InstantNotificationService>.Instance);

        Assert.Equal(expected, service.PublicBaseUrl());
    }

    [Fact]
    public void HepsiburadaDefinitionIsPreparedWhenItsVersion7IdWasMade()
    {
        var made = DateTimeOffset.Parse("2026-10-22T18:12:16.824Z");

        Assert.Equal(made, InstantNotificationService.PreparedAt(Guid.CreateVersion7(made)));
        Assert.Equal(DateTimeOffset.Parse("2026-10-22T18:12:16.784Z"), InstantNotificationService.PreparedAt(Guid.Parse("01a164d0-5310-7b34-b7c6-ca8c607ba86c")));
    }

    [Fact]
    public void ShopifyRegistrationIsActiveOnlyWhileEveryTopicIsListed()
    {
        RemoteWebhook Listed(string id) => new(id, "https://panel.ravencia.com/api/v1/hooks/x/y", "ACTIVE", null);

        Assert.Equal("ACTIVE", InstantNotificationService.RemoteState("11,12", [Listed("11"), Listed("12"), Listed("99")]));
        Assert.Equal("PASSIVE", InstantNotificationService.RemoteState("11,12", [Listed("12")]));
        Assert.Equal("MISSING", InstantNotificationService.RemoteState("11,12", [Listed("99")]));
        Assert.Equal("ACTIVE", InstantNotificationService.RemoteState("remote-webhook-1", [Listed("remote-webhook-1")]));
    }

    [Fact]
    public void ShopifySubscriptionIdsAreKeptAsTheirNumber()
    {
        Assert.Equal("1234567890", MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.CompactWebhookId("gid://shopify/WebhookSubscription/1234567890"));
        Assert.Equal("gid://shopify/WebhookSubscription/1234567890", MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.WebhookGid("1234567890"));
        Assert.True(string.Join(',', Enumerable.Repeat("12345678901234", MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.WebhookTopics.Length)).Length <= 256);
    }

    [Theory]
    [InlineData("""{"orderNumber":"1","orderCountryCode":"ro"}""", "RO")]
    [InlineData("""{"orderNumber":"1","orderCountryCode":"TR"}""", "TR")]
    [InlineData("""{"orderNumber":"1","orderCountryCode":"US"}""", null)]
    [InlineData("""{"orderNumber":"1"}""", null)]
    [InlineData("not json", null)]
    public void WebhookPackageStorefrontComesFromTheOrderCountry(string raw, string? expected)
    {
        Assert.Equal(expected, MarketplaceJobProcessor.TrendyolWebhookStorefront(raw));
    }
}
