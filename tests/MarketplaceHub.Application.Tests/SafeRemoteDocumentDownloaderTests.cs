using System.Net;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

namespace MarketplaceHub.Application.Tests;

public sealed class SafeRemoteDocumentDownloaderTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    public void Private_and_special_addresses_are_rejected(string value) =>
        Assert.False(SafeRemoteDocumentDownloader.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string value) =>
        Assert.True(SafeRemoteDocumentDownloader.IsPublicAddress(IPAddress.Parse(value)));
}
