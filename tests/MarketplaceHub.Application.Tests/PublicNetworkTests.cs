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

    [Fact]
    public void ImageTypeComesFromTheBytes()
    {
        Assert.Equal("image/jpeg", PublicNetwork.SniffImageType([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/png", PublicNetwork.SniffImageType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal("image/gif", PublicNetwork.SniffImageType("GIF89a...."u8));
        Assert.Equal("image/webp", PublicNetwork.SniffImageType("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(PublicNetwork.SniffImageType("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8));
        Assert.Null(PublicNetwork.SniffImageType("<html><body>x</body></html>"u8));
        Assert.Null(PublicNetwork.SniffImageType([]));
    }

    [Fact]
    public async Task ProxiedImages_AreServedAsWhatTheBytesAre_AndScriptableOnesAreRefused()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        using var mislabelled = new HttpClient(new FixedAnswer(png, "image/svg+xml"));
        var found = await PublicNetwork.GetImageAsync(mislabelled, new Uri("https://cdn.example.com/a.png"), 1024, CancellationToken.None);
        Assert.Equal("image/png", found?.ContentType);

        using var svg = new HttpClient(new FixedAnswer("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(), "image/svg+xml"));
        Assert.Null(await PublicNetwork.GetImageAsync(svg, new Uri("https://cdn.example.com/a.svg"), 1024, CancellationToken.None));
    }

    private sealed class FixedAnswer(byte[] body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
