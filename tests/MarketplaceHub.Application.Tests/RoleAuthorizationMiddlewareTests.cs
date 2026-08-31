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
}
