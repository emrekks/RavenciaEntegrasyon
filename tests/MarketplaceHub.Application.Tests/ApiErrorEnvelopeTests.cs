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

    [Fact]
    public async Task OtherExceptions_KeepTheDefaultHandling()
    {
        var handled = await new ApiExceptionHandler().TryHandleAsync(NewContext(), new InvalidOperationException("boom"), CancellationToken.None);

        Assert.False(handled);
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
