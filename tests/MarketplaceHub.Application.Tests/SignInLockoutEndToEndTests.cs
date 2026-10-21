using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The sign-in limits against a real database and the real pipeline, with each request coming from a
// chosen address (X-Test-Remote-Ip): an attacker on other networks cannot keep the owner out, and wrong
// authenticator codes keep counting when the attacker signs in again with the right password.
public sealed class SignInLockoutFixture : IAsyncLifetime
{
    public const string Password = ApiEndToEndFixture.Password;
    public const string AddressHeader = "X-Test-Remote-Ip";
    private readonly string? connectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION");
    private ApiTestHost? host;

    public WebApplicationFactory<SessionAuthMiddleware>? Factory { get; private set; }
    public string Run { get; } = Guid.NewGuid().ToString("N")[..12];
    public Guid TenantId { get; } = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using (var migrate = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options))
            await migrate.Database.MigrateAsync();
        host = new ApiTestHost(connectionString);
        Factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, TestRemoteAddress>()));
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant { Id = TenantId, Code = $"lock-{Run}", DisplayName = $"Lockout {Run}", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    public async Task<string> CreateUserAsync(string name, bool authenticator)
    {
        await using var scope = Factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;
        var email = $"{name}-{Run}@example.invalid";
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = name, ForcePasswordChange = false, CreatedAt = now, UpdatedAt = now };
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
        var security = new UserSecurity { UserId = user.Id, TotpState = authenticator ? TotpState.Enabled : TotpState.Disabled };
        if (authenticator)
            security.ProtectedTotpSecret = AuthEndpoints.ProtectTotp(scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), user.Id, RandomNumberGenerator.GetBytes(20));
        db.UserSecurities.Add(security);
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = TenantId, UserId = user.Id, Role = MembershipRole.Administrator, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return email;
    }

    public async Task<(HttpClient Client, string Csrf)> ClientAsync()
    {
        var client = Factory!.CreateClient();
        using var response = await client.GetAsync("/api/v1/auth/csrf");
        response.EnsureSuccessStatusCode();
        return (client, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!);
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string csrf, string address, string path, object body)
    {
        using var request = ApiTestHost.Json(HttpMethod.Post, path, csrf, body);
        request.Headers.Add(AddressHeader, address);
        return await client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> SignInAsync(string email, string password, string address)
    {
        var (client, csrf) = await ClientAsync();
        using (client) return await PostAsync(client, csrf, address, "/api/v1/auth/login", new { email, password });
    }

    public Task DisposeAsync()
    {
        Factory?.Dispose();
        host?.Dispose();
        return Task.CompletedTask;
    }

    private sealed class TestRemoteAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Headers.TryGetValue(AddressHeader, out var value) && IPAddress.TryParse(value.ToString(), out var address))
                    context.Connection.RemoteIpAddress = address;
                await following(context);
            });
            next(app);
        };
    }
}

[Collection("postgres-integration")]
public sealed class SignInLockoutEndToEndTests(SignInLockoutFixture fixture) : IClassFixture<SignInLockoutFixture>
{
    private const string OwnerNetwork = "198.51.100.10";

    [PostgreSqlFact]
    public async Task AnAttackerOnOtherNetworksCannotKeepTheOwnerOut()
    {
        var email = await fixture.CreateUserAsync("owner", authenticator: false);
        using (var first = await fixture.SignInAsync(email, SignInLockoutFixture.Password, OwnerNetwork))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // The owner's open session, used later from a network the attacker also failed from.
        var (session, sessionCsrf) = await fixture.ClientAsync();
        using (session)
        {
            using (var opened = await SignInLockoutFixture.PostAsync(session, sessionCsrf, OwnerNetwork, "/api/v1/auth/login", new { email, password = SignInLockoutFixture.Password }))
                Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

            // Seven attacker networks, five wrong passwords each: every one of them gets blocked, and the
            // account passes both account-wide ceilings (30).
            for (var network = 1; network <= 7; network++)
                for (var attempt = 0; attempt < SignInThrottle.FailuresPerBlock; attempt++)
                {
                    using var wrong = await fixture.SignInAsync(email, "Wrong-Password#2026x", $"203.0.113.{network}");
                    Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
                }
            using (var once = await fixture.SignInAsync(email, "Wrong-Password#2026x", "203.0.113.50"))
                Assert.Equal(HttpStatusCode.Unauthorized, once.StatusCode);
            await using (var scope = fixture.Factory!.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True(await users.IsLockedOutAsync((await users.FindByEmailAsync(email))!));
            }

            // A blocked network gets the same answer as an unknown account, even with the right password.
            using (var blocked = await fixture.SignInAsync(email, SignInLockoutFixture.Password, "203.0.113.1"))
            using (var unknown = await fixture.SignInAsync($"nobody-{fixture.Run}@example.invalid", SignInLockoutFixture.Password, "203.0.113.60"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
                Assert.Equal("INVALID_CREDENTIALS", (await ApiTestHost.ProblemAsync(blocked)).GetProperty("code").GetString());
                Assert.Equal("INVALID_CREDENTIALS", (await ApiTestHost.ProblemAsync(unknown)).GetProperty("code").GetString());
            }
            // A network that failed once is refused while the account is under attack.
            using (var failedBefore = await fixture.SignInAsync(email, SignInLockoutFixture.Password, "203.0.113.50"))
                Assert.Equal(HttpStatusCode.Unauthorized, failedBefore.StatusCode);

            // The owner's network signed in before: it still gets in, and so does a network that never failed.
            using (var owner = await fixture.SignInAsync(email, SignInLockoutFixture.Password, OwnerNetwork))
                Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
            using (var travelling = await fixture.SignInAsync(email, SignInLockoutFixture.Password, "192.0.2.77"))
                Assert.Equal(HttpStatusCode.OK, travelling.StatusCode);

            // A password re-check inside the open session (starting an authenticator setup) is not ended by failures elsewhere.
            using var rechecked = await SignInLockoutFixture.PostAsync(session, sessionCsrf, "203.0.113.50", "/api/v1/auth/mfa/setup", new { password = SignInLockoutFixture.Password });
            Assert.Equal(HttpStatusCode.OK, rechecked.StatusCode);
        }
    }

    [PostgreSqlFact]
    public async Task WrongAuthenticatorCodesKeepCountingAcrossNewSignIns()
    {
        var email = await fixture.CreateUserAsync("mfa", authenticator: true);
        const string network = "203.0.113.200";
        var failures = 0;
        foreach (var codesThisSignIn in new[] { 3, 2 })
        {
            var (client, csrf) = await fixture.ClientAsync();
            using (client)
            {
                using (var signIn = await SignInLockoutFixture.PostAsync(client, csrf, network, "/api/v1/auth/login", new { email, password = SignInLockoutFixture.Password }))
                {
                    Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
                    Assert.Equal("MFA_CHALLENGE", (await signIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
                }
                for (var code = 0; code < codesThisSignIn; code++)
                {
                    using var wrong = await SignInLockoutFixture.PostAsync(client, csrf, network, "/api/v1/auth/mfa/challenge", new { code = "000000" });
                    failures++;
                    Assert.Contains(wrong.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized });
                    if (failures == SignInThrottle.FailuresPerBlock)
                        Assert.Equal("MFA_ATTEMPTS_EXCEEDED", (await ApiTestHost.ProblemAsync(wrong)).GetProperty("code").GetString());
                }
            }
        }

        // The fifth wrong code (over two sign-ins) blocked the network: the right password no longer helps.
        using var again = await fixture.SignInAsync(email, SignInLockoutFixture.Password, network);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", (await ApiTestHost.ProblemAsync(again)).GetProperty("code").GetString());
        // Another network still gets to the second step.
        using var elsewhere = await fixture.SignInAsync(email, SignInLockoutFixture.Password, "192.0.2.201");
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
    }
}
