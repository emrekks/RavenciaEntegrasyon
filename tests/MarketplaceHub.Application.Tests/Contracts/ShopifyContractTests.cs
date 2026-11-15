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

    private static ShopifyHttpClient Client(CannedHttp http) =>
        new(http, null!, Options.Create(new ShopifyOptions()), TimeProvider.System, NullLogger<ShopifyHttpClient>.Instance, null!, new ConfigurationBuilder().Build());

    private static ShopifyRequestContext Shop() => new(
        new PlatformConnection { PlatformCode = "SHOPIFY", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Shopify", ExternalStoreId = "fake-shop", ApiVersion = "2026-01" },
        "fake-shop", "2026-01", "shpat_fake", DateTimeOffset.MaxValue);
}
