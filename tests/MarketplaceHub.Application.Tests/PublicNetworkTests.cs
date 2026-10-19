using System.Net;
using MarketplaceHub.Api.Security;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PublicNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void PrivateAndLocalAddressesAreRefused(string address)
    {
        Assert.False(PublicNetwork.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("185.60.216.35")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreAllowed(string address)
    {
        Assert.True(PublicNetwork.IsPublic(IPAddress.Parse(address)));
    }
}
