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
    [InlineData("/api/v1/questions/11111111-1111-1111-1111-111111111111/report")]
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

    [Theory]
    [InlineData("PUT", "/api/v1/connections/11111111-1111-1111-1111-111111111111/credential")]
    [InlineData("PUT", "/api/v1/connections/11111111-1111-1111-1111-111111111111/credential/")]
    [InlineData("PUT", "/api/v1/connections/11111111-1111-1111-1111-111111111111//credential")]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/deep-delete/")]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/data-reset")]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/full-reset/")]
    [InlineData("PUT", "/api/v1/connections/11111111-1111-1111-1111-111111111111/data-visibility/")]
    [InlineData("POST", "/api/v1/connections")]
    [InlineData("POST", "/api/v1/connections/")]
    [InlineData("PUT", "/api/v1/connections/11111111-1111-1111-1111-111111111111/capabilities/ORDER_READ/evidence/")]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/instant-notifications/")]
    [InlineData("POST", "//api/v1/jobs/11111111-1111-1111-1111-111111111111/retry")]
    [InlineData("POST", "/api/v1/settings/data-reset")]
    public async Task OperationsRole_CannotReachAdministratorRoutesWithAnySlashSpelling(string method, string path)
    {
        var (called, status) = await Invoke("OPERATIONS", method, path);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Theory]
    [InlineData("PATCH", "/api/v1/connections/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/sync-policies/ORDERS/runs")]
    [InlineData("POST", "/api/v1/shipments/11111111-1111-1111-1111-111111111111/labels")]
    [InlineData("POST", "/api/v1/products/")]
    public async Task OperationsRole_KeepsDailyWorkRoutes(string method, string path)
    {
        var (called, _) = await Invoke("OPERATIONS", method, path);

        Assert.True(called);
    }

    [Theory]
    [InlineData("OPERATIONS", "POST", "/api/v1/orders/11111111-1111-1111-1111-111111111111/instant-process", true)]
    [InlineData("OPERATIONS", "POST", "/api/v1/orders/11111111-1111-1111-1111-111111111111/packages", true)]
    [InlineData("OPERATIONS", "PUT", "/api/v1/orders/11111111-1111-1111-1111-111111111111/shopify-status", true)]
    [InlineData("OPERATIONS", "POST", "/api/v1/orders/shopify-csv-import", true)]
    [InlineData("ACCOUNTING", "POST", "/api/v1/orders/11111111-1111-1111-1111-111111111111/instant-process", false)]
    [InlineData("READONLY", "POST", "/api/v1/orders/11111111-1111-1111-1111-111111111111/instant-process", false)]
    [InlineData("OPERATIONS", "POST", "/api/v1/invoice-workspace/confirm", true)]
    [InlineData("ACCOUNTING", "POST", "/api/v1/invoice-workspace/confirm", true)]
    [InlineData("ACCOUNTING", "PUT", "/api/v1/invoice-workspace/manual-status", true)]
    [InlineData("READONLY", "POST", "/api/v1/invoice-workspace/confirm", false)]
    [InlineData("READONLY", "POST", "/api/v1/invoice-workspace/preview", false)]
    // Fiscal overrides stay with administrators (G21).
    [InlineData("OPERATIONS", "POST", "/api/v1/invoice-workspace/orders/11111111-1111-1111-1111-111111111111/replace-cancelled-invoice", false)]
    [InlineData("ACCOUNTING", "POST", "/api/v1/invoice-workspace/orders/11111111-1111-1111-1111-111111111111/confirm-no-remote-invoice", false)]
    [InlineData("OPERATIONS", "POST", "/api/v1/invoice-workspace/orders/11111111-1111-1111-1111-111111111111/confirm-marketplace-delivered/", false)]
    [InlineData("ADMINISTRATOR", "POST", "/api/v1/invoice-workspace/orders/11111111-1111-1111-1111-111111111111/replace-cancelled-invoice", true)]
    [InlineData("OWNER", "POST", "/api/v1/invoice-workspace/orders/11111111-1111-1111-1111-111111111111/confirm-no-remote-invoice", true)]
    // Recording a printed label is open to every working role (G21).
    [InlineData("ACCOUNTING", "POST", "/api/v1/orders/11111111-1111-1111-1111-111111111111/label-printed", true)]
    [InlineData("OPERATIONS", "POST", "/api/v1/orders/11111111-1111-1111-1111-111111111111/label-printed", true)]
    [InlineData("READONLY", "POST", "/api/v1/orders/11111111-1111-1111-1111-111111111111/label-printed", false)]
    // Webhook subscriptions hold the verifier secret (A22).
    [InlineData("OPERATIONS", "POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/webhooks", false)]
    [InlineData("ADMINISTRATOR", "POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/webhooks", true)]
    // Revoking one is as elevated as creating it, whatever the trailing subscription id.
    [InlineData("OPERATIONS", "DELETE", "/api/v1/connections/11111111-1111-1111-1111-111111111111/webhooks/22222222-2222-2222-2222-222222222222", false)]
    [InlineData("OPERATIONS", "DELETE", "/api/v1/connections/11111111-1111-1111-1111-111111111111/Webhooks/22222222-2222-2222-2222-222222222222/", false)]
    [InlineData("OWNER", "DELETE", "/api/v1/connections/11111111-1111-1111-1111-111111111111/webhooks/22222222-2222-2222-2222-222222222222", true)]
    public async Task OrderAndInvoiceWorkspaceWrites_FollowTheirWorkingRoles(string role, string method, string path, bool reachable)
    {
        var (called, status) = await Invoke(role, method, path);

        Assert.Equal(reachable, called);
        if (!reachable) Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Theory]
    [InlineData("READONLY", "/api/v1/invoices/11111111-1111-1111-1111-111111111111/documents/latest/content", false)]
    [InlineData("ACCOUNTING", "/api/v1/invoices/11111111-1111-1111-1111-111111111111/documents/latest/content", true)]
    [InlineData("READONLY", "/api/v1/shipments/11111111-1111-1111-1111-111111111111/documents/22222222-2222-2222-2222-222222222222/content", false)]
    [InlineData("OPERATIONS", "/api/v1/shipments/11111111-1111-1111-1111-111111111111/documents/22222222-2222-2222-2222-222222222222/content", true)]
    [InlineData("READONLY", "/api/v1/orders/11111111-1111-1111-1111-111111111111/finance", false)]
    [InlineData("READONLY", "/api/v1/connections/11111111-1111-1111-1111-111111111111/instant-notifications/", false)]
    [InlineData("ACCOUNTING", "/api/v1/returns/11111111-1111-1111-1111-111111111111/rejection-reasons", true)]
    [InlineData("READONLY", "/api/v1/orders", true)]
    [InlineData("READONLY", "/api/v1/files/product-media/11111111-1111-1111-1111-111111111111/content", true)]
    [InlineData("", "/api/v1/public/invoice-documents/token/content", true)]
    public async Task ReadsOfDocumentsAndLiveMarketplaceCallsNeedAWorkingRole(string role, string path, bool reachable)
    {
        var (called, _) = await Invoke(role, "GET", path);

        Assert.Equal(reachable, called);
    }

    // Only reads of public document links pass without a session (A23).
    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    public async Task PublicLinksAreOpenForReadingOnly(string method, bool reachable)
    {
        var (called, status) = await Invoke("", method, "/api/v1/public/invoice-documents/token/content");

        Assert.Equal(reachable, called);
        if (!reachable) Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task AdministratorRole_ReachesCredentialRoute()
    {
        var (called, _) = await Invoke("ADMINISTRATOR", "PUT", "/api/v1/connections/11111111-1111-1111-1111-111111111111/credential/");

        Assert.True(called);
    }

    private static async Task<(bool Called, int Status)> Invoke(string role, string method, string path)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"))
        };
        context.Request.Method = method;
        context.Request.Path = path;
        var called = false;
        var middleware = new RoleAuthorizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);
        return (called, context.Response.StatusCode);
    }
}
