using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Deep audit 20 Oct 2026, Trendyol contract items (C2, C3, C10-C15, C28, G10, G30): the exact requests the adapter sends.
public sealed class TrendyolWriteContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ConnectionId = Guid.CreateVersion7();

    [Theory]
    [InlineData("DE", "DE")]
    [InlineData("az", "AZ")]
    [InlineData(null, "TR")]
    [InlineData(" ", "TR")]
    public async Task APackageWriteGoesToThePackagesStoredStorefront(string? stored, string expected)
    {
        var handler = new Script(new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Client(handler).ExecutePackageActionAsync(Account(), Context(), new PackageActionCommand("4052072376", "TRACKING_NUMBER", """{"cargoSenderNumber":"7330029000000","providerCode":"YKMP"}""", stored), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://apigw.example.test/integration/order/sellers/seller-1/shipment-packages/4052072376/tracking-details", request.Uri.AbsoluteUri);
        Assert.Equal(expected, request.StoreFront);
    }

    [Fact]
    public async Task CancelItemsSendsOnlyTheValidatedLinesAndReason()
    {
        var handler = new Script(new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Client(handler).ExecutePackageActionAsync(Account(), Context(),
            new PackageActionCommand("4052072376", "CANCEL_ITEMS", """{"lines":[{"lineId":"11","quantity":1,"extra":true},{"lineId":12,"quantity":2}],"reasonId":500,"note":"x"}""", "SA"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.EndsWith("/shipment-packages/4052072376/items/unsupplied", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("""{"lines":[{"lineId":11,"quantity":1},{"lineId":12,"quantity":2}],"reasonId":500}""", request.Body);
        Assert.Equal("SA", request.StoreFront);
    }

    [Theory]
    [InlineData("""{"lines":[{"lineId":11,"quantity":1}],"reasonId":499}""")]
    [InlineData("""{"lines":[{"lineId":11,"quantity":1}],"reasonId":507}""")]
    [InlineData("""{"lines":[{"lineId":11,"quantity":1}]}""")]
    [InlineData("""{"lines":[],"reasonId":500}""")]
    [InlineData("""{"lines":[{"lineId":11,"quantity":0}],"reasonId":500}""")]
    [InlineData("""{"lines":[{"lineId":"x","quantity":1}],"reasonId":500}""")]
    [InlineData("""{"lines":[{"lineId":11,"quantity":1},{"lineId":11,"quantity":1}],"reasonId":500}""")]
    [InlineData("""{"lines":[{"quantity":1}],"reasonId":500}""")]
    [InlineData("not json")]
    public async Task AnInvalidCancelItemsBodyIsNeverSent(string payload)
    {
        var handler = new Script();

        var result = await Client(handler).ExecutePackageActionAsync(Account(), Context(), new PackageActionCommand("4052072376", "CANCEL_ITEMS", payload), CancellationToken.None);

        Assert.Equal("TRENDYOL_CANCEL_ITEMS_INVALID", result.Error?.Code);
        Assert.Equal(AdapterErrorClass.Validation, result.Error!.Class);
        Assert.Empty(handler.Requests);
        Assert.False(TrendyolPackageActionBodies.TryBuildUnsupplied(payload, out _));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(506)]
    public void EveryDocumentedUnsuppliedReasonIsAccepted(int reasonId) =>
        Assert.True(TrendyolPackageActionBodies.TryBuildUnsupplied($$"""{"lines":[{"lineId":11,"quantity":1}],"reasonId":{{reasonId}}}""", out _));

    [Fact]
    public async Task AnInvoiceLinkGoesToThePackagesStoredStorefront()
    {
        var handler = new Script(new HttpResponseMessage(HttpStatusCode.Created));

        var result = await Client(handler).DeliverAsync(Account(), Context(explicitInvoiceDelivery: true),
            new InvoiceDeliveryCommand("4052072376", "LINK", """{"invoiceLink":"https://panel.example.test/invoice.pdf","shipmentPackageId":4052072376}""", "hash", "AE"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://apigw.example.test/integration/sellers/seller-1/seller-invoice-links", request.Uri.AbsoluteUri);
        Assert.Equal("AE", request.StoreFront);
    }

    [Fact]
    public async Task ClaimEvidenceGoesInTheFilesPart()
    {
        var handler = new Script(new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Client(handler).ExecuteReturnActionAsync(Account(), Context(),
            new ReturnActionCommand("claim-1", ["line-1"], "REJECT", "251", "Ürün kullanılmış", [new("kanit.jpg", "image/jpeg", [1, 2, 3])]),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("multipart/form-data", request.ContentType);
        Assert.Contains("name=files; filename=kanit.jpg", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=file;", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimEvidenceOverFiveMegabytesIsRefusedLocally()
    {
        var handler = new Script();

        var result = await Client(handler).ExecuteReturnActionAsync(Account(), Context(),
            new ReturnActionCommand("claim-1", ["line-1"], "REJECT", "251", "Ürün kullanılmış", [new("kanit.pdf", "application/pdf", new byte[TrendyolHttpClient.MaxClaimEvidenceBytes + 1])]),
            CancellationToken.None);

        Assert.Equal("TRENDYOL_RETURN_EVIDENCE_TOO_LARGE", result.Error?.Code);
        Assert.Empty(handler.Requests);
        Assert.Equal(5_000_000, TrendyolHttpClient.MaxClaimEvidenceBytes);
    }

    [Theory]
    [InlineData("123456 - SelfIntegration", "123456", true, true)]
    [InlineData("123456 - Ravencia", "123456", true, true)]
    [InlineData("654321 - Ravencia", "123456", true, false)]
    [InlineData("Ravencia - Entegrasyon", "123456", true, false)]
    [InlineData("123456 - Ravencia Entegrasyon", "123456", true, false)]
    [InlineData("123456 - ABCDEFGHIJABCDEFGHIJABCDEFGHIJK", "123456", true, false)]
    [InlineData("123456 - Ravencİa", "123456", false, false)]
    [InlineData("123456 - Ravencia\r\nX: 1", "123456", false, false)]
    public void TheUserAgentIsCheckedForHeaderSafetyAndTheDocumentedFormat(string identity, string sellerId, bool headerSafe, bool documented)
    {
        Assert.Equal(headerSafe, TrendyolUserAgentPolicy.IsHeaderSafe(identity));
        Assert.Equal(documented, TrendyolUserAgentPolicy.MatchesDocumentedFormat(identity, sellerId));
    }

    [Fact]
    public void TheUserAgentIsSentAsWrittenWithoutTheTypedParser()
    {
        using var request = TrendyolAuthenticationHandler.Create(Account() with { UserAgentIdentity = "123456 - Ravencia" }, HttpMethod.Get, "order/sellers/123456/orders/stream");

        Assert.True(request.Headers.NonValidated.TryGetValues("User-Agent", out var values));
        Assert.Equal("123456 - Ravencia", values.ToString());
        // The quota bucket still keys on the identity although the typed User-Agent collection was bypassed.
        Assert.StartsWith("global:apigw.example.test:123456 - Ravencia:", TrendyolResilienceHandler.GlobalRateBucketFor(request), StringComparison.Ordinal);
    }

    [Fact]
    public void AContentIdReadKeepsOnlyThatContent()
    {
        var page = new AdapterPageResult<RemoteCatalogProduct>([Product("800001"), Product("800002")], "p:1", true);

        var filtered = TrendyolHttpClient.FilterByContentId(page, " 800002 ");

        Assert.Equal(["800002"], filtered.Items.Select(item => item.ExternalProductId).ToArray());
        Assert.Equal("p:1", filtered.NextCursor);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(12.7, 12)]
    [InlineData(20000, 20000)]
    [InlineData(25000, 20000)]
    public void TrendyolStockIsCappedAtTwentyThousand(decimal publishable, int expected) =>
        Assert.Equal(expected, TrendyolStockRules.Quantity(publishable));

    [Fact]
    public void OnlyDocumentedStorefrontsAndUndocumentedOnesWithOrdersAreRead()
    {
        Assert.Equal(["TR", "AE", "SA", "GR", "DE", "BG", "KW", "SK", "RO", "CZ"], TrendyolReadStorefronts.For([]));
        Assert.Equal(["TR", "AE", "SA", "GR", "DE", "BG", "KW", "AZ", "SK", "RO", "CZ"], TrendyolReadStorefronts.For(["az", null, "TR"]));
        Assert.Contains("QA", TrendyolReadStorefronts.For(["QA"]));
        Assert.DoesNotContain("XX", TrendyolReadStorefronts.For(["XX"]));
    }

    [Fact]
    public void ALineWithoutAVatRateTakesThePackagesRateAndIsKept()
    {
        const string json = """
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Created","lastModifiedDate":1760000000000,"lines":[
          {"lineId":"line-1","stockCode":"SKU-1","productName":"Ürün","quantity":1,"lineItemPrice":120,"vatBaseAmount":100},
          {"lineId":"line-2","stockCode":"SKU-2","productName":"Ürün 2","quantity":1,"lineItemPrice":120,"vatRate":20,"vatBaseAmount":100}
        ]}]}
        """;

        var page = TrendyolJsonMapper.Orders(json);

        var lines = Assert.Single(page.Items).Lines;
        Assert.Equal(["line-1", "line-2"], lines.Select(line => line.ExternalLineId).ToArray());
        Assert.Equal([20m, 20m], lines.Select(line => line.VatRate).ToArray());
        var issue = Assert.Single(page.Issues!);
        Assert.Equal("ORDER_PACKAGE_LINE_VAT_RATE_INFERRED", issue.Code);
        Assert.Equal("pkg-1:line-1", issue.Identity);
        Assert.Equal(["line-1", "line-2"], Assert.Single(Assert.Single(page.Items).Packages).Allocations.Select(allocation => allocation.ExternalLineId).ToArray());
    }

    [Fact]
    public void ALineWithoutAVatRateAndNoAgreeingRateIsKeptAndReported()
    {
        const string json = """
        {"content":[{"id":"pkg-1","orderNumber":"ord-1","status":"Created","lastModifiedDate":1760000000000,"lines":[
          {"lineId":"line-1","stockCode":"SKU-1","productName":"Ürün","quantity":1,"lineItemPrice":120},
          {"lineId":"line-2","stockCode":"SKU-2","productName":"Ürün 2","quantity":1,"lineItemPrice":120,"vatRate":20},
          {"lineId":"line-3","stockCode":"SKU-3","productName":"Ürün 3","quantity":1,"lineItemPrice":110,"vatRate":10}
        ]}]}
        """;

        var page = TrendyolJsonMapper.Orders(json);

        var lines = Assert.Single(page.Items).Lines;
        Assert.Equal(3, lines.Count);
        Assert.Equal(0m, lines[0].VatRate);
        var issue = Assert.Single(page.Issues!);
        Assert.Equal("ORDER_PACKAGE_LINE_VAT_RATE_MISSING", issue.Code);
        Assert.Equal("pkg-1:line-1", issue.Identity);
        // The kept line's raw JSON marks its rate as unknown, so no invoice is issued at 0% (or at the inferred rate).
        Assert.True(InvoiceIssuePolicy.IsLineVatRateUnknown("TRENDYOL", lines[0].SourceSnapshotJson));
        Assert.False(InvoiceIssuePolicy.IsLineVatRateUnknown("TRENDYOL", lines[1].SourceSnapshotJson));
    }

    [Fact]
    public void ATrendyolLineWithoutAReportedVatRateCountsAsUnknown()
    {
        Assert.True(InvoiceIssuePolicy.IsLineVatRateUnknown("TRENDYOL", """{"lineId":"1","vatBaseAmount":100}"""));
        Assert.True(InvoiceIssuePolicy.IsLineVatRateUnknown("TRENDYOL", """{"lineId":"1","vatRate":null}"""));
        Assert.False(InvoiceIssuePolicy.IsLineVatRateUnknown("TRENDYOL", """{"lineId":"1","vatRate":0}"""));
        Assert.False(InvoiceIssuePolicy.IsLineVatRateUnknown("TRENDYOL", """{"lineId":"1","vatRate":"10"}"""));
        // Lines stored without a raw snapshot keep their rate; Shopify reports no line rate at all.
        Assert.False(InvoiceIssuePolicy.IsLineVatRateUnknown("TRENDYOL", null));
        Assert.False(InvoiceIssuePolicy.IsLineVatRateUnknown("TRENDYOL", "{}"));
        Assert.False(InvoiceIssuePolicy.IsLineVatRateUnknown("SHOPIFY", """{"id":"1"}"""));
    }

    [Fact]
    public void TrendyolQuestionsAreReadOnlyByDocumentedStatuses() =>
        Assert.Equal(["WAITING_FOR_ANSWER", "ANSWERED", "REPORTED", "REJECTED", "WAITING_FOR_APPROVE"], MarketplaceHub.Infrastructure.Persistence.MarketplaceQuestionService.TrendyolStatuses);

    private static RemoteCatalogProduct Product(string contentId) => new(contentId, null, "Ürün", "", null, null, null, null, [], [], "{}");

    private static TrendyolHttpClient Client(Script handler) => new(
        new Factory(handler),
        null!,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true", ["FeatureFlags:InvoiceMarketplaceDeliveryWrites"] = "true" }).Build(),
        Options.Create(new TrendyolOptions()),
        new FixedClock(Now),
        NullLogger<TrendyolHttpClient>.Instance);

    private static TrendyolRequestContext Account() => new(
        new PlatformConnection { Id = ConnectionId, PlatformCode = "TRENDYOL", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Trendyol", ExternalStoreId = "seller-1", ApiVersion = "V2" },
        new Uri("https://apigw.example.test/integration/"), "key", "secret", "seller-1 - SelfIntegration", true);

    private static AdapterContext Context(bool explicitInvoiceDelivery = false) =>
        new(Guid.CreateVersion7(), ConnectionId, "trendyol-contract-test", "trendyol-contract-test", Now.AddMinutes(2), IsExplicitManualInvoiceDelivery: explicitInvoiceDelivery);

    private sealed record Recorded(HttpMethod Method, Uri Uri, string? ContentType, string? Body, string? StoreFront);

    private sealed class Script(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int next;
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType, body,
                request.Headers.TryGetValues("storeFrontCode", out var storeFront) ? storeFront.Single() : null));
            if (next >= responses.Length) throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
            return responses[next++];
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
