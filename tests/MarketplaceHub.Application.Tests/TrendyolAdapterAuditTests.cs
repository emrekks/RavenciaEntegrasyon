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

    // 11738971444 as Trendyol reads it: the package's own totals are taken as they are.
    [Fact]
    public void PackageAmountsAreThePackagesOwnTotals()
    {
        const string json = """
        {"content":[{"id":"4268578123","orderNumber":"11738971444","orderDate":1760000000000,"currencyCode":"TRY","status":"Delivered","lastModifiedDate":1760000000000,
          "grossAmount":1598.00,"totalDiscount":80.98,"totalTyDiscount":0.00,"totalPrice":1517.02,"lines":[
          {"lineId":"1","stockCode":"SKU-1","productName":"Bluz","quantity":1,"amount":799.00,"discount":40.49,"price":758.51,"vatRate":10},
          {"lineId":"2","stockCode":"SKU-2","productName":"Bluz","quantity":1,"amount":799.00,"discount":40.49,"price":758.51,"vatRate":10}
        ]}]}
        """;

        var order = Assert.Single(TrendyolJsonMapper.Orders(json).Items);

        Assert.Equal((1598m, 80.98m, 1517.02m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
        var package = Assert.Single(order.Packages);
        Assert.Equal((1598m, 80.98m, 1517.02m), (package.GrossAmount, package.DiscountAmount, package.NetAmount));
    }

    // A package that leaves its totals out takes the sum of its lines (per-unit amount, discount and price times the
    // quantity); a net still unknown is the sale less the discount, never 0.
    [Fact]
    public void PackageAmountsTheReadLeavesOutAreTheSumOfTheLines()
    {
        const string json = """
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"lines":[
          {"lineId":"1","stockCode":"SKU-1","productName":"Bluz","quantity":2,"amount":775.00,"discount":20.00,"price":755.00,"vatRate":10},
          {"lineId":"2","stockCode":"SKU-2","productName":"Bluz","quantity":1,"amount":799.00,"discount":40.49,"price":758.51,"vatRate":10}
        ]}]}
        """;

        var order = Assert.Single(TrendyolJsonMapper.Orders(json).Items);

        Assert.Equal((2349m, 80.49m, 2268.51m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
    }

    // A line without Trendyol's discount field: the package discount is not made up from the other lines' discounts.
    [Fact]
    public void PackageDiscountIsNotSummedWhenALineDoesNotStateIt()
    {
        const string json = """
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"grossAmount":1598.00,"lines":[
          {"lineId":"1","stockCode":"SKU-1","productName":"Bluz","quantity":1,"amount":799.00,"discount":40.49,"price":758.51,"vatRate":10},
          {"lineId":"2","stockCode":"SKU-2","productName":"Bluz","quantity":1,"amount":799.00,"price":758.51,"vatRate":10}
        ]}]}
        """;

        var order = Assert.Single(TrendyolJsonMapper.Orders(json).Items);

        Assert.Equal((1598m, 0m, 1598m), (order.GrossAmount, order.DiscountAmount, order.NetAmount));
    }

    [Fact]
    public void ALineWithoutAVatRateNeverTakesTheVatBaseAmountAsTheRate()
    {
        const string json = """
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"lines":[
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
        var handler = new PagedStream(_ => """{"content":[{"id":"pkg-1","orderNumber":"ORD-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Cancelled","lastModifiedDate":1760000000000,"lines":[]}],"hasMore":true,"nextCursor":"cursor-2"}""");
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
            ? """{"content":[{"id":"pkg-1","orderNumber":"ORD-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"lines":[]}],"hasMore":true,"nextCursor":"cursor-2"}"""
            : """{"content":[{"id":"pkg-2","orderNumber":"ORD-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000001000,"lines":[]}],"hasMore":false}""");
        var end = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var scan = await Client(handler).ScanOrderStreamAsync(Account(), "ORD-1", end.AddDays(-12), end, "TR", null, CancellationToken.None);

        Assert.True(scan.Result!.IsSuccess);
        Assert.Equal(["pkg-1", "pkg-2"], scan.Result.Value!.Packages.Select(package => package.ExternalPackageId).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, handler.Requests);
    }

    // O6: a page Trendyol refuses after the first one leaves the order half read; the packages read so far are no order.
    [Fact]
    public async Task TheOrderStreamLookupRefusedAfterTheFirstPageIsAnIncompleteScanNotASuccess()
    {
        var handler = new StatusStream(page => page == 1
            ? (HttpStatusCode.OK, """{"content":[{"id":"pkg-1","orderNumber":"ORD-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"lines":[]}],"hasMore":true,"nextCursor":"cursor-2"}""")
            : (HttpStatusCode.BadRequest, """{"errors":[{"message":"cursor expired"}]}"""));
        var end = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var scan = await Client(handler).ScanOrderStreamAsync(Account(), "ORD-1", end.AddDays(-12), end, "TR", null, CancellationToken.None);

        Assert.False(scan.Result!.IsSuccess);
        Assert.Equal(TrendyolHttpClient.OrderScanIncompleteCode, scan.Result.Error!.Code);
        Assert.Equal(AdapterErrorClass.TransientNetwork, scan.Result.Error.Class);
        Assert.Equal(2, handler.Requests);
    }

    // A refusal of the very first page still means the stream does not serve the window: "not found", not an error.
    [Fact]
    public async Task TheOrderStreamLookupRefusedOnTheFirstPageEndsWithoutAResult()
    {
        var handler = new StatusStream(_ => (HttpStatusCode.NotFound, "{}"));
        var end = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var scan = await Client(handler).ScanOrderStreamAsync(Account(), "ORD-1", end.AddDays(-12), end, "TR", null, CancellationToken.None);

        Assert.Null(scan.Result);
        Assert.Equal(1, handler.Requests);
    }

    // O6/D17: a cancelled read never hands back the packages it had read as the order.
    [Fact]
    public async Task ACancelledOrderStreamLookupDoesNotReturnTheFirstPagesPackagesAsTheOrder()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StatusStream(_ =>
        {
            cancellation.Cancel();
            return (HttpStatusCode.OK, """{"content":[{"id":"pkg-1","orderNumber":"ORD-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"lines":[]}],"hasMore":true,"nextCursor":"cursor-2"}""");
        });
        var end = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(handler).ScanOrderStreamAsync(Account(), "ORD-1", end.AddDays(-12), end, "TR", null, cancellation.Token));
    }

    // O9: a claim audit entry whose date is null is left out, not a crash of the whole read.
    [Fact]
    public void AClaimAuditEntryWithANullDateIsLeftOut()
    {
        const string json = """
        [{"newStatus":"Created","date":null},{"newStatus":"Accepted","date":"1761500000000"},{"newStatus":"Unresolved","date":1761500000000}]
        """;

        var audit = Assert.Single(TrendyolJsonMapper.ReturnLineAudits(json));

        Assert.Equal("Unresolved", audit.NewStatus);
    }

    // O9: a null invoiceDateTime is left out of the invoice-link body instead of throwing.
    [Fact]
    public void AnInvoiceLinkPayloadWithANullInvoiceDateLeavesTheDateOut()
    {
        using var payload = System.Text.Json.JsonDocument.Parse("""{"shipmentPackageId":"123","invoiceLink":"https://example.test/i.pdf","invoiceDateTime":null}""");

        var body = TrendyolHttpClient.InvoiceLinkBody(payload.RootElement);

        Assert.NotNull(body);
        Assert.False(body.ContainsKey("invoiceDateTime"));
        Assert.Equal(123L, body["shipmentPackageId"]);
    }

    // O5: a text amount with a decimal comma stays unknown instead of becoming a hundred times itself.
    [Fact]
    public void ACommaTextAmountIsNotReadAsAHundredTimesItself()
    {
        const string json = """
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"grossAmount":"1234,50","totalPrice":"1234.50","lines":[
          {"lineId":"1","stockCode":"SKU-1","productName":"Bluz","quantity":1,"amount":"1234.50","price":"1234.50","vatRate":10}
        ]}]}
        """;

        var order = Assert.Single(TrendyolJsonMapper.Orders(json).Items);

        Assert.Equal(1234.50m, order.GrossAmount);
        Assert.Equal(1234.50m, order.NetAmount);
    }

    // D12: a text deadline with an offset is stored as its UTC instant (Npgsql timestamptz takes offset 0 only).
    [Fact]
    public void ATextClaimDateWithAnOffsetIsReadAsUtc()
    {
        const string json = """
        {"page":0,"totalPages":1,"content":[{"id":"c1","claimId":"c1","orderNumber":"o1","orderDate":1760000000000,"currencyCode":"TRY","lastModifiedDate":1760000000000,"autoApproveDate":"2026-11-01T10:00:00+03:00","items":[]}]}
        """;

        var claim = Assert.Single(TrendyolJsonMapper.Returns(json).Items);

        Assert.Equal(TimeSpan.Zero, claim.ActionDueAt!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero), claim.ActionDueAt);
    }

    private sealed class StatusStream(Func<int, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        private int requests;
        public int Requests => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(Interlocked.Increment(ref requests));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
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
