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
