using System.Net;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8 (wave-2 findings): the role path rule had a "billing" branch (ACCOUNTING may write) that no route
// matched; a billing write now meets the default like any unknown area. And a write to a route no endpoint serves
// answered a signed-in caller below owner or administrator 403 (the default rule is Elevated) and an owner 404; every
// signed-in caller now gets the router's 404, while a caller without a session still gets 403, as on a known route.
public sealed class RoleGateUnknownRouteTests
{
    [Fact]
    public void NoRouteIsUnderBilling_AndABillingWriteMeetsTheDefaultRule()
    {
        var snapshot = File.ReadAllLines(Path.Combine(TestRepository.TestProject(), "ApiRouteTable.snapshot.txt"));
        Assert.DoesNotContain(snapshot, line => line.Contains("/api/v1/billing", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Elevated", ApiRouteTable.GateName(RoleAuthorizationMiddleware.RolesFor("POST", "/api/v1/billing/invoices"), "/api/v1/billing/invoices"));
        // No invoicing route is accounting-only: invoice work is Working, issuing and cancelling Elevated.
        Assert.DoesNotContain(snapshot, line => line.Contains("gate=Accounting", StringComparison.Ordinal));
    }
}

[Collection("postgres-integration")]
public sealed class RoleGateUnknownRouteEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task AWriteToAnUnknownRouteIs404ForASignedInCaller_And403WithoutASession()
    {
        foreach (var role in new[] { MembershipRole.Operations, MembershipRole.ReadOnly, MembershipRole.Owner })
        {
            var (client, csrf, signIn) = await fixture.SignInAsync(role, ownNetwork: true);
            using (client)
            using (signIn)
            {
                Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
                using var unknown = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/no-such-area/items", csrf, new { }));
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
                using var billing = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/billing/invoices", csrf, new { }));
                Assert.Equal(HttpStatusCode.NotFound, billing.StatusCode);
                // A known route keeps its gate: settings writes are the owner's and administrators'.
                using var known = await client.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/appearance", csrf, new { }));
                if (role != MembershipRole.Owner) Assert.Equal(HttpStatusCode.Forbidden, known.StatusCode);
            }
        }

        using var anonymous = fixture.Host!.ClientFromOwnNetwork();
        var token = await fixture.Host.CsrfAsync(anonymous);
        foreach (var path in new[] { "/api/v1/no-such-area/items", "/api/v1/settings/appearance" })
        {
            using var response = await anonymous.SendAsync(ApiTestHost.Json(HttpMethod.Post, path, token, new { }));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("PERMISSION_DENIED", (await ApiTestHost.ProblemAsync(response)).GetProperty("code").GetString());
        }
    }
}
