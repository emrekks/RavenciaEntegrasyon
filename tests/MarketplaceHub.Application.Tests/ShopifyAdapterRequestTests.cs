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
    public async Task InventoryWriteSendsTheIdempotentInventorySetQuantitiesMutation()
    {
        var handler = new Capturing("""{"data":{"inventorySetQuantities":{"userErrors":[]}}}""");
        var client = Client(handler);

        var result = await client.SetAvailableQuantityAsync(Shop(), Context(), "8690000000001", "gid://shopify/InventoryItem/7", "gid://shopify/Location/3", 12, 9, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(
            "mutation($input:InventorySetQuantitiesInput!,$idempotencyKey:String!){inventorySetQuantities(input:$input) @idempotent(key:$idempotencyKey){userErrors{field message}}}",
            body.RootElement.GetProperty("query").GetString());
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal(["input", "idempotencyKey"], variables.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(ShopifyHttpClient.IdempotencyKey("effect-1:8690000000001:inventory"), variables.GetProperty("idempotencyKey").GetString());
        var input = variables.GetProperty("input");
        Assert.Equal(["name", "reason", "referenceDocumentUri", "quantities"], input.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("available", input.GetProperty("name").GetString());
        Assert.Equal("correction", input.GetProperty("reason").GetString());
        Assert.Equal($"marketplacehub://{TenantId:D}/{ConnectionId:D}/8690000000001", input.GetProperty("referenceDocumentUri").GetString());
        var quantity = Assert.Single(input.GetProperty("quantities").EnumerateArray());
        Assert.Equal(["inventoryItemId", "locationId", "quantity", "changeFromQuantity"], quantity.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("gid://shopify/InventoryItem/7", quantity.GetProperty("inventoryItemId").GetString());
        Assert.Equal("gid://shopify/Location/3", quantity.GetProperty("locationId").GetString());
        Assert.Equal(12, quantity.GetProperty("quantity").GetInt32());
        Assert.Equal(9, quantity.GetProperty("changeFromQuantity").GetInt32());
    }

    [Fact]
    public async Task TheSameEffectAndBarcodeAlwaysSendTheSameIdempotencyKey()
    {
        var handler = new Capturing("""{"data":{"inventorySetQuantities":{"userErrors":[]}}}""");
        var client = Client(handler);

        await client.SetAvailableQuantityAsync(Shop(), Context(), "B-1", "item", "location", 3, 2, CancellationToken.None);
        await client.SetAvailableQuantityAsync(Shop(), Context(), "B-1", "item", "location", 3, 2, CancellationToken.None);
        await client.SetAvailableQuantityAsync(Shop(), Context(), "B-2", "item", "location", 3, 2, CancellationToken.None);

        var keys = handler.Bodies.Select(body => JsonDocument.Parse(body).RootElement.GetProperty("variables").GetProperty("idempotencyKey").GetString()).ToArray();
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[0], keys[2]);
        Assert.Matches("^[0-9a-f]{64}$", keys[0]);
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
    public void ReturnQueriesAskFulfillmentLinesThroughTheReturnLineItemFragment()
    {
        foreach (var query in new[] { ShopifyHttpClient.ReturnPageQuery, ShopifyHttpClient.SingleReturnQuery, ShopifyHttpClient.OrderReturnsContinuationQuery, ShopifyHttpClient.ReturnLinesContinuationQuery })
        {
            Assert.Contains("... on ReturnLineItem{fulfillmentLineItem{id lineItem{id}}}", query, StringComparison.Ordinal);
            Assert.Contains("returnReasonDefinition{handle name}", query, StringComparison.Ordinal);
            Assert.DoesNotContain("returnReason ", query, StringComparison.Ordinal);
            Assert.DoesNotContain("{id quantity returnReasonNote returnReasonDefinition{handle name} fulfillmentLineItem", query, StringComparison.Ordinal);
        }
        Assert.Contains("returns(first:20){nodes{", ShopifyHttpClient.ReturnPageQuery, StringComparison.Ordinal);
        Assert.Contains("} pageInfo{hasNextPage endCursor}}}} pageInfo{hasNextPage endCursor}}}", ShopifyHttpClient.ReturnPageQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReturnWithMoreLinesOrAnOrderWithMoreReturnsIsReadOnToTheEnd()
    {
        var handler = new Capturing(
            """{"data":{"orders":{"edges":[{"node":{"id":"gid://shopify/Order/1","name":"#1001","createdAt":"2026-10-01T10:00:00Z","returns":{"nodes":[{"id":"gid://shopify/Return/7","status":"OPEN","createdAt":"2026-10-02T10:00:00Z","returnLineItems":{"nodes":[{"id":"gid://shopify/ReturnLineItem/71","quantity":1,"returnReasonNote":"Kırık","returnReasonDefinition":{"handle":"damaged","name":"Hasarlı"},"fulfillmentLineItem":{"id":"gid://shopify/FulfillmentLineItem/1","lineItem":{"id":"gid://shopify/LineItem/11"}}}],"pageInfo":{"hasNextPage":true,"endCursor":"L1"}}}],"pageInfo":{"hasNextPage":true,"endCursor":"R1"}}}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""",
            """{"data":{"order":{"id":"gid://shopify/Order/1","name":"#1001","createdAt":"2026-10-01T10:00:00Z","returns":{"nodes":[{"id":"gid://shopify/Return/8","status":"CLOSED","createdAt":"2026-10-03T10:00:00Z","returnLineItems":{"nodes":[{"id":"gid://shopify/ReturnLineItem/81","quantity":2,"returnReasonNote":"","fulfillmentLineItem":{"id":"gid://shopify/FulfillmentLineItem/2","lineItem":{"id":"gid://shopify/LineItem/12"}}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""",
            """{"data":{"return":{"id":"gid://shopify/Return/7","returnLineItems":{"nodes":[{"id":"gid://shopify/ReturnLineItem/72","quantity":3,"returnReasonNote":"","fulfillmentLineItem":{"id":"gid://shopify/FulfillmentLineItem/3","lineItem":{"id":"gid://shopify/LineItem/13"}}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
        var client = Client(handler);

        var result = await client.PollReturnsAsync(Shop(), new ReturnPollWindow(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), null), new(null, 10), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var requests = handler.Bodies.Select(body => JsonDocument.Parse(body).RootElement).ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("updated_at:>='2026-10-01T00:00:00Z'", requests[0].GetProperty("variables").GetProperty("query").GetString());
        Assert.Equal(ShopifyHttpClient.OrderReturnsContinuationQuery, requests[1].GetProperty("query").GetString());
        Assert.Equal("gid://shopify/Order/1", requests[1].GetProperty("variables").GetProperty("id").GetString());
        Assert.Equal("R1", requests[1].GetProperty("variables").GetProperty("after").GetString());
        Assert.Equal(ShopifyHttpClient.ReturnLinesContinuationQuery, requests[2].GetProperty("query").GetString());
        Assert.Equal("gid://shopify/Return/7", requests[2].GetProperty("variables").GetProperty("id").GetString());
        Assert.Equal("L1", requests[2].GetProperty("variables").GetProperty("after").GetString());
        var claims = result.Value!.Items;
        Assert.Equal(["7", "8"], claims.Select(claim => claim.ExternalClaimId).ToArray());
        Assert.Equal(["11", "13"], claims[0].Lines.Select(line => line.ExternalOrderLineId).ToArray());
        Assert.Equal("damaged", claims[0].ReasonCode);
        Assert.Equal("Kırık", claims[0].ReasonText);
        Assert.Equal(["12"], claims[1].Lines.Select(line => line.ExternalOrderLineId).ToArray());
    }

    [Fact]
    public async Task AReturnContinuationThatNeverEndsFailsInsteadOfCuttingTheReturn()
    {
        var handler = new Capturing(
            """{"data":{"orders":{"edges":[{"node":{"id":"gid://shopify/Order/1","name":"#1001","createdAt":"2026-10-01T10:00:00Z","returns":{"nodes":[{"id":"gid://shopify/Return/7","status":"OPEN","createdAt":"2026-10-02T10:00:00Z","returnLineItems":{"nodes":[],"pageInfo":{"hasNextPage":true,"endCursor":null}}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");
        var client = Client(handler);

        var result = await client.PollReturnsAsync(Shop(), new ReturnPollWindow(null, null), new(null, 10), CancellationToken.None);

        Assert.Equal("SHOPIFY_RETURN_CONTINUATION_INCOMPLETE", result.Error?.Code);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("OPEN", ReturnClaimStatus.Approved)]
    [InlineData("CLOSED", ReturnClaimStatus.Completed)]
    [InlineData("DECLINED", ReturnClaimStatus.Rejected)]
    [InlineData("REQUESTED", ReturnClaimStatus.ActionRequired)]
    [InlineData("CANCELED", ReturnClaimStatus.Cancelled)]
    public void ShopifyReturnStatusesAreCanonicalised(string raw, ReturnClaimStatus expected) =>
        Assert.Equal(expected, MarketplaceReturnStatus.Canonicalize(raw));

    [Fact]
    public void AReturnDeclineNotifiesTheCustomer()
    {
        var json = JsonSerializer.SerializeToElement(ShopifyHttpClient.ReturnActionVariables("REJECT", "gid://shopify/Return/7", "  Ürün kullanılmış  "));
        var input = json.GetProperty("input");
        Assert.Equal(["id", "declineReason", "notifyCustomer", "declineNote"], input.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.True(input.GetProperty("notifyCustomer").GetBoolean());
        Assert.Equal("Ürün kullanılmış", input.GetProperty("declineNote").GetString());
        var approve = JsonSerializer.SerializeToElement(ShopifyHttpClient.ReturnActionVariables("APPROVE", "gid://shopify/Return/7", null));
        Assert.Equal(["id"], approve.GetProperty("input").EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Theory]
    [InlineData("""{"barcodes":{"nodes":[{"value":"869001"}]},"barcode":"OLD"}""", "869001")]
    [InlineData("""{"barcodes":{"nodes":[]}}""", null)]
    [InlineData("""{"barcode":"869002"}""", "869002")]
    [InlineData("""{"barcode":null}""", null)]
    public void TheVariantBarcodeComesFromTheBarcodesConnection(string variant, string? expected)
    {
        using var json = JsonDocument.Parse(variant);
        Assert.Equal(expected, ShopifyHttpClient.VariantBarcode(json.RootElement));
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

    private static AdapterContext Context() => new(TenantId, ConnectionId, "correlation-1", "effect-1", DateTimeOffset.UtcNow.AddMinutes(1));

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
