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

        var result = await MarketplaceEndpoints.ReceiveWebhook(Guid.CreateVersion7(), "route-token", context, service, KnownGate, Quota);
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

        var result = await MarketplaceEndpoints.ReceiveWebhook(connectionId, "route-token", context, service, KnownGate, Quota);
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
        var body = Encoding.UTF8.GetBytes("{\"orderNumber\":\"4730000001\"}");
        var service = new RecordingWebhookService();
        var context = NewContext(body, body.Length);
        context.Request.Method = "PUT";
        context.Request.Headers[WebhookEventPath.Header] = "forged";

        var result = await MarketplaceEndpoints.ReceiveWebhookEvent(Guid.CreateVersion7(), "route-token", "/LineItems/B17553DE/Cancel/", context, service, KnownGate, Quota);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("lineitems/b17553de/cancel", service.Headers[WebhookEventPath.Header]);

        var plain = new RecordingWebhookService();
        var plainContext = NewContext(body, body.Length);
        plainContext.Request.Headers[WebhookEventPath.Header] = "forged";
        await (await MarketplaceEndpoints.ReceiveWebhook(Guid.CreateVersion7(), "route-token", plainContext, plain, KnownGate, Quota)).ExecuteAsync(plainContext);
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

        await (await MarketplaceEndpoints.ReceiveWebhookEvent(Guid.CreateVersion7(), "route-token", path, context, new RecordingWebhookService(), KnownGate, Quota)).ExecuteAsync(context);

        Assert.Equal(expected, context.Response.StatusCode);
    }

    [Fact]
    public async Task ReceiveWebhook_ForAnUnknownConnection_Answers404WithoutReadingTheBody()
    {
        var service = new RecordingWebhookService();
        var context = NewContext([], 10 * 1024 * 1024);
        context.Request.Body = new UnreadableStream();

        var result = await MarketplaceEndpoints.ReceiveWebhook(Guid.CreateVersion7(), "route-token", context, service, new Gate(false), Quota);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(service.Called);
        Assert.True(service.RouteRefusalNoted);
    }

    // Every refused call reaches the record (its writes are held to once a minute by their own conditions), so a stream of
    // wrong-token calls cannot hide the calls to a closed definition of the same address; only the log line is held to
    // once a minute per address and reason. An unknown address leaves neither a log line nor a cache entry.
    [Fact]
    public async Task ReceiveWebhook_KeepsEveryRefusal_AndLogsOncePerAddressAndReasonAMinute()
    {
        var logs = new CountingLoggerProvider();
        var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs).AddMemoryCache(options => options.SizeLimit = 100).BuildServiceProvider();
        var connection = Guid.CreateVersion7();
        async Task CallAsync(RecordingWebhookService service, Guid address, string token)
        {
            var context = NewContext([], 0);
            context.RequestServices = services;
            await (await MarketplaceEndpoints.ReceiveWebhook(address, token, context, service, new Gate(false), Quota)).ExecuteAsync(context);
            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        }

        var wrongToken = new RecordingWebhookService { RefusalCode = "WEBHOOK_ROUTE_TOKEN_MISMATCH" };
        for (var index = 0; index < 5; index++) await CallAsync(wrongToken, connection, $"guess-{index}");
        var closed = new RecordingWebhookService { RefusalCode = "WEBHOOK_CALLS_REJECTED" };
        for (var index = 0; index < 3; index++) await CallAsync(closed, connection, "closed-token");
        Assert.Equal(5, wrongToken.RefusalsNoted);
        Assert.Equal(3, closed.RefusalsNoted);
        Assert.Equal(2, logs.Count("Webhook route refused"));

        var unknown = new RecordingWebhookService();
        for (var index = 0; index < 3; index++) await CallAsync(unknown, Guid.CreateVersion7(), "guess");
        Assert.Equal(3, unknown.RefusalsNoted);
        Assert.Equal(2, logs.Count("Webhook route refused"));
    }

    private sealed class CountingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new();
        public int Count(string start) => messages.Count(message => message.StartsWith(start, StringComparison.Ordinal));
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(messages);
        public void Dispose() { }
        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }

    // A fault while the refusal is kept does not turn the address's 404 into a 500.
    [Fact]
    public async Task ReceiveWebhook_WhenKeepingTheRefusalFails_StillAnswers404()
    {
        var service = new RecordingWebhookService { RefusalFault = new TimeoutException("database timeout") };
        var context = NewContext([], 0);

        await (await MarketplaceEndpoints.ReceiveWebhookEvent(Guid.CreateVersion7(), "closed-token", "orders", context, service, new Gate(false), Quota)).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(1, service.RefusalsNoted);
    }

    // The event path is the sender's: a log line keeps at most 64 characters of it.
    [Fact]
    public void LoggedPath_KeepsAtMost64CharactersOfTheSendersPath()
    {
        Assert.Equal("/", MarketplaceEndpoints.LoggedPath(null));
        Assert.Equal("orders", MarketplaceEndpoints.LoggedPath("orders"));
        var logged = MarketplaceEndpoints.LoggedPath(new string('x', 10_000));
        Assert.Equal(65, logged.Length);
        Assert.EndsWith("…", logged);
    }

    [Fact]
    public void WebhookLimits_LimitEachAddress()
    {
        using var limiter = WebhookRateLimits.Create();
        // Random connection ids from one address: a per-connection limit alone never applied.
        for (var index = 0; index < WebhookRateLimits.PerAddressPerMinute; index++)
            Assert.True(limiter.AttemptAcquire(Hook(Guid.NewGuid(), "203.0.113.7")).IsAcquired);

        Assert.False(limiter.AttemptAcquire(Hook(Guid.NewGuid(), "203.0.113.7")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Hook(Guid.NewGuid(), "203.0.113.8")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Plain("/api/v1/orders", "203.0.113.7")).IsAcquired);
    }

    // A-2 (2026-10-29): the limiter runs before the route token is checked, so it must not hold the connection's
    // allowance; a sender that knows only the public id would use it up with made-up tokens.
    [Fact]
    public void WebhookLimits_BeforeTheEndpoint_DoNotSpendTheConnectionAllowance()
    {
        using var limiter = WebhookRateLimits.Create();
        var connection = Guid.NewGuid();
        for (var index = 0; index <= WebhookRateLimits.PerConnectionPerMinute; index++)
            Assert.True(limiter.AttemptAcquire(Hook(connection, "203.0.113.9")).IsAcquired);
    }

    [Fact]
    public async Task ReceiveWebhook_WithAWrongRouteToken_DoesNotSpendTheConnectionAllowance()
    {
        using var quota = new WebhookConnectionQuota();
        var connection = Guid.CreateVersion7();
        for (var index = 0; index <= WebhookRateLimits.PerConnectionPerMinute; index++)
        {
            var refused = NewContext([], 0);
            await (await MarketplaceEndpoints.ReceiveWebhook(connection, $"guess-{index}", refused, new RecordingWebhookService(), new Gate(false), quota)).ExecuteAsync(refused);
            Assert.Equal(StatusCodes.Status404NotFound, refused.Response.StatusCode);
        }

        var body = Encoding.UTF8.GetBytes("{\"id\":1}");
        var service = new RecordingWebhookService();
        var context = NewContext(body, body.Length);
        await (await MarketplaceEndpoints.ReceiveWebhook(connection, "route-token", context, service, KnownGate, quota)).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(service.Called);
    }

    [Fact]
    public async Task ReceiveWebhook_KeepsThePerConnectionLimitForAdmittedDeliveries_WithTheRateLimitedAnswer()
    {
        using var quota = new WebhookConnectionQuota();
        var connection = Guid.CreateVersion7();
        for (var index = 0; index < WebhookRateLimits.PerConnectionPerMinute; index++) Assert.True(quota.TryTake(connection));

        var service = new RecordingWebhookService();
        var context = NewContext([], 10 * 1024 * 1024);
        context.Request.Body = new UnreadableStream();
        await (await MarketplaceEndpoints.ReceiveWebhookEvent(connection, "route-token", "orders", context, service, KnownGate, quota)).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.False(service.Called);
        Assert.False(service.RouteRefusalNoted);
        context.Response.Body.Position = 0;
        using var answer = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("RATE_LIMITED", answer.RootElement.GetProperty("code").GetString());
        Assert.True(answer.RootElement.GetProperty("retryable").GetBoolean());
        // Another connection keeps its own allowance.
        Assert.True(quota.TryTake(Guid.CreateVersion7()));
    }

    private static readonly IWebhookRouteGate KnownGate = new Gate(true);
    private static readonly WebhookConnectionQuota Quota = new();

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

        public bool RouteRefusalNoted => RefusalsNoted > 0;
        public int RefusalsNoted { get; private set; }
        // What the service answers for a refused address: null for one that names nothing.
        public string? RefusalCode { get; init; }

        // A database fault while the refusal is kept (a timeout, a deadlock).
        public Exception? RefusalFault { get; init; }

        public Task<string?> NoteRouteRefusedAsync(Guid connectionPublicId, string routeToken, CancellationToken cancellationToken)
        {
            RefusalsNoted++;
            return RefusalFault is null ? Task.FromResult(RefusalCode) : Task.FromException<string?>(RefusalFault);
        }
    }
}
