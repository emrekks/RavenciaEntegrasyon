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

        var result = await MarketplaceEndpoints.ReceiveWebhook(Guid.CreateVersion7(), "route-token", context, service);
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

        var result = await MarketplaceEndpoints.ReceiveWebhook(connectionId, "route-token", context, service);
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
