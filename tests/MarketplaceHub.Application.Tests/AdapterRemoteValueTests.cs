using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Values the marketplaces send that the adapters must not misread: null where a number was expected, numbers written
// with a decimal comma, missing dates, faults inside HTTP 500 and errors after the first one in a GraphQL answer.
public sealed class AdapterRemoteValueTests
{
    // O5: dot-decimal text and JSON numbers are read; a comma is never taken as a thousands separator.
    [Theory]
    [InlineData("1234.50", "1234.50")]
    [InlineData(" -12.5 ", "-12.5")]
    [InlineData("1E2", "100")]
    [InlineData("1234,50", null)]
    [InlineData("1,000", null)]
    [InlineData("1.234,50", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DotDecimalTextIsReadAndCommaTextStaysUnknown(string? text, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), RemoteJson.ParseDecimal(text));

    // O8/O9/D16: TryGetInt32 and friends throw on null; the helpers answer unknown instead.
    [Fact]
    public void ANullOrTextValueIsAnUnknownNumberNotAnException()
    {
        using var json = JsonDocument.Parse("""{"n":null,"s":"12","o":{},"i":7,"d":1.5,"big":1e40}""");
        var root = json.RootElement;

        Assert.Null(RemoteJson.Int32(root, "n"));
        Assert.Null(RemoteJson.Int32(root, "s"));
        Assert.Null(RemoteJson.Int64(root, "o"));
        Assert.Null(RemoteJson.Double(root, "n"));
        Assert.Null(RemoteJson.NumberDecimal(root, "s"));
        Assert.Null(RemoteJson.Decimal(root, "big"));
        Assert.Null(RemoteJson.Int32(root, "missing"));
        Assert.Equal(7, RemoteJson.Int32(root, "i"));
        Assert.Equal(12m, RemoteJson.Decimal(root, "s"));
        Assert.Equal(1.5m, RemoteJson.Decimal(root, "d"));
        Assert.Null(RemoteJson.EpochMilliseconds(JsonDocument.Parse("9223372036854775807").RootElement));
    }

    // Hepsiburada's catalog documents money with a decimal comma: that exact shape is read, nothing looser.
    [Theory]
    [InlineData("130,50", "130.50")]
    [InlineData("1.234,50", "1234.50")]
    [InlineData("-5,5", "-5.5")]
    [InlineData("1,234", null)]
    [InlineData("12.34", null)]
    [InlineData("1.23,45", null)]
    public void HepsiburadaTurkishMoneyIsReadOnlyInItsExactShape(string text, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), HepsiburadaJsonMapper.TurkishMoney(text));

    // O8: a null gibStatusCode is unknown, not an exception that turns the status read into a contract error.
    [Fact]
    public void AnInvoiceStatusWithANullGibCodeIsRead()
    {
        var status = TrendyolEFaturamJsonMapper.InvoiceStatus("""{"status":"40","invoiceUuid":"u-1","gibStatusCode":null}""", "u-1");

        Assert.Null(status.GibStatusCode);
        Assert.Equal("40", status.RawStatus);
        Assert.Equal(1300, TrendyolEFaturamJsonMapper.InvoiceStatus("""{"status":"40","gibStatusCode":"1300"}""", "u-1").GibStatusCode);
    }

    // O9: a paged status answer is read at the invoice asked for, never at whatever came first.
    [Fact]
    public void APagedInvoiceStatusIsReadAtTheItemWithTheRequestedUuid()
    {
        const string json = """{"content":[{"status":"20","invoiceUuid":"other"},{"status":"40","invoiceUuid":"AAA-1"}]}""";

        var status = TrendyolEFaturamJsonMapper.InvoiceStatus(json, "ref-1", "aaa-1");

        Assert.Equal("40", status.RawStatus);
        Assert.Equal("AAA-1", status.EttnUuid);
        Assert.Throws<JsonException>(() => TrendyolEFaturamJsonMapper.InvoiceStatus("""{"content":[{"status":"20","invoiceUuid":"other"}]}""", "ref-1", "aaa-1"));
        Assert.Throws<JsonException>(() => TrendyolEFaturamJsonMapper.InvoiceStatus("""{"content":[]}""", "ref-1", "aaa-1"));
        Assert.Equal("20", TrendyolEFaturamJsonMapper.InvoiceStatus("""{"content":[{"status":"20"}]}""", "ref-1", "aaa-1").RawStatus);
    }

    // O9: Hepsiburada's finance count may be null; the page's own size stands in.
    [Fact]
    public void AFinancePageWithANullCountCountsItsItems()
    {
        using var json = JsonDocument.Parse("""{"count":null,"items":[{"id":"1","amount":{"value":10,"currencyCode":"949"},"dueDate":"2026-11-10T00:00:00"}]}""");

        var page = HepsiburadaFinanceMapper.Page(json.RootElement);

        Assert.Equal(1, page.Count);
    }

    // D13: a question without any date is not stored as asked in 1970; one with only its last change is dated by it.
    [Fact]
    public void AHepsiburadaQuestionWithoutAnyDateIsLeftForALaterRead()
    {
        using var document = JsonDocument.Parse("""
            {"content":[{"issueNumber":"Q-1","status":"WaitingForAnswer","lastContent":"Var mı?"},
                        {"issueNumber":"Q-2","status":"WaitingForAnswer","lastContent":"Ne zaman?","lastModifiedAt":"2026-11-01T10:00:00+03:00"}]}
            """);

        var page = HepsiburadaQuestionMapper.Page(document, new("PRODUCT", null, null, null, 1, 25));

        var question = Assert.Single(page.Items);
        Assert.Equal("Q-2", question.Id);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero), question.CreatedAt);
    }

    // D13: an n11 package with no date at all is not imported as 1970.
    [Fact]
    public void AnN11PackageWithoutAnyDateIsNotImported()
    {
        const string page = """
            {"page":0,"totalPages":1,"content":[{"orderNumber":"203872347999","id":"112999455244999",
              "lines":[{"orderLineId":"L1","quantity":1,"productName":"Bluz","stockCode":"BLZ-1","price":100,"sellerInvoiceAmount":100}],
              "shipmentPackageStatus":"Created"}]}
            """;

        var result = N11OrderMapper.Packages(page);

        Assert.Empty(result.Items);
        Assert.Equal("ORDER_PACKAGE_INVALID", Assert.Single(result.Issues!).Code);
    }

    // One rule for every marketplace (5 Nov 2026, replacing O12's refusal): the package is kept for shipping with its
    // readable line, counts the unreadable one, and the line is never guessed.
    [Fact]
    public void AnN11PackageWithOneUnreadableLineIsKeptWithTheLineCounted()
    {
        const string page = """
            {"page":0,"totalPages":1,"content":[{"orderNumber":"203872347999","id":"112999455244999","packageHistories":[{"createdDate":1724274054460,"status":"Created"}],"lastModifiedDate":1724323386203,
              "lines":[{"orderLineId":"L1","quantity":1,"productName":"Bluz","stockCode":"BLZ-1","price":100,"sellerInvoiceAmount":100},
                       {"orderLineId":"L2","quantity":1,"productName":"Etek","stockCode":"ETK-1","price":null}],
              "shipmentPackageStatus":"Created"}]}
            """;

        var result = N11OrderMapper.Packages(page);

        var order = Assert.Single(result.Items);
        Assert.Equal("L1", Assert.Single(order.Lines).ExternalLineId);
        var package = Assert.Single(order.Packages);
        Assert.Equal(1, package.UnreadableLineCount);
        Assert.Equal("L1", Assert.Single(package.Allocations).ExternalLineId);
        // No n11 total in this read: the amount is the read line only and is said to be possibly incomplete.
        Assert.Equal(100m, package.GrossAmount);
        Assert.Equal(
            new[] { ("ORDER_PACKAGE_LINE_INVALID", "112999455244999:L2"), ("ORDER_AMOUNT_MAY_BE_INCOMPLETE", "112999455244999") },
            result.Issues!.Select(issue => (issue.Code, issue.Identity)).ToArray());

        // With n11's own package totals the order takes them (totalAmount, totalDiscountAmount), not the read lines' sum.
        var withTotals = N11OrderMapper.Packages(page.Replace("\"lastModifiedDate\":1724323386203,", "\"lastModifiedDate\":1724323386203,\"totalAmount\":250,\"totalDiscountAmount\":20,", StringComparison.Ordinal));
        var totalled = Assert.Single(Assert.Single(withTotals.Items).Packages);
        Assert.Equal((250m, 20m, 230m), (totalled.GrossAmount, totalled.DiscountAmount, totalled.NetAmount));
        Assert.Equal((250m, 230m), (withTotals.Items[0].GrossAmount, withTotals.Items[0].NetAmount));
        Assert.Equal("ORDER_PACKAGE_LINE_INVALID", Assert.Single(withTotals.Issues!).Code);
    }

    // O5: an n11 settlement amount that is there but unreadable is a page error, not 0 or a hundred times itself.
    [Fact]
    public void AnUnreadableN11SettlementAmountIsAPageErrorAndAMissingOneIsZero()
    {
        var line = XElement.Parse("<orderItemSettlement><orderItemId>1</orderItemId><commission>12.50</commission><adFee>1234,50</adFee></orderItemSettlement>");

        Assert.Equal(12.50m, N11SettlementMapper.Number(line, "commission"));
        Assert.Equal(0m, N11SettlementMapper.Number(line, "serviceFee"));
        Assert.Throws<JsonException>(() => N11SettlementMapper.Number(line, "adFee"));
    }

    // D18: a SOAP fault inside HTTP 500 is read before the status decides.
    [Fact]
    public void AnN11FaultInsideHttp500IsClassifiedByTheFault()
    {
        static string Fault(string code, string reason) =>
            $"""<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><soap:Fault><faultcode>{code}</faultcode><faultstring>{reason}</faultstring></soap:Fault></soap:Body></soap:Envelope>""";

        var rejected = N11HttpClient.SoapHttpError(HttpStatusCode.InternalServerError, Fault("soap:Client", "orderNumber alanı geçersizdir"), "GetSettlementDetail");
        var internalError = N11HttpClient.SoapHttpError(HttpStatusCode.InternalServerError, Fault("soap:Server", "Internal Error"), "GetSettlementDetail");
        var credentials = N11HttpClient.SoapHttpError(HttpStatusCode.InternalServerError, Fault("soap:Server", "Authentication failed: appKey"), "GetSettlementDetail");
        var named = N11HttpClient.SoapHttpError(HttpStatusCode.InternalServerError, Fault("soap:Server", "Fatura linki geçersiz"), "SaveLinkSellerInvoice");

        Assert.Equal((AdapterErrorClass.Validation, "N11_SOAP_FAULT"), (rejected!.Class, rejected.Code));
        Assert.Equal((AdapterErrorClass.Remote5xx, "N11_REMOTE_FAULT"), (internalError!.Class, internalError.Code));
        Assert.Equal((AdapterErrorClass.Authentication, "N11_CREDENTIALS_REJECTED"), (credentials!.Class, credentials.Code));
        Assert.Equal(AdapterErrorClass.Validation, named!.Class);
    }

    [Fact]
    public void AnN11HtmlAnswerIsReadByItsStatus()
    {
        const string html = "<html><body><h1>403 Forbidden</h1></body></html>";

        // A 403 HTML page is the security gateway on every marketplace (a temporary block); a 401 is the credentials.
        Assert.Equal(AdapterErrorClass.RateLimit, N11HttpClient.SoapHttpError(HttpStatusCode.Forbidden, html, "GetSettlementList")!.Class);
        Assert.Equal(AdapterErrorClass.Authentication, N11HttpClient.SoapHttpError(HttpStatusCode.Unauthorized, html, "GetSettlementList")!.Class);
        Assert.Equal(AdapterErrorClass.Remote5xx, N11HttpClient.SoapHttpError(HttpStatusCode.BadGateway, html, "GetSettlementList")!.Class);
        Assert.Null(N11HttpClient.SoapHttpError(HttpStatusCode.OK, "<x/>", "GetSettlementList"));
        // An answer that carries the operation's response is read as a SOAP answer, whatever the status.
        Assert.Null(N11HttpClient.SoapHttpError(HttpStatusCode.InternalServerError, "<GetSettlementListResponse/>", "GetSettlementList"));
    }

    // O13: every entry of a GraphQL errors array is read; a retryable one makes the answer retryable.
    [Fact]
    public void AShopifyInternalErrorAfterAnotherErrorIsRetryable()
    {
        using var json = JsonDocument.Parse("""
            {"errors":[{"message":"Field 'x' is deprecated"},{"message":"Internal error","extensions":{"code":"INTERNAL_SERVER_ERROR","requestId":"r-1"}}]}
            """);

        var error = ShopifyHttpClient.GraphQlError(json.RootElement, 200, "req-1")!;

        Assert.Equal((AdapterErrorClass.Remote5xx, ShopifyHttpClient.GraphQlInternalErrorCode), (error.Class, error.Code));
        Assert.NotNull(error.RetryAfter);
    }

    [Fact]
    public void AShopifyThrottleAnywhereInTheErrorsIsARateLimitAndOtherErrorsStayRejections()
    {
        using var throttled = JsonDocument.Parse("""
            {"errors":[{"message":"x","extensions":{"code":"NOT_FOUND"}},{"message":"Throttled","extensions":{"code":"THROTTLED"}}],
             "extensions":{"cost":{"requestedQueryCost":100,"throttleStatus":{"currentlyAvailable":null,"restoreRate":50}}}}
            """);
        using var rejected = JsonDocument.Parse("""{"errors":[{"message":null},{"message":"Bad input","extensions":{"code":7}}]}""");

        var rateLimit = ShopifyHttpClient.GraphQlError(throttled.RootElement, 200, null)!;
        var rejection = ShopifyHttpClient.GraphQlError(rejected.RootElement, 200, null)!;

        Assert.Equal(AdapterErrorClass.RateLimit, rateLimit.Class);
        Assert.Equal(TimeSpan.FromSeconds(5), rateLimit.RetryAfter);
        Assert.Equal((AdapterErrorClass.Validation, "Bad input"), (rejection.Class, rejection.SafeMessage));
        Assert.Null(ShopifyHttpClient.GraphQlError(JsonDocument.Parse("""{"data":{}}""").RootElement, 200, null));
    }

    // Y3: the catalog bucket is one per egress IP, shared by every Hepsiburada account; the others stay per account.
    [Fact]
    public void TheHepsiburadaCatalogBucketIsSharedAcrossAccounts()
    {
        static HttpRequestMessage Request(string url, string user)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", user);
            return request;
        }
        using var catalogA = Request("https://mpop.hepsiburada.com/product/api/products", "a");
        using var catalogB = Request("https://mpop.hepsiburada.com/product/api/products", "b");
        using var ordersA = Request("https://oms-external.hepsiburada.com/orders", "a");
        using var ordersB = Request("https://oms-external.hepsiburada.com/orders", "b");

        Assert.Equal(HepsiburadaRateLimitHandler.QuotaKey(catalogA, "catalog"), HepsiburadaRateLimitHandler.QuotaKey(catalogB, "catalog"));
        Assert.Equal("hepsiburada:egress:catalog", HepsiburadaRateLimitHandler.QuotaKey(catalogA, "catalog"));
        Assert.NotEqual(HepsiburadaRateLimitHandler.QuotaKey(ordersA, "oms"), HepsiburadaRateLimitHandler.QuotaKey(ordersB, "oms"));
    }

    // D17: without the shared limiter a catalog call waits for its place instead of going out unthrottled.
    [Fact]
    public async Task ACatalogCallWaitsWhenTheSharedLimiterIsDown()
    {
        var now = new DateTimeOffset(3000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, HepsiburadaRateLimitHandler.TakeFallbackCatalogSlot(now));
        Assert.Equal(HepsiburadaRateLimitHandler.CatalogFallbackSpacing, HepsiburadaRateLimitHandler.TakeFallbackCatalogSlot(now));
        Assert.Equal(TimeSpan.FromSeconds(1), HepsiburadaRateLimitHandler.CatalogFallbackSpacing);

        // Wired: a broken limiter does not stop an order call, and the catalog call is still sent (after its wait).
        using var downstream = new Recording();
        using var handler = new HepsiburadaRateLimitHandler(TimeProvider.System, new BrokenLimiter()) { InnerHandler = downstream };
        using var client = new HttpClient(handler);
        using var orders = await client.GetAsync("https://oms-external.hepsiburada.com/orders");
        Assert.Equal(1, downstream.Requests);
    }

    private sealed class BrokenLimiter : ISharedRateLimiter
    {
        public ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database down");
    }

    private sealed class Recording : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
