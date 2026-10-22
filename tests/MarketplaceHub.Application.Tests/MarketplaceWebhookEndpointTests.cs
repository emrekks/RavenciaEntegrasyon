using System.Text;
using MarketplaceHub.Api.Marketplace;
using MarketplaceHub.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceWebhookEndpointTests
{
    [Fact]
    public async Task ReceiveWebhook_RejectsOversizedBody_BeforeCallingService()
    {
        var service = new RecordingWebhookService();
        var context = NewContext(Array.Empty<byte>(), MarketplaceHub.Api.Marketplace.WebhookBodyReader.MaximumBytes + 1L);

        var result = await MarketplaceEndpoints.ReceiveWebhook(Guid.CreateVersion7(), "route-token", context, service, KnownGate);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status413RequestEntityTooLarge, context.Response.StatusCode);
        Assert.False(service.Called);
    }

    [Fact]
    public async Task ReceiveWebhook_ForValidBody_ForwardsPayloadHeadersAndRouteData()
    {
        var connectionId = Guid.CreateVersion7();
        var body = Encoding.UTF8.GetBytes("{\"shipmentPackageId\":123,\"status\":\"Created\"}");
        var service = new RecordingWebhookService();
        var context = NewContext(body, body.Length);
        context.Request.Headers["x-trendyol-signature"] = "signature";
        context.Request.Headers["x-request-id"] = "request-1";

        var result = await MarketplaceEndpoints.ReceiveWebhook(connectionId, "route-token", context, service, KnownGate);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(service.Called);
        Assert.Equal(connectionId, service.ConnectionPublicId);
        Assert.Equal("route-token", service.RouteToken);
        Assert.Equal(body, service.Body);
        Assert.Equal("signature", service.Headers["x-trendyol-signature"]);
        Assert.Equal("request-1", service.Headers["x-request-id"]);
        Assert.Equal(context.TraceIdentifier, service.CorrelationId);
    }

    [Fact]
    public async Task ReceiveWebhookEvent_PassesTheRoutePathAndIgnoresASentPathHeader()
    {
        var body = Encoding.UTF8.GetBytes("{\"orderNumber\":\"4736002251\"}");
        var service = new RecordingWebhookService();
        var context = NewContext(body, body.Length);
        context.Request.Method = "PUT";
        context.Request.Headers[WebhookEventPath.Header] = "forged";

        var result = await MarketplaceEndpoints.ReceiveWebhookEvent(Guid.CreateVersion7(), "route-token", "/LineItems/B17553DE/Cancel/", context, service, KnownGate);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("lineitems/b17553de/cancel", service.Headers[WebhookEventPath.Header]);

        var plain = new RecordingWebhookService();
        var plainContext = NewContext(body, body.Length);
        plainContext.Request.Headers[WebhookEventPath.Header] = "forged";
        await (await MarketplaceEndpoints.ReceiveWebhook(Guid.CreateVersion7(), "route-token", plainContext, plain, KnownGate)).ExecuteAsync(plainContext);
        Assert.False(plain.Headers.ContainsKey(WebhookEventPath.Header));
    }

    // Hepsiburada's contract: POST orders, packages and claims/packages answer 201 Created, every PUT answers 204.
    [Theory]
    [InlineData("POST", "orders", StatusCodes.Status201Created)]
    [InlineData("POST", "packages", StatusCodes.Status201Created)]
    [InlineData("POST", "claims/packages", StatusCodes.Status201Created)]
    [InlineData("PUT", "lineitems/6ad0b850/cancel", StatusCodes.Status204NoContent)]
    [InlineData("PUT", "packages/1234567890/intransit", StatusCodes.Status204NoContent)]
    [InlineData("PUT", "orders/999999999/shippingaddress", StatusCodes.Status204NoContent)]
    [InlineData("PUT", "claims/awaitingaction", StatusCodes.Status204NoContent)]
    public async Task ReceiveWebhookEvent_AnswersTheCodeHepsiburadasContractNames(string method, string path, int expected)
    {
        var body = Encoding.UTF8.GetBytes("{\"orderNumber\":\"999999999\"}");
        var context = NewContext(body, body.Length);
        context.Request.Method = method;

        await (await MarketplaceEndpoints.ReceiveWebhookEvent(Guid.CreateVersion7(), "route-token", path, context, new RecordingWebhookService(), KnownGate)).ExecuteAsync(context);

        Assert.Equal(expected, context.Response.StatusCode);
    }

    [Fact]
    public async Task ReceiveWebhook_ForAnUnknownConnection_Answers404WithoutReadingTheBody()
    {
        var service = new RecordingWebhookService();
        var context = NewContext([], 10 * 1024 * 1024);
        context.Request.Body = new UnreadableStream();

        var result = await MarketplaceEndpoints.ReceiveWebhook(Guid.CreateVersion7(), "route-token", context, service, new Gate(false));
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(service.Called);
    }

    [Fact]
    public void WebhookLimits_ChainAPerAddressLimitWithThePerConnectionOne()
    {
        using var limiter = WebhookRateLimits.Create();
        // Random connection ids from one address: the per-connection limit alone never applied.
        for (var index = 0; index < WebhookRateLimits.PerAddressPerMinute; index++)
            Assert.True(limiter.AttemptAcquire(Hook(Guid.NewGuid(), "203.0.113.7")).IsAcquired);

        Assert.False(limiter.AttemptAcquire(Hook(Guid.NewGuid(), "203.0.113.7")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Hook(Guid.NewGuid(), "203.0.113.8")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Plain("/api/v1/orders", "203.0.113.7")).IsAcquired);
    }

    [Fact]
    public void WebhookLimits_KeepThePerConnectionLimitAcrossAddresses()
    {
        using var limiter = WebhookRateLimits.Create();
        var connection = Guid.NewGuid();
        for (var index = 0; index < WebhookRateLimits.PerConnectionPerMinute; index++)
            Assert.True(limiter.AttemptAcquire(Hook(connection, $"198.51.100.{index % 200}")).IsAcquired);

        Assert.False(limiter.AttemptAcquire(Hook(connection, "198.51.100.250")).IsAcquired);
    }

    private static readonly IWebhookRouteGate KnownGate = new Gate(true);

    private static DefaultHttpContext Hook(Guid connection, string address) => Plain($"/api/v1/hooks/{connection:D}/token", address);

    private static DefaultHttpContext Plain(string path, string address)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
        return context;
    }

    private sealed class Gate(bool known) : IWebhookRouteGate
    {
        public ValueTask<bool> IsKnownAsync(Guid connectionPublicId, string routeToken, CancellationToken cancellationToken) => ValueTask.FromResult(known);
    }

    private sealed class UnreadableStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("The body must not be read.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new InvalidOperationException("The body must not be read.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The body must not be read.");
    }

    private static DefaultHttpContext NewContext(byte[] body, long? contentLength)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Request.Body = new MemoryStream(body, writable: false);
        context.Request.ContentLength = contentLength;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class RecordingWebhookService : IMarketplaceWebhookService
    {
        public bool Called { get; private set; }
        public Guid ConnectionPublicId { get; private set; }
        public string? RouteToken { get; private set; }
        public byte[] Body { get; private set; } = [];
        public IReadOnlyDictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();
        public string? CorrelationId { get; private set; }

        public Task<ServiceResult<bool>> ReceiveAsync(Guid connectionPublicId, string routeToken, ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, string correlationId, CancellationToken cancellationToken)
        {
            Called = true;
            ConnectionPublicId = connectionPublicId;
            RouteToken = routeToken;
            Body = rawBody.ToArray();
            Headers = headers;
            CorrelationId = correlationId;
            return Task.FromResult(ServiceResult<bool>.Ok(true));
        }
    }
}
