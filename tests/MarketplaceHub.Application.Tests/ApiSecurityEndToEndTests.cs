using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The authenticator lifecycle (A1, A2, A24), irreversible work without a step-up (removed 21 Oct 2026) and the password change
// (A9) through the real pipeline. Its own host and tenant (fixture per class), so its sign-ins do not use
// up the sign-in limit of ApiEndToEndTests.
[Collection("postgres-integration")]
public sealed class ApiSecurityEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task AuthenticatorChange_TakesThePasswordAndTheCurrentFactor_EndsOtherSessions_AndRetiresOldRecoveryCodes()
    {
        var email = $"mfa-{fixture.Run}@example.invalid";
        await CreateUserAsync(email, MembershipRole.Administrator);
        var (first, firstCsrf) = await SignInAsync(email, "ACTIVE");
        using (first)
        {
            using (var withoutPassword = await first.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/setup", firstCsrf, new { })))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, withoutPassword.StatusCode);
                Assert.Equal("REAUTHENTICATION_FAILED", (await ApiTestHost.ProblemAsync(withoutPassword)).GetProperty("code").GetString());
            }

            var firstSecret = await StartSetupAsync(first, firstCsrf, new { password = ApiEndToEndFixture.Password });
            var firstCodes = await ConfirmAsync(first, firstCsrf, firstSecret);
            Assert.Equal(10, firstCodes.Length);
            Assert.All(firstCodes, code => Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code));

            // A second device signs in with a recovery code.
            var (second, secondCsrf) = await SignInAsync(email, "MFA_CHALLENGE");
            using (second)
            {
                using (var challenge = await second.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/challenge", secondCsrf, new { code = (string?)null, recoveryCode = firstCodes[0] })))
                    Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);

                // Replacing the authenticator takes the current factor as well as the password.
                using (var passwordOnly = await first.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/setup", firstCsrf, new { password = ApiEndToEndFixture.Password })))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, passwordOnly.StatusCode);
                    Assert.Equal("SECOND_FACTOR_FAILED", (await ApiTestHost.ProblemAsync(passwordOnly)).GetProperty("code").GetString());
                }
                var replacement = await StartSetupAsync(first, firstCsrf, new { password = ApiEndToEndFixture.Password, recoveryCode = firstCodes[1] });
                var replacementCodes = await ConfirmAsync(first, firstCsrf, replacement);

                using (var otherDevice = await second.GetAsync("/api/v1/auth/me"))
                    Assert.Equal(HttpStatusCode.Unauthorized, otherDevice.StatusCode);
                using (var thisDevice = await first.GetAsync("/api/v1/auth/security-status"))
                    Assert.Equal(HttpStatusCode.OK, thisDevice.StatusCode);

                using (var oldCode = await first.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/setup", firstCsrf, new { password = ApiEndToEndFixture.Password, recoveryCode = firstCodes[2] })))
                    Assert.Equal(HttpStatusCode.Unauthorized, oldCode.StatusCode);
                using (var newCode = await first.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/setup", firstCsrf, new { password = ApiEndToEndFixture.Password, recoveryCode = replacementCodes[0] })))
                    Assert.Equal(HttpStatusCode.OK, newCode.StatusCode);
            }
        }

        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
        Assert.True(await db.AuditLogs.AnyAsync(entry => entry.ActorUserId == userId && entry.Action == AuthAudit.MfaReplaced));
        Assert.True(BoundSecret.IsBound(await db.UserSecurities.Where(security => security.UserId == userId).Select(security => security.ProtectedTotpSecret).SingleAsync()));
    }

    [PostgreSqlFact]
    public async Task DataReset_NeedsNoReauthentication_ASignedInAdministratorReachesTheEndpoint()
    {
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (client)
        using (signIn)
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            // The step-up re-authentication is gone (21 Oct 2026): a session whose last password entry is long past
            // (the old column emptied) is enough. The re-authentication endpoint no longer exists.
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var userId = await db.Users.Where(user => user.Email == fixture.Email(MembershipRole.Administrator)).Select(user => user.Id).SingleAsync();
                await db.UserSessions.Where(session => session.UserId == userId && session.State == SessionState.Active)
                    .ExecuteUpdateAsync(update => update.SetProperty(session => session.ReauthenticatedAt, (DateTimeOffset?)null));
            }
            // An empty selection: the endpoint itself refuses it (422) and nothing is deleted.
            var body = new { scopes = Array.Empty<string>(), confirmation = "" };
            using (var reset = await client.SendAsync(Keyed(HttpMethod.Post, "/api/v1/settings/data-reset", csrf, Guid.NewGuid().ToString(), body)))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, reset.StatusCode);
            using var removed = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/reauthenticate", csrf, new { password = ApiEndToEndFixture.Password }));
            Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        }
    }

    [PostgreSqlFact]
    public async Task PasswordChange_ChecksTheCurrentPasswordFirst_CountsAFailure_AndRefusesACommonNewPassword()
    {
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.ReadOnly);
        using (client)
        using (signIn)
        {
            using (var wrong = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/change-password", csrf, new { currentPassword = "Wrong-Password#2026", newPassword = "Kx9!mQ2#vT7$pL4@" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
                Assert.Equal("CURRENT_PASSWORD_INVALID", (await ApiTestHost.ProblemAsync(wrong)).GetProperty("code").GetString());
            }
            using (var common = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/change-password", csrf, new { currentPassword = ApiEndToEndFixture.Password, newPassword = "MarketplaceHub1!" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, common.StatusCode);
                Assert.Equal("PASSWORD_POLICY_FAILED", (await ApiTestHost.ProblemAsync(common)).GetProperty("code").GetString());
            }

            await using var scope = fixture.Host!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = await db.Users.Where(user => user.Email == fixture.Email(MembershipRole.ReadOnly)).Select(user => user.Id).SingleAsync();
            Assert.True(await db.AuditLogs.AnyAsync(entry => entry.ActorUserId == userId && entry.Action == AuthAudit.PasswordChangeFailed && entry.Reason.StartsWith("wrong-current-password")));
        }
    }

    private async Task CreateUserAsync(string email, MembershipRole role)
    {
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "E2E MFA", ForcePasswordChange = false, CreatedAt = now, UpdatedAt = now };
        var created = await users.CreateAsync(user, ApiEndToEndFixture.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
        db.UserSecurities.Add(new UserSecurity { UserId = user.Id, TotpState = TotpState.Disabled });
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, UserId = user.Id, Role = role, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Client, string Csrf)> SignInAsync(string email, string expectedState)
    {
        var client = fixture.Host!.CreateClient();
        var csrf = await fixture.Host.CsrfAsync(client);
        using var signIn = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf, new { email, password = ApiEndToEndFixture.Password }));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(expectedState, (await signIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        return (client, csrf);
    }

    private static async Task<byte[]> StartSetupAsync(HttpClient client, string csrf, object body)
    {
        using var response = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/setup", csrf, body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var key = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        return FromBase32(key.Replace(" ", "", StringComparison.Ordinal));
    }

    private static async Task<string[]> ConfirmAsync(HttpClient client, string csrf, byte[] secret)
    {
        var code = TotpService.Generate(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TotpService.StepSeconds);
        using var response = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/confirm", csrf, new { code }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static byte[] FromBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var character in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character, StringComparison.Ordinal); bits += 5;
            if (bits < 8) continue;
            output.Add((byte)(buffer >> (bits - 8))); bits -= 8; buffer &= (1 << bits) - 1;
        }
        return [.. output];
    }

    private static HttpRequestMessage Keyed(HttpMethod method, string path, string csrf, string key, object? body = null)
    {
        var request = ApiTestHost.Json(method, path, csrf, body);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }
}
