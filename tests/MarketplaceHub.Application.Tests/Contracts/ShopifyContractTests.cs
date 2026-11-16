using System.Net;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ADAPTOR-DIGER-10: Shopify Admin GraphQL order nodes through the order mapper and error answers (HTTP and GraphQL
// errors) through the client's query path, compared with goldens under Fixtures/Shopify/.
public sealed class ShopifyContractTests
{
    [Fact]
    public void OrderNodesMapAmountsLinesFulfilmentsRefundsAndCancellation()
    {
        using var document = ContractFixture.Json("Shopify/orders-page.json");
        var nodes = document.RootElement.GetProperty("data").GetProperty("orders").GetProperty("nodes").EnumerateArray().ToList();

        var orders = nodes.Select(node => ContractFixture.Outcome(() => ShopifyHttpClient.MapOrder(node, "https://fake-shop.example.test"))).ToList();

        ContractFixture.MatchesGolden("Shopify/orders-page.golden.json", orders);
        var paid = Assert.IsType<RemoteOrder>(orders[0]);
        Assert.Equal(("#1001", "TRY"), (paid.OrderNumber, paid.Currency));
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 9, 30, 0, TimeSpan.Zero), paid.OrderedAt);
        Assert.Equal((300m, 40m, 260m), (paid.GrossAmount, paid.DiscountAmount, paid.NetAmount));
        Assert.Equal((2m, 120m, 20m), (paid.Lines[0].Quantity, paid.Lines[0].UnitPrice, paid.Lines[0].VatRate));
        var refunded = Assert.IsType<RemoteOrder>(orders[1]);
        Assert.Equal(50m, refunded.RefundedAmount);
        var cancelled = Assert.IsType<RemoteOrder>(orders[2]);
        Assert.Equal("CANCELLED", cancelled.CancellationStatus);
        // A line without taxLines leaves its rate unknown, never 0.
        Assert.Null(cancelled.Lines[0].VatRate);
    }

    public static TheoryData<string, CannedHttpCase> ErrorAnswers => new()
    {
        { "429 with Retry-After", new(() => CannedHttp.RateLimited(11), AdapterErrorClass.RateLimit, "SHOPIFY_RATE_LIMITED", 429, TimeSpan.FromSeconds(11)) },
        { "401 token", new(() => new CannedHttp(HttpStatusCode.Unauthorized, """{"errors":"[API] Invalid API key or access token (unrecognized login or wrong password)"}"""), AdapterErrorClass.Authentication, "SHOPIFY_ACCESS_DENIED", 401, null) },
        { "403 HTML gateway page", new(CannedHttp.GatewayBlocked, AdapterErrorClass.RateLimit, "SHOPIFY_GATEWAY_BLOCKED", 403, TimeSpan.FromMinutes(15)) },
        { "400 validation", new(() => new CannedHttp(HttpStatusCode.BadRequest, """{"errors":{"query":"Required parameter missing or invalid"}}"""), AdapterErrorClass.Validation, "SHOPIFY_HTTP_ERROR", 400, null) },
        { "503 server", new(() => new CannedHttp(HttpStatusCode.ServiceUnavailable, ""), AdapterErrorClass.Remote5xx, "SHOPIFY_REMOTE_ERROR", 503, TimeSpan.FromSeconds(15)) }
    };

    [Theory]
    [MemberData(nameof(ErrorAnswers))]
    public async Task EachErrorAnswerIsClassifiedThroughTheQueryPath(string name, CannedHttpCase answer)
    {
        var http = answer.Http();

        var result = await Client(http).PollReturnsAsync(Shop(), new(null, null), new(null, 25), CancellationToken.None);

        Assert.True(http.Requests >= 1);
        answer.AssertMatches(name, result.Error);
    }

    [Fact]
    public async Task AThrottledGraphQlAnswerIsARateLimitWithTheRestoreWait()
    {
        var http = CannedHttp.Fixture(HttpStatusCode.OK, "Shopify/graphql-throttled.json");

        var result = await Client(http).PollReturnsAsync(Shop(), new(null, null), new(null, 25), CancellationToken.None);

        Assert.Equal((AdapterErrorClass.RateLimit, "SHOPIFY_RATE_LIMITED"), (result.Error?.Class, result.Error?.Code));
        Assert.NotNull(result.Error!.RetryAfter);
        Assert.InRange(result.Error.RetryAfter!.Value.TotalSeconds, 4, 6);
    }

    // ADAPTOR-TY-HB-9 / ADAPTOR-DIGER-10: returns, invoice and price-stock answers (synthetic fixtures, see
    // Fixtures/Shopify/README.md for why they are not anonymised live answers).
    [Fact]
    public async Task AReturnPageMapsEachReturnWithItsLinesReasonsAndTheNextCursor()
    {
        var http = CannedHttp.Fixture(HttpStatusCode.OK, "Shopify/returns-page.json");

        var result = await Client(http).PollReturnsAsync(Shop(), new(null, null), new(null, 25), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        ContractFixture.MatchesGolden("Shopify/returns-page.golden.json", result.Value);
        Assert.Equal(1, http.Requests);
        Assert.Equal(2, result.Value!.Items.Count);
        Assert.Equal(("cursor-page-2", true), (result.Value.NextCursor, result.Value.HasMore));
        // The latest change among every order of the page, with or without a return.
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 16, 45, 0, TimeSpan.Zero), result.Value.LastModifiedSeen);
    }

    [Theory]
    [InlineData("linked")]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("missing")]
    public async Task AnInvoiceReadSaysWhatTheOrdersLinkSlotHolds(string answer)
    {
        using var answers = ContractFixture.Json("Shopify/invoice-metafield-answers.json");
        var http = new CannedHttp(HttpStatusCode.OK, answers.RootElement.GetProperty(answer).GetRawText());

        var result = await Client(http).QueryDeliveryAsync(Shop(), new("gid://shopify/Order/9001", ExternalOrderId: "gid://shopify/Order/9001"), CancellationToken.None);

        ContractFixture.MatchesGolden($"Shopify/invoice-metafield-{answer}.golden.json", result.IsSuccess ? result.Value : new { result.Error!.Class, result.Error.Code, result.Error.HttpStatus });
        // An empty slot is not invoiced for the write; an unknown order is an error, never "not invoiced".
        if (answer == "missing") Assert.Equal("SHOPIFY_ORDER_NOT_FOUND", result.Error?.Code);
        else Assert.Equal(answer == "linked" ? "INVOICED" : "NOT_INVOICED", result.Value!.RawStatus);
    }

    [Fact]
    public async Task APriceStockSendRecordsEachLinesOutcome()
    {
        using var answers = ContractFixture.Json("Shopify/price-inventory-answers.json");
        var http = new SequencedHttp(answers.RootElement.GetProperty("answers").EnumerateArray().Select(answer => answer.GetRawText()).ToList());
        const string payload = """
            {"items":[
              {"barcode":"BC-1","variantId":"44791","quantity":7,"salePrice":120.5,"listPrice":150},
              {"barcode":"BC-2","quantity":3},
              {"barcode":"BC-3","variantId":"44793","quantity":1},
              {"barcode":"BC-4","variantId":"44794","quantity":4}
            ]}
            """;
        var context = new AdapterContext(Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"), "corr-1", "effect-1", DateTimeOffset.MaxValue);
        var client = Client(http);

        var sent = await client.PushPriceAndInventoryAsync(Shop(), context, payload, CancellationToken.None);

        Assert.True(sent.IsSuccess, sent.Error?.Code);
        Assert.Equal(7, http.Requests);
        var operation = await client.GetOperationAsync(context, sent.Value!.ExternalOperationId, CancellationToken.None);
        ContractFixture.MatchesGolden("Shopify/price-inventory.golden.json", operation.Value);
        Assert.Equal(new string?[] { "COMPLETED", "REJECTED", "REJECTED", "The specified compare quantity does not match the current quantity." }, operation.Value!.Lines.Select(line => line.Status).ToArray());
        Assert.Equal(new string?[] { null, "SHOPIFY_VARIANT_NOT_FOUND", "SHOPIFY_INVENTORY_NOT_TRACKED", "SHOPIFY_INVENTORY_REJECTED" }, operation.Value.Lines.Select(line => line.ErrorCode).ToArray());
    }

    // Answers the canned bodies in order, one per request.
    private sealed class SequencedHttp(IReadOnlyList<string> bodies) : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = Requests < bodies.Count ? bodies[Requests] : """{"errors":[{"message":"no canned answer left"}]}""";
            Requests++;
            return Task.FromResult(CannedHttp.Json(body));
        }
    }

    private static ShopifyHttpClient Client(IHttpClientFactory http) =>
        new(http, null!, Options.Create(new ShopifyOptions()), TimeProvider.System, NullLogger<ShopifyHttpClient>.Instance, null!, new ConfigurationBuilder().Build());

    private static ShopifyRequestContext Shop() => new(
        new PlatformConnection { PlatformCode = "SHOPIFY", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Shopify", ExternalStoreId = "fake-shop", ApiVersion = "2026-01" },
        "fake-shop", "2026-01", "shpat_fake", DateTimeOffset.MaxValue);
}
