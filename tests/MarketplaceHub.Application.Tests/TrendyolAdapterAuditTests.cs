using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class TrendyolAdapterAuditTests
{
    [Fact]
    public void RetryAfterIsReadAsSecondsOrAsADate()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(30), TrendyolHttpClient.RetryAfterDelay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), now));
        Assert.Equal(TimeSpan.FromSeconds(90), TrendyolHttpClient.RetryAfterDelay(new RetryConditionHeaderValue(now.AddSeconds(90)), now));
        Assert.Null(TrendyolHttpClient.RetryAfterDelay(new RetryConditionHeaderValue(now.AddSeconds(-5)), now));
        Assert.Null(TrendyolHttpClient.RetryAfterDelay(null, now));
    }

    [Fact]
    public void ALineWithoutAVatRateNeverTakesTheVatBaseAmountAsTheRate()
    {
        const string json = """
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Created","lastModifiedDate":1760000000000,"lines":[
          {"lineId":"line-1","stockCode":"SKU-1","productName":"Ürün","quantity":1,"lineItemPrice":120,"vatBaseAmount":100},
          {"lineId":"line-2","stockCode":"SKU-2","productName":"Ürün 2","quantity":1,"lineItemPrice":120,"vatRate":20,"vatBaseAmount":100}
        ]}]}
        """;

        var page = TrendyolJsonMapper.Orders(json);

        // G30: the line is kept with the package's rate (20), never with vatBaseAmount (100).
        var lines = Assert.Single(page.Items).Lines;
        Assert.Equal(["line-1", "line-2"], lines.Select(line => line.ExternalLineId).ToArray());
        Assert.All(lines, line => Assert.Equal(20m, line.VatRate));
        var issue = Assert.Single(page.Issues!);
        Assert.Equal("ORDER_PACKAGE_LINE_VAT_RATE_INFERRED", issue.Code);
        Assert.Equal("pkg-1:line-1", issue.Identity);
    }

    [Fact]
    public void InvoiceValueAndInvoiceObservationKeepTheirTwoMeaningsInOnePlace()
    {
        var authoritativeEmpty = new RemotePackageInvoiceObservation(null, null, null, null, ReferencesAreAuthoritative: true);
        var withNumber = new RemotePackageInvoiceObservation(null, "INV-1", null, null);

        Assert.False(TrendyolJsonMapper.HasInvoiceValue(authoritativeEmpty));
        Assert.True(TrendyolJsonMapper.HasInvoiceObservation(authoritativeEmpty));
        Assert.True(TrendyolJsonMapper.HasInvoiceValue(withNumber));
        Assert.True(TrendyolJsonMapper.HasInvoiceObservation(withNumber));
        Assert.False(TrendyolJsonMapper.HasInvoiceObservation(null));
    }

    [Fact]
    public async Task ThePackageStreamScanStopsAfterItsPageCap()
    {
        var handler = new EndlessStream();
        var client = new TrendyolHttpClient(new Factory(handler), null!, new ConfigurationBuilder().Build(), Options.Create(new TrendyolOptions()), TimeProvider.System, NullLogger<TrendyolHttpClient>.Instance);
        var account = new TrendyolRequestContext(
            new PlatformConnection { PlatformCode = "TRENDYOL", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Trendyol", ExternalStoreId = "seller-1", ApiVersion = "V2" },
            new Uri("https://apigw.example/"), "key", "secret", "seller-1 - SelfIntegration", false);
        var end = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var scan = await client.ScanPackageStreamAsync(account, "PKG-MISSING", null, null, end.AddDays(-12), end, "TR", CancellationToken.None);

        Assert.Null(scan.Result);
        Assert.Equal(TrendyolHttpClient.PackageScanIncompleteCode, scan.StreamError?.Code);
        Assert.Equal(AdapterErrorClass.TransientNetwork, scan.StreamError!.Class);
        Assert.Equal(TrendyolHttpClient.MaxPackageStreamPages, handler.Requests);
    }

    [Fact]
    public async Task ThePackageStreamScanStopsAtTheMatchingPackageEvenWithoutCargoOrInvoiceFields()
    {
        // T-3: a cancelled package carries no cargo provider, tracking number or invoice fields. Its match ends the
        // scan; the empty fields stay unknown instead of turning the package into "not found" after more pages.
        var handler = new PagedStream(_ => """{"content":[{"id":"pkg-1","orderNumber":"ORD-1","status":"Cancelled","lastModifiedDate":1760000000000,"lines":[]}],"hasMore":true,"nextCursor":"cursor-2"}""");
        var end = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var scan = await Client(handler).ScanPackageStreamAsync(Account(), "pkg-1", null, null, end.AddDays(-12), end, "TR", CancellationToken.None);

        Assert.Null(scan.StreamError);
        Assert.True(scan.Result!.IsSuccess);
        var package = scan.Result.Value!.Package;
        Assert.Equal("pkg-1", package.ExternalPackageId);
        Assert.Equal("ORD-1", scan.Result.Value.ExternalOrderId);
        Assert.Null(package.CargoProviderExternalId);
        Assert.Null(package.CargoTrackingNumber);
        Assert.False(TrendyolJsonMapper.HasInvoiceObservation(package.Invoice));
        Assert.Equal(MarketplaceInvoiceStatus.Unknown, MarketplaceInvoiceStatePolicy.FromRemote(
            package.Invoice?.RawStatus, package.RawStatus, package.Invoice?.InvoiceNumber, package.Invoice?.InvoiceUrl));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task TheOrderStreamLookupStopsAfterItsPageCap()
    {
        var handler = new EndlessStream();
        var end = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var scan = await Client(handler).ScanOrderStreamAsync(Account(), "ORD-MISSING", end.AddDays(-12), end, "TR", null, CancellationToken.None);

        Assert.Equal(TrendyolHttpClient.OrderScanIncompleteCode, scan.Result?.Error?.Code);
        Assert.Equal(AdapterErrorClass.TransientNetwork, scan.Result!.Error!.Class);
        Assert.Equal(TrendyolHttpClient.MaxOrderStreamPages, handler.Requests);
    }

    [Fact]
    public async Task TheOrderStreamLookupStopsWhenTheStreamRepeatsACursor()
    {
        var handler = new PagedStream(_ => """{"content":[],"hasMore":true,"nextCursor":"same"}""");
        var end = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var scan = await Client(handler).ScanOrderStreamAsync(Account(), "ORD-1", end.AddDays(-12), end, "TR", null, CancellationToken.None);

        Assert.Equal(TrendyolHttpClient.OrderScanIncompleteCode, scan.Result?.Error?.Code);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task TheOrderStreamLookupKeepsReadingForTheOrdersOtherPackages()
    {
        var handler = new PagedStream(page => page == 1
            ? """{"content":[{"id":"pkg-1","orderNumber":"ORD-1","status":"Created","lastModifiedDate":1760000000000,"lines":[]}],"hasMore":true,"nextCursor":"cursor-2"}"""
            : """{"content":[{"id":"pkg-2","orderNumber":"ORD-1","status":"Created","lastModifiedDate":1760000001000,"lines":[]}],"hasMore":false}""");
        var end = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var scan = await Client(handler).ScanOrderStreamAsync(Account(), "ORD-1", end.AddDays(-12), end, "TR", null, CancellationToken.None);

        Assert.True(scan.Result!.IsSuccess);
        Assert.Equal(["pkg-1", "pkg-2"], scan.Result.Value!.Packages.Select(package => package.ExternalPackageId).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, handler.Requests);
    }

    private static TrendyolHttpClient Client(HttpMessageHandler handler) =>
        new(new Factory(handler), null!, new ConfigurationBuilder().Build(), Options.Create(new TrendyolOptions()), TimeProvider.System, NullLogger<TrendyolHttpClient>.Instance);

    private static TrendyolRequestContext Account() => new(
        new PlatformConnection { PlatformCode = "TRENDYOL", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Trendyol", ExternalStoreId = "seller-1", ApiVersion = "V2" },
        new Uri("https://apigw.example/"), "key", "secret", "seller-1 - SelfIntegration", false);

    [Fact]
    public void EachClaimBucketKeepsItsOwnPage()
    {
        var start = TrendyolHttpClient.ClaimBucketPages(null);
        Assert.Equal(TrendyolHttpClient.ClaimStatuses.Length, start.Count);
        Assert.All(start, bucket => Assert.Equal(0, bucket.Page));

        var cursor = TrendyolHttpClient.ClaimBucketCursor([("Created", 2), ("Accepted", 5)]);
        Assert.Equal("s:Created=2,Accepted=5", cursor);
        Assert.Equal([("Created", 2), ("Accepted", 5)], TrendyolHttpClient.ClaimBucketPages(cursor));
        Assert.Null(TrendyolHttpClient.ClaimBucketCursor([]));
    }

    [Fact]
    public void APlainPageNumberStillReadsEveryBucketAtThatPage()
    {
        var pages = TrendyolHttpClient.ClaimBucketPages("3");

        Assert.Equal(TrendyolHttpClient.ClaimStatuses, pages.Select(bucket => bucket.Status));
        Assert.All(pages, bucket => Assert.Equal(3, bucket.Page));
    }

    [Fact]
    public void AClaimBucketEndpointNamesItsStatusAndPage() =>
        Assert.EndsWith("?size=200&page=4&startDate=1760000000000&claimItemStatus=Accepted",
            TrendyolHttpClient.ClaimsEndpoint("seller-1", 500, 4, new ReturnPollWindow(DateTimeOffset.FromUnixTimeMilliseconds(1760000000000), null), "Accepted"));

    private sealed class EndlessStream : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var page = Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"content":[],"hasMore":true,"nextCursor":"cursor-{{page}}"}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class PagedStream(Func<int, string> body) : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var page = Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body(page), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
