using System.Globalization;
using System.Net;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ADAPTOR-TY-HB-3 step 2: a package without a recorded status time is looked for in the order stream around its
// order's last change (or order date) when that lies within the stream's three months; only a package outside every
// window is answered TRENDYOL_PACKAGE_OUT_OF_READ_WINDOW without a request.
public sealed class TrendyolPackageReadWindowTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[],"hasMore":false}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static TrendyolRequestContext Account() => new(
        new PlatformConnection { PlatformCode = "TRENDYOL", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Trendyol", ExternalStoreId = "seller-1", ApiVersion = "V2" },
        new Uri("https://apigw.example/"), "key", "secret", "seller-1 - SelfIntegration", false);

    private static async Task<(AdapterResult<RemoteOrderPackage> Result, Recorder Handler)> ReadAsync(PackageLookup lookup)
    {
        var handler = new Recorder();
        var client = new TrendyolHttpClient(new Factory(handler), null!, new ConfigurationBuilder().Build(), Options.Create(new TrendyolOptions()), TimeProvider.System, NullLogger<TrendyolHttpClient>.Instance);
        return (await client.ReadShipmentPackageAsync(Account(), "PKG-1", lookup, CancellationToken.None), handler);
    }

    private static long Query(Uri uri, string name) =>
        long.Parse(uri.Query.TrimStart('?').Split('&').Single(part => part.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..], CultureInfo.InvariantCulture);

    [Fact]
    public async Task APackageOutsideEveryWindowIsNotAskedFor()
    {
        var old = DateTimeOffset.UtcNow.AddMonths(-4);

        var (result, handler) = await ReadAsync(new PackageLookup(null, new OrderLookupHint(old, old, "TR")));

        Assert.Empty(handler.Requests);
        Assert.Equal(TrendyolHttpClient.PackageNotAddressableCode, result.Error!.Code);
    }

    [Fact]
    public async Task APackageWithoutStatusTimeIsLookedForAroundItsOrdersLastChange()
    {
        var now = DateTimeOffset.UtcNow;
        var lastChange = now.AddDays(-40);

        var (result, handler) = await ReadAsync(new PackageLookup(null, new OrderLookupHint(now.AddDays(-45), lastChange, "TR")));

        var stream = Assert.Single(handler.Requests);
        Assert.Contains("/orders/stream", stream.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(lastChange.AddDays(-6).ToUnixTimeMilliseconds(), Query(stream, "lastModifiedStartDate"));
        Assert.Equal(lastChange.AddDays(6).ToUnixTimeMilliseconds(), Query(stream, "lastModifiedEndDate"));
        // Trendyol was asked and did not list the package: its own "not found", not the unknown state.
        Assert.Equal("REMOTE_PACKAGE_NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public async Task WithoutALastChangeTheOrderDateAnchorsTheStream()
    {
        var orderedAt = DateTimeOffset.UtcNow.AddDays(-40);

        var (_, handler) = await ReadAsync(new PackageLookup(null, new OrderLookupHint(orderedAt, null, "TR")));

        var stream = Assert.Single(handler.Requests);
        Assert.Equal(orderedAt.AddDays(-6).ToUnixTimeMilliseconds(), Query(stream, "lastModifiedStartDate"));
    }

    [Fact]
    public void TheStatusTimeWindowIsUnchanged()
    {
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var status = now.AddDays(-50);
        Assert.Equal((status.AddDays(-6), status.AddDays(6)), TrendyolHttpClient.PackageStreamWindow(status, new OrderLookupHint(now.AddMonths(-5), null, "TR"), now));
        Assert.Equal((now.AddDays(-2).AddDays(-6), now), TrendyolHttpClient.PackageStreamWindow(now.AddDays(-2), null, now));
        // A status time older than the stream's months is not read, and the order's dates do not replace it.
        Assert.Null(TrendyolHttpClient.PackageStreamWindow(now.AddMonths(-4), new OrderLookupHint(now.AddDays(-40), now.AddDays(-40), "TR"), now));
        Assert.Null(TrendyolHttpClient.PackageStreamWindow(null, null, now));
    }
}
