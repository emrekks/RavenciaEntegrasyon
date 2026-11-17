using System.Security.Claims;
using System.Text.Json;
using MarketplaceHub.Api.Marketplace;
using MarketplaceHub.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ApiErrorEnvelopeTests
{
    [Fact]
    public async Task Problem_CarriesTheRequestsCorrelationIdEvenWhenBuiltWithoutTheContext()
    {
        var context = NewContext();
        context.TraceIdentifier = "corr-123";

        await ApiProblems.Of(null, "ORDER_LOCKED", "Sipariş kilitli.", 409).ExecuteAsync(context);

        var body = await ReadBody(context);
        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal(ApiProblems.ContentType, context.Response.ContentType?.Split(';')[0]);
        Assert.Equal("https://marketplacehub.invalid/problems/order-locked", body.GetProperty("type").GetString());
        Assert.Equal("Sipariş kilitli.", body.GetProperty("title").GetString());
        Assert.Equal("ORDER_LOCKED", body.GetProperty("code").GetString());
        Assert.Equal("corr-123", body.GetProperty("correlationId").GetString());
        Assert.False(body.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task Problem_AddsExtensionsWithoutOverridingTheStandardMembers()
    {
        var context = NewContext();
        var extensions = new Dictionary<string, object?> { ["tenants"] = new[] { new { id = "t1", displayName = "Mağaza" } }, ["code"] = "FORGED" };

        await ApiProblems.Of(context, "TENANT_SELECTION_REQUIRED", "Çalışma alanı seçin.", 409, extensions).ExecuteAsync(context);

        var body = await ReadBody(context);
        Assert.Equal("TENANT_SELECTION_REQUIRED", body.GetProperty("code").GetString());
        Assert.Equal("Mağaza", body.GetProperty("tenants")[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task RoleGateRefusal_UsesTheSharedShape()
    {
        var context = NewContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "READONLY")], "test"));
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/settings/appearance";

        await new RoleAuthorizationMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        var body = await ReadBody(context);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal("PERMISSION_DENIED", body.GetProperty("code").GetString());
        Assert.Equal(context.TraceIdentifier, body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task CsrfRefusal_KeepsTheCodeTheWebClientRetriesOn()
    {
        var context = NewContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/orders/11111111-1111-1111-1111-111111111111/instant-process";

        await new RequestSecurityMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        var body = await ReadBody(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(ApiProblems.ContentType, context.Response.ContentType?.Split(';')[0]);
        Assert.Equal("REQUEST_VERIFICATION_FAILED", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(400, "INVALID_REQUEST_BODY")]
    [InlineData(413, "REQUEST_TOO_LARGE")]
    public async Task UnreadableRequest_IsAnsweredAsAClientErrorInTheSharedShape(int frameworkStatus, string code)
    {
        var context = NewContext();
        context.Response.StatusCode = 500;

        var handled = await new ApiExceptionHandler().TryHandleAsync(context, new BadHttpRequestException("Failed to read parameter", frameworkStatus), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(frameworkStatus, context.Response.StatusCode);
        Assert.Equal(code, (await ReadBody(context)).GetProperty("code").GetString());
    }

    // API-GUVENLIK-4: an unexpected failure answers in the panel's shape (Turkish, with a code, the correlation id and
    // retryable), never the framework's English ProblemDetails, and never with the exception's own text.
    [Fact]
    public async Task UnexpectedException_IsAnInternalErrorInTheSharedShape()
    {
        var context = NewContext();
        context.TraceIdentifier = "corr-500";

        var handled = await new ApiExceptionHandler().TryHandleAsync(context, new InvalidOperationException("boom at Secret.Path"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(ApiProblems.ContentType, context.Response.ContentType?.Split(';')[0]);
        var body = await ReadBody(context);
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("code").GetString());
        Assert.Equal("corr-500", body.GetProperty("correlationId").GetString());
        Assert.True(body.GetProperty("retryable").GetBoolean());
        Assert.Contains("corr-500", body.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("boom", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Secret.Path", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrencyException_IsAConflict()
    {
        var context = NewContext();

        Assert.True(await new ApiExceptionHandler().TryHandleAsync(context, new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("row changed"), CancellationToken.None));

        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", (await ReadBody(context)).GetProperty("code").GetString());
    }

    // Review (18 Nov 2026): a save that met another on the same rows (a model-row lock wait that ran out, 55P03; a
    // serialization failure or a deadlock), on any path without its own answer, is a 409 to try again, not a 500.
    [Fact]
    public async Task ALockConflict_IsATryAgainConflict_NotAnInternalError()
    {
        foreach (var state in new[] { Npgsql.PostgresErrorCodes.LockNotAvailable, Npgsql.PostgresErrorCodes.SerializationFailure, Npgsql.PostgresErrorCodes.DeadlockDetected })
        {
            var context = NewContext();
            var wrapped = new InvalidOperationException("strategy", new Microsoft.EntityFrameworkCore.DbUpdateException("save", new Npgsql.PostgresException("lock", "ERROR", "ERROR", state)));

            Assert.True(await new ApiExceptionHandler().TryHandleAsync(context, wrapped, CancellationToken.None));

            Assert.Equal(409, context.Response.StatusCode);
            var body = await ReadBody(context);
            Assert.Equal("SAVE_CONFLICT_RETRY", body.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ArgumentException_IsABadRequest_InEveryEndpointGroup()
    {
        var context = NewContext();

        Assert.True(await new ApiExceptionHandler().TryHandleAsync(context, new ArgumentException("limit 1-200 arasında olmalıdır.", "limit"), CancellationToken.None));

        Assert.Equal(400, context.Response.StatusCode);
        var body = await ReadBody(context);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("code").GetString());
        Assert.Equal("limit 1-200 arasında olmalıdır.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ARequestTheClientAbandoned_AndAStartedResponse_KeepTheDefaultHandling()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var abandoned = NewContext();
        abandoned.RequestAborted = aborted.Token;
        Assert.False(await new ApiExceptionHandler().TryHandleAsync(abandoned, new OperationCanceledException(aborted.Token), CancellationToken.None));

        var started = NewContext();
        started.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new StartedResponse());
        Assert.False(await new ApiExceptionHandler().TryHandleAsync(started, new InvalidOperationException("late"), CancellationToken.None));
    }

    private sealed class StartedResponse : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    [Theory]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(429, 503)]
    [InlineData(404, 404)]
    [InlineData(400, 422)]
    [InlineData(409, 422)]
    [InlineData(500, 502)]
    [InlineData(503, 502)]
    public void MarketplaceStatus_IsNeverPassedThroughAsThePanelsOwn(int marketplaceStatus, int panelStatus)
    {
        Assert.Equal(panelStatus, MarketplaceErrorStatus.ToPanelStatus(new AdapterError(AdapterErrorClass.Remote5xx, "X", "x", marketplaceStatus, null, null)));
    }

    [Theory]
    [InlineData(AdapterErrorClass.RateLimit, 503)]
    [InlineData(AdapterErrorClass.NotFound, 404)]
    [InlineData(AdapterErrorClass.Validation, 422)]
    [InlineData(AdapterErrorClass.TransientNetwork, 502)]
    [InlineData(AdapterErrorClass.Authentication, 502)]
    public void MarketplaceErrorWithoutStatus_IsMappedByItsClass(AdapterErrorClass errorClass, int panelStatus)
    {
        Assert.Equal(panelStatus, MarketplaceErrorStatus.ToPanelStatus(new AdapterError(errorClass, "X", "x", null, null, null)));
    }

    [Fact]
    public void MissingAdapterError_IsABadGateway()
    {
        var error = MarketplaceErrorStatus.ToServiceError(null);

        Assert.Equal(502, error.Status);
        Assert.Equal("ADAPTER_ERROR", error.Code);
    }

    [Theory]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(429, 503)]
    [InlineData(200, 502)]
    [InlineData(404, 404)]
    [InlineData(409, 409)]
    [InlineData(422, 422)]
    [InlineData(503, 503)]
    public void ServiceErrorsCarryingAMarketplaceStatus_KeepTheServicesOwnAnswers(int serviceStatus, int panelStatus)
    {
        var result = MarketplaceErrorStatus.ForPanel(ServiceResult<int>.Fail("PICKING_FAILED", "x", serviceStatus));

        Assert.Equal(panelStatus, result.Error!.Status);
        Assert.Equal("PICKING_FAILED", result.Error.Code);
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonElement> ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }
}
