using System.Net;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8 (wave-2 findings). The role path rule had a "billing" branch (ACCOUNTING may write) that no route
// matched; a billing write now meets the default like any unknown area. A write to a route no endpoint serves stays
// with the path rule (403 below administrator, 404 for owner and administrator): the path rule is the second gate that
// RoleAuthorizationMiddlewareTests pins for requests without endpoint metadata, and letting every role through it
// whenever no endpoint matched would remove that layer, so the 403 is kept on purpose.
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
    // A caller without a session gets the same 403 for an unknown write as for a known one: the answer tells it nothing.
    [PostgreSqlFact]
    public async Task AWriteToAnUnknownRouteAnswersLikeAKnownOneWithoutASession()
    {
        using var anonymous = fixture.Host!.ClientFromOwnNetwork();
        var token = await fixture.Host.CsrfAsync(anonymous);
        foreach (var path in new[] { "/api/v1/no-such-area/items", "/api/v1/billing/invoices", "/api/v1/settings/appearance" })
        {
            using var response = await anonymous.SendAsync(ApiTestHost.Json(HttpMethod.Post, path, token, new { }));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("PERMISSION_DENIED", (await ApiTestHost.ProblemAsync(response)).GetProperty("code").GetString());
        }

        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Accounting, ownNetwork: true);
        using (client)
        using (signIn)
        {
            // ACCOUNTING no longer passes the path rule under /billing (the removed branch).
            using var billing = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/billing/invoices", csrf, new { }));
            Assert.Equal(HttpStatusCode.Forbidden, billing.StatusCode);
        }
    }
}
