using System.Data.Common;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The API's route table as the panel meets it, taken before the endpoint files were restructured (API-GUVENLIK-8)
// and asserted after: every mapped route with its role gate (RoleAuthorizationMiddleware), whether it refuses a
// request another site started (SameOriginFetchRequired), and what three probes get back from the real pipeline:
//   anon   no session (CSRF token sent);
//   owner  a signed-in OWNER with a second factor, no Idempotency-Key, body {} (shows which writes require the key);
//   ownerIfMatch  the same write with If-Match "v1" (reaches the key check behind a version check);
//   cross  a signed-in OWNER's GET started by another site (Sec-Fetch-Site: cross-site).
// The probe host has a database that fails on the first query, so a probe stops where the handler first reads.
// A restructuring that keeps the panel's contract keeps every line; a deliberate change updates the snapshot file
// (set MARKETPLACEHUB_UPDATE_ROUTE_SNAPSHOT=1 and run this test once).
public sealed class ApiRouteTableSnapshotTests(ApiRouteProbeHost fixture) : IClassFixture<ApiRouteProbeHost>
{
    private const string SnapshotFile = "ApiRouteTable.snapshot.txt";

    [Fact]
    public async Task RouteTableMatchesTheSnapshot()
    {
        var actual = await ApiRouteTable.BuildAsync(fixture.Host);
        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("MARKETPLACEHUB_UPDATE_ROUTE_SNAPSHOT") == "1")
        {
            await File.WriteAllLinesAsync(path, actual);
            return;
        }
        var expected = (await File.ReadAllLinesAsync(path)).Where(line => line.Length > 0).ToList();
        var missing = expected.Except(actual, StringComparer.Ordinal).ToList();
        var added = actual.Except(expected, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0 && added.Count == 0,
            "The route table changed.\nExpected (snapshot) lines not produced:\n" + string.Join("\n", missing) + "\nProduced lines not in the snapshot:\n" + string.Join("\n", added));
    }

    // The snapshot lives next to this file in the repository.
    private static string SnapshotPath([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, SnapshotFile);
}

public static class ApiRouteTable
{
    public const string ProbeRoleHeader = "X-Test-Probe-Role";
    public const string ProbeTenant = "6f1c2a3e-0000-4000-8000-0000000000aa";
    public const string ProbeUser = "6f1c2a3e-0000-4000-8000-0000000000bb";

    public static IReadOnlyList<RouteEndpoint> Endpoints(ApiTestHost host)
    {
        _ = host.CreateClient();
        return host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
    }

    public static IEnumerable<(string Method, string Pattern, RouteEndpoint Endpoint)> Routes(ApiTestHost host) =>
        Endpoints(host)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (method, "/" + endpoint.RoutePattern.RawText!.TrimStart('/'), endpoint)))
            .OrderBy(route => route.Item2, StringComparer.Ordinal).ThenBy(route => route.method, StringComparer.Ordinal);

    // A request path that matches the pattern: route values become a fixed id or a plain segment.
    public static string Sample(string pattern) =>
        Regex.Replace(Regex.Replace(pattern, @"\{\*\*[^}]+\}", "event/path"), @"\{[^}]+\}",
            match => match.Value.Contains(":guid", StringComparison.Ordinal) ? "6f1c2a3e-0000-4000-8000-000000000001" : "x");

    public static bool IsOpenPath(string pattern) =>
        !pattern.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(pattern, "^/api/v1/(auth|hooks|public)/", RegexOptions.IgnoreCase);

    // The gate's name as ApiRouteAuthorizationMatrixTests declares it.
    public static string GateName(IReadOnlySet<string>? roles, string pattern)
    {
        if (roles is null) return IsOpenPath(pattern) ? "Open" : "SignedIn";
        var sorted = string.Join(",", roles.OrderBy(role => role, StringComparer.Ordinal));
        return sorted switch
        {
            "ACCOUNTING,ADMINISTRATOR,OPERATIONS,OWNER" => "Working",
            "ADMINISTRATOR,OPERATIONS,OWNER" => "Operations",
            "ACCOUNTING,ADMINISTRATOR,OWNER" => "Accounting",
            "ADMINISTRATOR,OWNER" => "Elevated",
            "OWNER" => "Owner",
            _ => sorted
        };
    }

    public static async Task<List<string>> BuildAsync(ApiTestHost host)
    {
        var routes = Routes(host).ToList();
        var lines = new List<string>();
        foreach (var (method, pattern, endpoint) in routes)
        {
            var gate = GateName(RoleAuthorizationMiddleware.RolesFor(method, Sample(pattern)), pattern);
            var sameOrigin = endpoint.Metadata.GetMetadata<SameOriginFetchRequired>() is not null ? "yes" : "no";
            var line = new StringBuilder($"{method,-6} {pattern} | gate={gate} | sameOrigin={sameOrigin}");
            // Only the panel's API is probed; health, the hub and the internal routes are not part of its contract here.
            if (pattern.StartsWith("/api/v1/", StringComparison.Ordinal) && method != "*" && !pattern.StartsWith("/api/v1/hooks/", StringComparison.Ordinal))
            {
                line.Append(" | anon=").Append(await ProbeAsync(host, null, method, pattern));
                line.Append(" | owner=").Append(await ProbeAsync(host, "OWNER", method, pattern));
                if (method != "GET") line.Append(" | ownerIfMatch=").Append(await ProbeAsync(host, "OWNER", method, pattern, ifMatch: true));
                if (method == "GET") line.Append(" | cross=").Append(await ProbeAsync(host, "OWNER", method, pattern, crossSite: true));
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    private static async Task<string> ProbeAsync(ApiTestHost host, string? role, string method, string pattern, bool crossSite = false, bool ifMatch = false)
    {
        // A client of its own network and a fresh token for every probe: no rate limit carries over from one route to the
        // next, and a probed GET (the CSRF route itself) cannot have replaced the cookie.
        using var client = host.ClientFromOwnNetwork();
        if (role is not null) client.DefaultRequestHeaders.Add(ProbeRoleHeader, role);
        var csrf = method is "GET" or "HEAD" ? null : await host.CsrfAsync(client);
        var request = new HttpRequestMessage(new HttpMethod(method), Sample(pattern) + "?connectionId=6f1c2a3e-0000-4000-8000-000000000002&attributeId=6f1c2a3e-0000-4000-8000-000000000003");
        if (method is not ("GET" or "HEAD"))
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf!);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        if (crossSite) request.Headers.Add("Sec-Fetch-Site", "cross-site");
        if (ifMatch) request.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
        using (request)
        using (var response = await client.SendAsync(request))
        {
            var status = (int)response.StatusCode;
            if (response.Content.Headers.ContentType?.MediaType != ApiProblems.ContentType) return status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            return $"{status} {problem.GetProperty("code").GetString()}";
        }
    }
}

// The real API pipeline with a database that fails on the first command, and a test-only way to be a signed-in
// user of a tenant without a session row (ApiRouteTable.ProbeRoleHeader). The session middleware leaves the user
// alone when no session cookie is sent.
public sealed class ApiRouteProbeHost : IDisposable
{
    public ApiTestHost Host { get; } = new ProbeApiTestHost();

    public void Dispose() => Host.Dispose();

    private sealed class ProbeApiTestHost() : ApiTestHost("Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Password=none;Timeout=1")
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(new NoDatabase()));
                services.AddTransient<IStartupFilter, ProbeUserStartupFilter>();
            });
        }
    }

    private sealed class NoDatabase : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            throw new InvalidOperationException("The route probe host has no database.");

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The route probe host has no database.");
    }

    private sealed class ProbeUserStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, call) =>
            {
                if (context.Request.Headers.TryGetValue(ApiRouteTable.ProbeRoleHeader, out var role))
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, ApiRouteTable.ProbeUser), new Claim("tenant_id", ApiRouteTable.ProbeTenant),
                        new Claim(ClaimTypes.Role, role.ToString()), new Claim(SessionAuthMiddleware.MfaClaim, "enabled")
                    ], "RouteProbe"));
                }
                await call();
            });
            next(app);
        };
    }
}
