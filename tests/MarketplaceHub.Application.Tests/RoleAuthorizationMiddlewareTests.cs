using System.Net;
using System.Security.Claims;
using MarketplaceHub.Api.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class RoleAuthorizationMiddlewareTests
{
    [Fact]
    public async Task WebhookPost_BypassesSessionRoleGate()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/hooks/11111111-1111-1111-1111-111111111111/token";
        var called = false;
        var middleware = new RoleAuthorizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.NotEqual((int)HttpStatusCode.Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnauthenticatedMutationOutsideWebhook_RemainsForbidden()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/orders/11111111-1111-1111-1111-111111111111/instant-process";
        var called = false;
        var middleware = new RoleAuthorizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/questions/11111111-1111-1111-1111-111111111111/answer")]
    [InlineData("/api/v1/questions/sync")]
    [InlineData("/api/v1/question-templates")]
    public async Task OperationsRole_CanManageMarketplaceQuestions(string path)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "OPERATIONS")], "test"))
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        var called = false;
        var middleware = new RoleAuthorizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("ACCOUNTING")]
    [InlineData("READONLY")]
    public async Task NonOperationalRoles_CannotMutateMarketplaceQuestions(string role)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"))
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/questions/sync";
        var called = false;
        var middleware = new RoleAuthorizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }
}
