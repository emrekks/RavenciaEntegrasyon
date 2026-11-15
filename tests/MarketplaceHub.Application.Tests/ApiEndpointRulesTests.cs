using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8: each /api/v1 endpoint declares its rules as metadata (RequiredGate, IdempotencyKeyRequired,
// LiveMarketplaceReadMetadata) instead of leaving them to the request path's text or to which file it is in. These
// tests hold the declarations equal to what the code decides today, route by route.
public sealed class ApiEndpointRulesTests(ApiRouteProbeHost fixture) : IClassFixture<ApiRouteProbeHost>
{
    private IEnumerable<(string Method, string Pattern, Microsoft.AspNetCore.Routing.RouteEndpoint Endpoint)> PanelRoutes() =>
        ApiRouteTable.Routes(fixture.Host).Where(route => route.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal));

    // The declared gate equals the path rule's (RoleAuthorizationMiddleware.RolesFor) for every route; the middleware
    // applies both, so a route whose declaration were looser would still meet the path rule.
    [Fact]
    public void EveryRouteDeclaresTheGateThePathRuleGivesIt()
    {
        var wrong = new List<string>();
        foreach (var (method, pattern, endpoint) in PanelRoutes())
        {
            var declared = endpoint.Metadata.GetMetadata<RequiredGate>();
            var pathGate = ApiRouteTable.GateName(RoleAuthorizationMiddleware.RolesFor(method, ApiRouteTable.Sample(pattern)), pattern);
            if (declared is null) wrong.Add($"{method} {pattern}: no gate declared (path rule: {pathGate})");
            else if (declared.Gate.ToString() != pathGate) wrong.Add($"{method} {pattern}: declared {declared.Gate}, path rule {pathGate}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // Every write that refuses a request without an Idempotency-Key says so, and none that does not: the route-table
    // snapshot records which writes answer IDEMPOTENCY_KEY_REQUIRED to a signed-in owner without a key. The probe's
    // body ({}) and query cannot reach two handlers (a list body, a required productId); their source checks the key
    // and they are listed here.
    private static readonly string[] DeclaredBeyondTheProbe =
    [
        "DELETE /api/v1/files/product-media",
        "POST /api/v1/catalog/attributes/{id:guid}/values",
    ];

    [Fact]
    public void EveryWriteThatRequiresAnIdempotencyKeyDeclaresIt()
    {
        var lines = SnapshotLines().ToList();
        var probed = lines
            .Where(line => line.Contains("owner=400 IDEMPOTENCY_KEY_REQUIRED", StringComparison.Ordinal) || line.Contains("ownerIfMatch=400 IDEMPOTENCY_KEY_REQUIRED", StringComparison.Ordinal))
            .Select(RouteOf).ToHashSet();
        // Each listed exception is a write the probe stopped at binding, before any handler code.
        Assert.All(DeclaredBeyondTheProbe, route => Assert.Contains(lines, line => RouteOf(line) == route && line.Contains("owner=400 INVALID_REQUEST_BODY", StringComparison.Ordinal)));
        probed.UnionWith(DeclaredBeyondTheProbe);
        var declared = PanelRoutes().Where(route => route.Endpoint.Metadata.GetMetadata<IdempotencyKeyRequired>() is not null)
            .Select(route => $"{route.Method} {route.Pattern}").ToHashSet();
        var undeclared = probed.Except(declared).Order(StringComparer.Ordinal).ToList();
        var unchecked_ = declared.Except(probed).Order(StringComparer.Ordinal).ToList();
        Assert.True(undeclared.Count == 0 && unchecked_.Count == 0,
            "Require the key but do not declare it (.RequireIdempotencyKey()):\n" + string.Join("\n", undeclared)
            + "\nDeclare it but the probe did not meet the check:\n" + string.Join("\n", unchecked_));
    }

    // The live marketplace reads are the routes the path rule names (RoleAuthorizationMiddleware.IsLiveMarketplaceRead);
    // each declares itself one, which gives it the Working gate and the same-origin filter.
    [Fact]
    public void EveryLiveMarketplaceReadDeclaresIt()
    {
        var wrong = PanelRoutes()
            .Where(route => (route.Method == "GET" && RoleAuthorizationMiddleware.IsLiveMarketplaceRead(ApiRouteTable.Sample(route.Pattern)))
                != (route.Endpoint.Metadata.GetMetadata<LiveMarketplaceReadMetadata>() is not null))
            .Select(route => $"{route.Method} {route.Pattern}").ToList();
        Assert.True(wrong.Count == 0, "Live read rule and declaration differ:\n" + string.Join("\n", wrong));
        Assert.All(PanelRoutes().Where(route => route.Endpoint.Metadata.GetMetadata<LiveMarketplaceReadMetadata>() is not null), route =>
        {
            Assert.NotNull(route.Endpoint.Metadata.GetMetadata<SameOriginFetchRequired>());
            Assert.Equal(ApiGate.Working, route.Endpoint.Metadata.GetMetadata<RequiredGate>()!.Gate);
        });
    }

    // The role gate applies the endpoint's declared gate as well as the path rule: a declaration is never decoration.
    [Theory]
    [InlineData("OPERATIONS", ApiGate.Elevated, 403)]
    [InlineData("ADMINISTRATOR", ApiGate.Elevated, 200)]
    [InlineData("ACCOUNTING", ApiGate.Operations, 403)]
    [InlineData("READONLY", ApiGate.SignedIn, 200)]
    public async Task TheRoleGateAppliesTheDeclaredGate(string role, ApiGate gate, int status)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        context.Request.Method = "GET";
        // A read the path rule leaves to every signed-in role.
        context.Request.Path = "/api/v1/settings/appearance";
        context.Response.Body = new MemoryStream();
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)], "Test"));
        Microsoft.AspNetCore.Http.EndpointHttpContextExtensions.SetEndpoint(context, new Microsoft.AspNetCore.Http.Endpoint(_ => Task.CompletedTask,
            new Microsoft.AspNetCore.Http.EndpointMetadataCollection(RequiredGate.For(gate)), "probe"));
        var reached = false;

        await new RoleAuthorizationMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.Equal(status == 200, reached);
        Assert.Equal(status, context.Response.StatusCode);
    }

    private static string RouteOf(string snapshotLine)
    {
        var parts = snapshotLine.Split('|')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return $"{parts[0]} {parts[1]}";
    }

    private static IEnumerable<string> SnapshotLines([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        File.ReadAllLines(Path.Combine(Path.GetDirectoryName(source)!, "ApiRouteTable.snapshot.txt"));
}
