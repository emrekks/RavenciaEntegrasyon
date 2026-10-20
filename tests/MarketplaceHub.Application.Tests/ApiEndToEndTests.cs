using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Api.Realtime;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The real API pipeline (middleware order, cookies, CSRF, role gate, idempotency) in memory. The worker
// project also exposes a top-level Program to this assembly, so the factory is anchored on a type of the
// API assembly instead of Program.
public sealed class ApiTestHost(string connectionString) : WebApplicationFactory<SessionAuthMiddleware>
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "marketplacehub-api-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:AppDb", connectionString);
        builder.UseSetting("Security:CredentialKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        // Plain-HTTP cookie names and no key-encryption certificate, as on a developer machine.
        builder.UseSetting("MARKETPLACEHUB_ENVIRONMENT", "PILOT_LOCAL");
        builder.UseSetting("Storage:Root", Path.Combine(root, "files"));
        builder.UseSetting("DataProtection:KeysRoot", Path.Combine(root, "keys"));
        builder.UseSetting("FeatureFlags:ExternalWrites", "false");
        builder.ConfigureTestServices(services =>
        {
            // Background loops are not part of the request pipeline under test.
            foreach (var hosted in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && (descriptor.ImplementationType == typeof(OperationsRealtimeBroadcaster) || descriptor.ImplementationType == typeof(ApiIdempotencyRetentionService)
                    || descriptor.ImplementationType == typeof(MarketplaceHub.Api.Operations.PurgedFileRetentionService))).ToList())
                services.Remove(hosted);
        });
    }

    public async Task<string> CsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    public static HttpRequestMessage Json(HttpMethod method, string path, string? csrf, object? body = null, string? rawBody = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (rawBody is not null) request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        else if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(ApiProblems.ContentType, response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

// Needs no database: every request here is answered before the first query.
public sealed class ApiPipelineHost() : IDisposable
{
    public ApiTestHost Host { get; } = new("Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Password=none;Timeout=1");
    public void Dispose() => Host.Dispose();
}

public sealed class ApiPipelineTests(ApiPipelineHost fixture) : IClassFixture<ApiPipelineHost>
{
    [Fact]
    public async Task CsrfToken_IsIssuedInAnHttpOnlyLaxCookieWithSecurityHeaders()
    {
        using var client = fixture.Host.CreateClient();

        using var response = await client.GetAsync("/api/v1/auth/csrf");

        response.EnsureSuccessStatusCode();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(RequestSecurityMiddleware.PilotLocalCsrfCookie + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task MutationWithoutCsrf_IsRefusedBeforeAnythingElse()
    {
        using var client = fixture.Host.CreateClient();

        using var response = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf: null, new { email = "a@example.invalid", password = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("REQUEST_VERIFICATION_FAILED", (await ApiTestHost.ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SignInWithoutFields_IsABadRequestNotAServerError()
    {
        using var client = fixture.Host.CreateClient();
        var csrf = await fixture.Host.CsrfAsync(client);

        using var response = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf, rawBody: "{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", (await ApiTestHost.ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MalformedJson_IsABadRequestInTheSharedShape()
    {
        using var client = fixture.Host.CreateClient();
        var csrf = await fixture.Host.CsrfAsync(client);

        using var response = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf, rawBody: "{\"email\":"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiTestHost.ProblemAsync(response);
        Assert.Equal("INVALID_REQUEST_BODY", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task MutationWithoutASession_IsStoppedByTheRoleGate()
    {
        using var client = fixture.Host.CreateClient();
        var csrf = await fixture.Host.CsrfAsync(client);
        var request = ApiTestHost.Json(HttpMethod.Post, $"/api/v1/orders/{Guid.NewGuid():D}/instant-process", csrf);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("PERMISSION_DENIED", (await ApiTestHost.ProblemAsync(response)).GetProperty("code").GetString());
    }

    // Without a session an auth read is "signed out" (the panel shows the sign-in screen), not "forbidden".
    [Fact]
    public async Task AuthReadsWithoutASession_AnswerAuthenticationRequired()
    {
        using var client = fixture.Host.CreateClient();

        using var response = await client.GetAsync("/api/v1/auth/security-status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("AUTHENTICATION_REQUIRED", (await ApiTestHost.ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SecondFactorAttempts_HaveTheirOwnLimit()
    {
        using var client = fixture.Host.CreateClient();
        var csrf = await fixture.Host.CsrfAsync(client);
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt <= RateLimitPolicies.StepsPerMinute; attempt++)
        {
            using var response = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/challenge", csrf, new { code = "000000" }));
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(RateLimitPolicies.StepsPerMinute), status => Assert.Equal(HttpStatusCode.Unauthorized, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Theory]
    [InlineData("client-op-123", true)]
    [InlineData("bad value with spaces", false)]
    public async Task ACorrelationIdIsKeptOnlyWhenItIsAPlainToken(string sent, bool kept)
    {
        using var client = fixture.Host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", sent);

        using var response = await client.SendAsync(request);

        Assert.Equal(kept, response.Headers.GetValues("X-Correlation-ID").Single() == sent);
    }

    [Fact]
    public async Task Liveness_NeedsNoDatabase()
    {
        using var client = fixture.Host.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

// Sign-in, CSRF, a keyed mutation and its replay, and the role matrix against a real PostgreSQL
// database. Runs where MARKETPLACEHUB_TEST_CONNECTION points at a dedicated test database (CI does).
public sealed class ApiEndToEndFixture : IAsyncLifetime
{
    public const string Password = "Ravencia-Test-Pass#2026";
    private readonly string? connectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION");

    public ApiTestHost? Host { get; private set; }
    public string Run { get; } = Guid.NewGuid().ToString("N")[..12];
    public Guid TenantId { get; } = Guid.CreateVersion7();

    public string Email(MembershipRole role) => $"{role.ToString().ToLowerInvariant()}-{Run}@example.invalid";

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using (var migrate = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options))
            await migrate.Database.MigrateAsync();

        Host = new ApiTestHost(connectionString);
        await using var scope = Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant { Id = TenantId, Code = $"e2e-{Run}", DisplayName = $"E2E {Run}", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        foreach (var role in new[] { MembershipRole.Administrator, MembershipRole.Operations, MembershipRole.ReadOnly })
        {
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = Email(role), Email = Email(role), DisplayName = $"E2E {role}", ForcePasswordChange = false, CreatedAt = now, UpdatedAt = now };
            var created = await users.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
            db.UserSecurities.Add(new UserSecurity { UserId = user.Id, TotpState = TotpState.Disabled });
            db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = TenantId, UserId = user.Id, Role = role, CreatedAt = now, UpdatedAt = now });
        }
        await db.SaveChangesAsync();
    }

    public async Task<(HttpClient Client, string Csrf, HttpResponseMessage SignIn)> SignInAsync(MembershipRole role)
    {
        var client = Host!.CreateClient();
        var csrf = await Host.CsrfAsync(client);
        var signIn = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf, new { email = Email(role), password = Password }));
        return (client, csrf, signIn);
    }

    public Task DisposeAsync()
    {
        Host?.Dispose();
        return Task.CompletedTask;
    }
}

[Collection("postgres-integration")]
public sealed class ApiEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task SignIn_SetsAnHttpOnlyLaxSessionCookieAndOpensAnActiveSession()
    {
        var (client, _, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (client)
        using (signIn)
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            Assert.Equal("ACTIVE", (await signIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
            var cookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie"), value => value.StartsWith(SessionAuthMiddleware.PilotLocalCookieName + "=", StringComparison.Ordinal));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

            using var me = await client.GetAsync("/api/v1/auth/me");
            Assert.Equal("ADMINISTRATOR", (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
        }
    }

    [PostgreSqlFact]
    public async Task KeyedMutation_IsReplayedWithItsBodyAndETag_AndTheKeyCannotBeReused()
    {
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (client)
        using (signIn)
        {
            var key = Guid.NewGuid().ToString();
            var settings = new { printer = $"zebra-{fixture.Run}" };

            using var first = await client.SendAsync(Keyed(HttpMethod.Put, "/api/v1/settings/shipping-label", csrf, key, settings));
            using var replay = await client.SendAsync(Keyed(HttpMethod.Put, "/api/v1/settings/shipping-label", csrf, key, settings));
            using var reused = await client.SendAsync(Keyed(HttpMethod.Put, "/api/v1/settings/shipping-label", csrf, key, new { printer = "other" }));

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.NotNull(first.Headers.ETag);
            Assert.False(first.Headers.Contains("Idempotency-Replayed"));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
            Assert.Equal(first.Headers.ETag, replay.Headers.ETag);
            Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
            Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await ApiTestHost.ProblemAsync(reused)).GetProperty("code").GetString());

            var stale = Keyed(HttpMethod.Put, "/api/v1/settings/shipping-label", csrf, Guid.NewGuid().ToString(), settings);
            stale.Headers.TryAddWithoutValidation("If-Match", "\"v999999\"");
            using var staleResponse = await client.SendAsync(stale);
            Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        }
    }

    // A record written before keys were scoped to the user (raw key) whose outcome is unknown still stops
    // a blind replay of the same key.
    [PostgreSqlFact]
    public async Task AnUnfinishedRecordUnderTheOldKeyFormat_StillBlocksTheReplay()
    {
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (client)
        using (signIn)
        {
            var key = Guid.NewGuid().ToString();
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTimeOffset.UtcNow;
                db.ApiIdempotencyRecords.Add(new ApiIdempotencyRecord { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, RouteTemplate = "/api/v1/settings/shipping-label", IdempotencyKey = key, RequestHash = "legacy-sha256", State = "UNKNOWN", CreatedAt = now.AddHours(-1), ExpiresAt = now.AddDays(6) });
                await db.SaveChangesAsync();
            }

            using var replay = await client.SendAsync(Keyed(HttpMethod.Put, "/api/v1/settings/shipping-label", csrf, key, new { printer = $"legacy-{fixture.Run}" }));

            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Equal("IDEMPOTENCY_RESULT_UNKNOWN", (await ApiTestHost.ProblemAsync(replay)).GetProperty("code").GetString());
        }
    }

    [PostgreSqlFact]
    public async Task RoleMatrix_ReadOnlyReadsOnly_OperationsWorksOrdersButNotSettings()
    {
        var (readOnly, readOnlyCsrf, readOnlySignIn) = await fixture.SignInAsync(MembershipRole.ReadOnly);
        var (operations, operationsCsrf, operationsSignIn) = await fixture.SignInAsync(MembershipRole.Operations);
        using (readOnly)
        using (readOnlySignIn)
        using (operations)
        using (operationsSignIn)
        {
            Assert.Equal(HttpStatusCode.OK, readOnlySignIn.StatusCode);
            Assert.Equal(HttpStatusCode.OK, operationsSignIn.StatusCode);
            var order = $"/api/v1/orders/{Guid.NewGuid():D}/instant-process";

            using var readOnlyList = await readOnly.GetAsync("/api/v1/orders");
            using var readOnlyPicking = await readOnly.SendAsync(Keyed(HttpMethod.Post, order, readOnlyCsrf, Guid.NewGuid().ToString()));
            using var readOnlySettings = await readOnly.SendAsync(Keyed(HttpMethod.Put, "/api/v1/settings/shipping-label", readOnlyCsrf, Guid.NewGuid().ToString(), new { printer = "x" }));
            using var operationsPicking = await operations.SendAsync(Keyed(HttpMethod.Post, order, operationsCsrf, Guid.NewGuid().ToString()));
            using var operationsSettings = await operations.SendAsync(Keyed(HttpMethod.Put, "/api/v1/settings/shipping-label", operationsCsrf, Guid.NewGuid().ToString(), new { printer = "x" }));

            Assert.Equal(HttpStatusCode.OK, readOnlyList.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, readOnlyPicking.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, readOnlySettings.StatusCode);
            // Past the role gate: the unknown order is the endpoint's own answer.
            Assert.NotEqual(HttpStatusCode.Forbidden, operationsPicking.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, operationsPicking.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, operationsSettings.StatusCode);
            Assert.Equal("PERMISSION_DENIED", (await ApiTestHost.ProblemAsync(operationsSettings)).GetProperty("code").GetString());
        }
    }

    [PostgreSqlFact]
    public async Task WrongPassword_IsRefusedAndAudited()
    {
        using var client = fixture.Host!.CreateClient();
        var csrf = await fixture.Host.CsrfAsync(client);

        using var response = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf, new { email = fixture.Email(MembershipRole.Operations), password = "Wrong-Password#2026" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var scope = fixture.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(user => user.Email == fixture.Email(MembershipRole.Operations)).Select(user => user.Id).SingleAsync();
        Assert.True(await db.AuditLogs.AnyAsync(entry => entry.ActorUserId == userId && entry.Action == AuthAudit.LoginFailed && entry.Reason.StartsWith("wrong-password")));
    }

    private static HttpRequestMessage Keyed(HttpMethod method, string path, string csrf, string key, object? body = null)
    {
        var request = ApiTestHost.Json(method, path, csrf, body);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }
}
