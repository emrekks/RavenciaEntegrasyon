using System.Net;
using System.Text.RegularExpressions;
using MarketplaceHub.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-7: a read that calls a marketplace live spends its quota, and the session cookie is SameSite=Lax, so a
// link on another site must not start one. Every route the role gate counts as a live read carries the same-origin
// filter, and that filter refuses a cross-site request before the handler runs.
public sealed class LiveReadSameOriginTests(ApiPipelineHost fixture) : IClassFixture<ApiPipelineHost>
{
    private List<(string Pattern, bool SameOriginOnly)> LiveReads()
    {
        _ = fixture.Host.CreateClient();
        return fixture.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Select(endpoint => (Pattern: "/" + endpoint.RoutePattern.RawText!.TrimStart('/'), SameOriginOnly: endpoint.Metadata.GetMetadata<SameOriginFetchRequired>() is not null))
            .Where(route => RoleAuthorizationMiddleware.IsLiveMarketplaceRead(Sample(route.Pattern)))
            .ToList();
    }

    [Fact]
    public void EveryLiveMarketplaceRead_RefusesAnotherSitesRequest()
    {
        var reads = LiveReads();

        Assert.Contains(reads, route => route.Pattern.EndsWith("/audits", StringComparison.Ordinal));
        var unguarded = reads.Where(route => !route.SameOriginOnly).Select(route => route.Pattern).ToList();
        Assert.True(unguarded.Count == 0, "Live reads without RequireSameOriginFetch:\n" + string.Join("\n", unguarded));
    }

    [Theory]
    [InlineData("/api/v1/returns/6f1c2a3e-0000-4000-8000-000000000001/audits", true)]
    [InlineData("/api/v1/orders/6f1c2a3e-0000-4000-8000-000000000001/finance", true)]
    [InlineData("/api/v1/returns/6f1c2a3e-0000-4000-8000-000000000001", false)]
    [InlineData("/api/v1/questions/6f1c2a3e-0000-4000-8000-000000000001/customer-history", false)]
    public void TheLiveReadRuleKnowsItsRoutes(string path, bool live) => Assert.Equal(live, RoleAuthorizationMiddleware.IsLiveMarketplaceRead(path));

    [Fact]
    public void AnotherSitesRequestIsRecognised_ThePanelsOwnAndNonBrowsersAreNot()
    {
        static HttpRequest Request(string? site)
        {
            var context = new DefaultHttpContext();
            if (site is not null) context.Request.Headers["Sec-Fetch-Site"] = site;
            return context.Request;
        }

        Assert.True(FetchMetadata.IsStartedByAnotherSite(Request("cross-site")));
        Assert.True(FetchMetadata.IsStartedByAnotherSite(Request("same-site")));
        Assert.False(FetchMetadata.IsStartedByAnotherSite(Request("same-origin")));
        Assert.False(FetchMetadata.IsStartedByAnotherSite(Request("none")));
        Assert.False(FetchMetadata.IsStartedByAnotherSite(Request(null)));
    }

    private static string Sample(string pattern) =>
        Regex.Replace(Regex.Replace(pattern, @"\{\*\*[^}]+\}", "event/path"), @"\{[^}]+\}",
            match => match.Value.Contains(":guid", StringComparison.Ordinal) ? "6f1c2a3e-0000-4000-8000-000000000001" : "x");
}
