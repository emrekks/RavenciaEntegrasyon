using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyAdapterRequestTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConnectionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task InventoryWriteSendsTheDocumentedInventorySetQuantitiesShape()
    {
        var handler = new Capturing("""{"data":{"inventorySetQuantities":{"userErrors":[]}}}""");
        var client = Client(handler);

        var result = await client.SetAvailableQuantityAsync(Shop(), Context(), "8690000000001", "gid://shopify/InventoryItem/7", "gid://shopify/Location/3", 12, 9, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(
            "mutation($input:InventorySetQuantitiesInput!){inventorySetQuantities(input:$input){userErrors{field message}}}",
            body.RootElement.GetProperty("query").GetString());
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal(["input"], variables.EnumerateObject().Select(property => property.Name).ToArray());
        var input = variables.GetProperty("input");
        Assert.Equal(["name", "reason", "referenceDocumentUri", "ignoreCompareQuantity", "quantities"], input.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("available", input.GetProperty("name").GetString());
        Assert.Equal("correction", input.GetProperty("reason").GetString());
        Assert.Equal($"marketplacehub://{TenantId:D}/{ConnectionId:D}/8690000000001", input.GetProperty("referenceDocumentUri").GetString());
        Assert.False(input.GetProperty("ignoreCompareQuantity").GetBoolean());
        var quantity = Assert.Single(input.GetProperty("quantities").EnumerateArray());
        Assert.Equal(["inventoryItemId", "locationId", "quantity", "compareQuantity"], quantity.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("gid://shopify/InventoryItem/7", quantity.GetProperty("inventoryItemId").GetString());
        Assert.Equal("gid://shopify/Location/3", quantity.GetProperty("locationId").GetString());
        Assert.Equal(12, quantity.GetProperty("quantity").GetInt32());
        Assert.Equal(9, quantity.GetProperty("compareQuantity").GetInt32());
        Assert.DoesNotContain("@idempotent", handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("changeFromQuantity", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAKnownQuantityTheCompareCheckIsSkippedExplicitly()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ShopifyHttpClient.InventorySetQuantitiesVariables(TenantId, ConnectionId, "B", "item", "location", 1, null)));

        Assert.True(json.RootElement.GetProperty("input").GetProperty("ignoreCompareQuantity").GetBoolean());
    }

    [Fact]
    public async Task AReturnPageOverTheCostCeilingIsAskedAgainWithAQuarterOfTheOrders()
    {
        var handler = new Capturing(
            """{"errors":[{"message":"Query cost is 1520, which exceeds the single query max cost limit (1000).","extensions":{"code":"MAX_COST_EXCEEDED","cost":1520,"maxCost":1000}}]}""",
            """{"data":{"orders":{"edges":[{"node":{"id":"gid://shopify/Order/1","name":"#1001","createdAt":"2026-10-01T10:00:00Z","returns":{"nodes":[]}}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");
        var client = Client(handler);

        var result = await client.PollReturnsAsync(Shop(), new ReturnPollWindow(null, null), new(null, 100), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal([100, 25], handler.Bodies.Select(body => JsonDocument.Parse(body).RootElement.GetProperty("variables").GetProperty("first").GetInt32()).ToArray());
        var query = JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("query").GetString()!;
        Assert.Contains("returns(first:20)", query, StringComparison.Ordinal);
        Assert.Contains("returnLineItems(first:50)", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrottledQueryWaitsOnlyForTheCostItAskedFor()
    {
        var handler = new Capturing("""{"errors":[{"message":"Throttled","extensions":{"code":"THROTTLED"}}],"extensions":{"cost":{"requestedQueryCost":500,"actualQueryCost":null,"throttleStatus":{"maximumAvailable":2000,"currentlyAvailable":100,"restoreRate":50}}}}""");
        var client = Client(handler);

        var result = await client.PollReturnsAsync(Shop(), new ReturnPollWindow(null, null), new(null, 10), CancellationToken.None);

        Assert.Equal(AdapterErrorClass.RateLimit, result.Error!.Class);
        Assert.Equal(TimeSpan.FromSeconds(8), result.Error.RetryAfter);
    }

    [Theory]
    [InlineData("""{"status":"SUCCESS"}""", "SHIPPED")]
    [InlineData("""{"status":"OPEN"}""", "PROCESSING")]
    [InlineData("""{"status":"CANCELLED"}""", "CANCELLED")]
    [InlineData("""{"status":"FAILURE"}""", "FULFILLMENT_FAILED")]
    [InlineData("""{"status":"ERROR"}""", "FULFILLMENT_FAILED")]
    [InlineData("""{"status":"SOMETHING_NEW"}""", "UNKNOWN")]
    [InlineData("""{}""", "UNKNOWN")]
    public void FulfillmentStatusIsMappedExplicitly(string fulfillment, string expected)
    {
        using var json = JsonDocument.Parse(fulfillment);

        Assert.Equal(expected, ShopifyHttpClient.FulfillmentStatus(json.RootElement));
    }

    [Theory]
    [InlineData("""{"status":"FAILURE"}""")]
    [InlineData("""{"status":"SOMETHING_NEW"}""")]
    public void AFailedOrUnknownFulfillmentIsNeverTakenAsShipped(string fulfillment)
    {
        using var json = JsonDocument.Parse(fulfillment);

        Assert.Equal(ShipmentPackageStatus.ManualReview, ShipmentPackageStatusPolicy.FromRemote(ShopifyHttpClient.FulfillmentStatus(json.RootElement)));
    }

    private static ShopifyHttpClient Client(HttpMessageHandler handler) => new(
        new Factory(handler), null!, Options.Create(new ShopifyOptions()), TimeProvider.System, NullLogger<ShopifyHttpClient>.Instance, null!, new ConfigurationBuilder().Build());

    private static ShopifyRequestContext Shop() => new(
        new PlatformConnection { Id = ConnectionId, TenantId = TenantId, PlatformCode = "SHOPIFY", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Shopify", ExternalStoreId = "test-shop", ApiVersion = "2026-10" },
        "test-shop", "2026-10", "fixture-token", DateTimeOffset.MaxValue);

    private static AdapterContext Context() => new(TenantId, ConnectionId, "effect-1", "correlation-1", DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class Capturing(params string[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = responses[Math.Min(Bodies.Count - 1, responses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
