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
            var second = fixture.Host!.CreateClient();
            var secondCsrf = await fixture.Host.CsrfAsync(second);
            string halfSignedIn;
            using (var signIn = await second.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", secondCsrf, new { email, password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal("MFA_CHALLENGE", (await signIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
                halfSignedIn = SessionToken(signIn);
            }
            using (second)
            {
                using (var challenge = await second.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/challenge", secondCsrf, new { code = (string?)null, recoveryCode = firstCodes[0] })))
                {
                    Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
                    // Scan 25 Nov 2026 (E-5): the session's token changes as it gains the second factor; the
                    // half-signed-in token seen before it signs nothing in.
                    Assert.NotEqual(halfSignedIn, SessionToken(challenge));
                }
                using (var stale = fixture.Host!.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false }))
                using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me"))
                {
                    request.Headers.Add("Cookie", $"{SessionAuthMiddleware.PilotLocalCookieName}={halfSignedIn}");
                    using var me = await stale.SendAsync(request);
                    Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
                }

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

    // Scan 25 Nov 2026 (E-5 review): the token changes only where a half-signed-in session passes its second factor. A
    // signed-in session of the same person keeps its token and answers its reads while another device's token is renewed
    // at the same moment (no 401, so no Oturum Doldu window); a session that was really ended still hears 401.
    [PostgreSqlFact]
    public async Task SecondFactorTokenRenewal_LeavesParallelReadsOfASignedInSession_AndRevokedTokensStillFail()
    {
        var email = $"mfa-rotation-{fixture.Run}@example.invalid";
        await CreateUserAsync(email, MembershipRole.Administrator);
        var (first, firstCsrf) = await SignInAsync(email, "ACTIVE");
        using (first)
        {
            var secret = await StartSetupAsync(first, firstCsrf, new { password = ApiEndToEndFixture.Password });
            var codes = await ConfirmAsync(first, firstCsrf, secret);
            var (second, secondCsrf) = await SignInAsync(email, "MFA_CHALLENGE");
            using (second)
            {
                var challenge = second.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/mfa/challenge", secondCsrf, new { code = (string?)null, recoveryCode = codes[0] }));
                var reads = Enumerable.Range(0, 6).Select(index => first.GetAsync(index % 2 == 0 ? "/api/v1/auth/me" : "/api/v1/auth/security-status")).ToArray();
                using (var renewed = await challenge)
                {
                    Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
                    Assert.Contains(renewed.Headers.GetValues("Set-Cookie"), value => value.StartsWith(SessionAuthMiddleware.PilotLocalCookieName + "=", StringComparison.Ordinal));
                }
                foreach (var read in await Task.WhenAll(reads))
                    using (read)
                    {
                        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                        // The signed-in session's own token is never renewed by a read or by the other device's step.
                        Assert.False(read.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(value => value.StartsWith(SessionAuthMiddleware.PilotLocalCookieName + "=", StringComparison.Ordinal)));
                    }
                using (var afterwards = await first.GetAsync("/api/v1/auth/me"))
                    Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);

                // The second device ends the other sessions: the first one's token is now refused.
                using (var revoke = await second.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/sessions/revoke-others", secondCsrf, new { })))
                    Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
                using (var revoked = await first.GetAsync("/api/v1/auth/me"))
                    Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
                using (var stillSignedIn = await second.GetAsync("/api/v1/auth/me"))
                    Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
            }
        }
    }

    // Scan 25 Nov 2026 (D-6): "Google Drive'a Bağlan" saved the settings and then asked for Google's consent address in
    // two requests; each checked the authenticator code, which works once, so the owner with MFA always failed. One
    // request now saves and authorizes, checked once; the code still works once, nothing is remembered between requests.
    [PostgreSqlFact]
    public async Task DriveSetup_SavesTheSettingsAndAuthorizes_InOneRequest_WithOneAuthenticatorCode()
    {
        var email = $"drive-owner-{fixture.Run}@example.invalid";
        await CreateUserAsync(email, MembershipRole.Owner);
        var (client, csrf) = await SignInAsync(email, "ACTIVE");
        using (client)
        {
            var secret = await StartSetupAsync(client, csrf, new { password = ApiEndToEndFixture.Password });
            await ConfirmAsync(client, csrf, secret);
            // The confirmation used this time step's code; the next step's code is the one the owner types now.
            var code = TotpService.Generate(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TotpService.StepSeconds + 1);
            var clientId = $"123456789-{fixture.Run}.apps.googleusercontent.com";
            var settings = new { enabled = false, ageRecipient = (string?)null, folderMode = (string?)null, folderValue = (string?)null, keepDays = (int?)null, keepMonths = (int?)null, clientId, clientSecret = $"d6-{fixture.Run}" };
            using (var setup = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/settings/offsite-backup/google/authorize", csrf, new { password = ApiEndToEndFixture.Password, code, settings })))
            {
                Assert.True(setup.StatusCode == HttpStatusCode.OK, await setup.Content.ReadAsStringAsync());
                Assert.Contains($"client_id={clientId}", (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString());
            }
            using (var read = await client.GetAsync("/api/v1/settings/offsite-backup"))
            {
                var saved = await read.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(clientId, saved.GetProperty("clientId").GetString());
                Assert.True(saved.GetProperty("clientSecretSaved").GetBoolean());
            }
            // The same code is not taken twice: the re-authentication is not relaxed.
            using var again = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/settings/offsite-backup/google/authorize", csrf, new { password = ApiEndToEndFixture.Password, code }));
            Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        }
    }

    [PostgreSqlFact]
    public async Task Resets_TakeThePasswordAgain_OnePassedCheckCoversTheSessionsNextResetsForAFewMinutes()
    {
        var (client, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (client)
        using (signIn)
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            var connection = Guid.NewGuid();
            // No password: refused before anything is counted or queued, on every reset that empties data.
            using (var reset = await client.SendAsync(Keyed(HttpMethod.Post, "/api/v1/settings/factory-reset", csrf, Guid.NewGuid().ToString(), new { confirmation = "PANELİ SIFIRLA" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
                Assert.Equal("REAUTHENTICATION_REQUIRED", (await ApiTestHost.ProblemAsync(reset)).GetProperty("code").GetString());
            }
            foreach (var path in new[] { $"/api/v1/connections/{connection}/full-reset", $"/api/v1/connections/{connection}/deep-delete" })
            {
                var request = Keyed(HttpMethod.Post, path, csrf, Guid.NewGuid().ToString(), new { confirmation = "x" });
                request.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
                using var refused = await client.SendAsync(request);
                Assert.Equal("REAUTHENTICATION_REQUIRED", (await ApiTestHost.ProblemAsync(refused)).GetProperty("code").GetString());
            }
            // A wrong password is refused.
            using (var wrong = await client.SendAsync(Keyed(HttpMethod.Post, "/api/v1/settings/factory-reset", csrf, Guid.NewGuid().ToString(), new { confirmation = "", password = "Wrong-Password#2026" })))
                Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            // The right password passes the check; the missing confirmation word is then the endpoint's own refusal (422)
            // and nothing is queued.
            using (var reset = await client.SendAsync(Keyed(HttpMethod.Post, "/api/v1/settings/factory-reset", csrf, Guid.NewGuid().ToString(), new { confirmation = "", password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, reset.StatusCode);
            // Within the next minutes the same session's resets (Ayarlar › Veri runs several in a row) need no password.
            using (var again = await client.SendAsync(Keyed(HttpMethod.Post, "/api/v1/settings/factory-reset", csrf, Guid.NewGuid().ToString(), new { confirmation = "" })))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
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

    private static string SessionToken(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(SessionAuthMiddleware.PilotLocalCookieName + "=", StringComparison.Ordinal))
            .Split(';')[0][(SessionAuthMiddleware.PilotLocalCookieName.Length + 1)..];

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
