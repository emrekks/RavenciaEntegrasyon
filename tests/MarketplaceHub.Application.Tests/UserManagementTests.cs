using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketplaceHub.Api.Settings;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Ayarlar › Kullanıcılar: who may change whom, without a database.
public sealed class UserManagementGuardTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static TenantMembership Member(Guid userId, MembershipRole role) => new() { Id = Guid.CreateVersion7(), TenantId = Tenant, UserId = userId, Role = role };

    [Theory]
    [InlineData("OWNER")]
    [InlineData("ADMINISTRATOR")]
    public void NobodyChangesTheirOwnAccountHere(string actorRole)
    {
        var self = Guid.CreateVersion7();
        var refusal = UserManagementEndpoints.Guard(new TenantContext(self, Tenant, actorRole), Member(self, actorRole == "OWNER" ? MembershipRole.Owner : MembershipRole.Administrator));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void AnAdministratorCannotChangeAnOwner_AnOwnerCan()
    {
        var owner = Member(Guid.CreateVersion7(), MembershipRole.Owner);
        Assert.NotNull(UserManagementEndpoints.Guard(new TenantContext(Guid.CreateVersion7(), Tenant, "ADMINISTRATOR"), owner));
        Assert.Null(UserManagementEndpoints.Guard(new TenantContext(Guid.CreateVersion7(), Tenant, "OWNER"), owner));
        Assert.Null(UserManagementEndpoints.Guard(new TenantContext(Guid.CreateVersion7(), Tenant, "ADMINISTRATOR"), Member(Guid.CreateVersion7(), MembershipRole.Operations)));
    }

    [Theory]
    [InlineData("OWNER", MembershipRole.Owner)]
    [InlineData("administrator", MembershipRole.Administrator)]
    [InlineData("READ_ONLY", MembershipRole.ReadOnly)]
    [InlineData("READONLY", MembershipRole.ReadOnly)]
    [InlineData("ACCOUNTING", MembershipRole.Accounting)]
    [InlineData("OPERATIONS", MembershipRole.Operations)]
    public void RolesParseFromTheirWireNames(string wire, MembershipRole role) => Assert.Equal(role, UserManagementEndpoints.ParseRole(wire));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ROOT")]
    public void UnknownRolesAreRefused(string? wire) => Assert.Null(UserManagementEndpoints.ParseRole(wire));
}

// The whole flow through the real pipeline and PostgreSQL. Its own host and tenant (fixture per class).
[Collection("postgres-integration")]
public sealed class UserManagementEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    private const string Base = "/api/v1/settings/users";

    [PostgreSqlFact]
    public async Task OnlyOwnersAndAdministratorsReachTheUserList()
    {
        foreach (var role in new[] { MembershipRole.Operations, MembershipRole.Accounting, MembershipRole.ReadOnly })
        {
            var (client, _, signIn) = await fixture.SignInAsync(role);
            using (client)
            using (signIn)
            using (var list = await client.GetAsync(Base))
                Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        }
        var (admin, _, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (admin)
        using (adminSignIn)
        using (var list = await admin.GetAsync(Base))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var body = await list.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(body.GetProperty("items").EnumerateArray(), item => item.GetProperty("email").GetString() == fixture.Email(MembershipRole.ReadOnly) && item.GetProperty("role").GetString() == "READONLY");
            Assert.Contains(body.GetProperty("items").EnumerateArray(), item => item.GetProperty("self").GetBoolean() && item.GetProperty("role").GetString() == "ADMINISTRATOR");
        }
    }

    [PostgreSqlFact]
    public async Task AnAdministratorAddsAUserWithAOneTimePassword_ThePasswordIsAskedAgain_AndOwnersStayOutOfReach()
    {
        var ownerEmail = $"owner-um-{fixture.Run}@example.invalid";
        var ownerId = await CreateUserAsync(ownerEmail, MembershipRole.Owner);
        var (admin, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (admin)
        using (signIn)
        {
            var email = $"new-um-{fixture.Run}@example.invalid";
            using (var withoutPassword = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, Base, csrf, new { email, displayName = "Yeni Kullanıcı", role = "OPERATIONS" })))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, withoutPassword.StatusCode);
                Assert.Equal("REAUTHENTICATION_FAILED", (await ApiTestHost.ProblemAsync(withoutPassword)).GetProperty("code").GetString());
            }
            using (var asOwner = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, Base, csrf, new { email, displayName = "Yeni Kullanıcı", role = "OWNER", password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, asOwner.StatusCode);
                Assert.Equal("OWNER_ROLE_RESERVED", (await ApiTestHost.ProblemAsync(asOwner)).GetProperty("code").GetString());
            }

            string temporary;
            using (var created = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, Base, csrf, new { email, displayName = "Yeni Kullanıcı", role = "OPERATIONS", password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);
                temporary = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("temporaryPassword").GetString()!;
                Assert.Equal(24, temporary.Length);
            }
            using (var again = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, Base, csrf, new { email, displayName = "Yeni Kullanıcı", role = "OPERATIONS", password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

            // The new user signs in with the one-time password and must change it first.
            using (var newcomer = fixture.Host!.CreateClient())
            {
                var newcomerCsrf = await fixture.Host.CsrfAsync(newcomer);
                using var first = await newcomer.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", newcomerCsrf, new { email, password = temporary }));
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                Assert.Equal("PASSWORD_CHANGE_REQUIRED", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
            }

            using (var ownerRole = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{ownerId}/role", csrf, new { role = "OPERATIONS", password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, ownerRole.StatusCode);
                Assert.Equal("OWNER_PROTECTED", (await ApiTestHost.ProblemAsync(ownerRole)).GetProperty("code").GetString());
            }
            using (var ownerDisable = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{ownerId}/status", csrf, new { active = false, password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.Forbidden, ownerDisable.StatusCode);

            var adminId = await UserIdAsync(fixture.Email(MembershipRole.Administrator));
            using (var self = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{adminId}/status", csrf, new { active = false, password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
                Assert.Equal("SELF_CHANGE_REFUSED", (await ApiTestHost.ProblemAsync(self)).GetProperty("code").GetString());
            }

            await using var scope = fixture.Host!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var newId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            Assert.True(await db.AuditLogs.AnyAsync(entry => entry.Action == UserManagementEndpoints.Created && entry.ActorUserId == adminId && entry.TargetId == newId.ToString("D") && entry.TenantId == fixture.TenantId));
            Assert.True(await db.UserSecurities.AnyAsync(security => security.UserId == newId && security.TotpState == TotpState.Disabled));
            Assert.Equal(MembershipRole.Owner, await db.TenantMemberships.Where(x => x.UserId == ownerId).Select(x => x.Role).SingleAsync());
        }
    }

    [PostgreSqlFact]
    public async Task DisablingEndsTheUsersSessions_ResettingThePasswordGivesANewOneTimePassword()
    {
        var targetEmail = $"target-um-{fixture.Run}@example.invalid";
        var targetId = await CreateUserAsync(targetEmail, MembershipRole.Accounting);
        var target = fixture.Host!.CreateClient();
        var targetCsrf = await fixture.Host.CsrfAsync(target);
        using (var login = await target.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", targetCsrf, new { email = targetEmail, password = ApiEndToEndFixture.Password })))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var (admin, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        using (admin)
        using (signIn)
        using (target)
        {
            using (var role = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{targetId}/role", csrf, new { role = "READONLY", password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.NoContent, role.StatusCode);
            using (var me = await target.GetAsync("/api/v1/auth/me"))
                Assert.Equal("READONLY", (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());

            using (var disable = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{targetId}/status", csrf, new { active = false, password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
            using (var me = await target.GetAsync("/api/v1/auth/me"))
                Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
            using (var enable = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{targetId}/status", csrf, new { active = true, password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);

            string temporary;
            using (var reset = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"{Base}/{targetId}/password-reset", csrf, new { password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
                temporary = (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("temporaryPassword").GetString()!;
            }
            using (var mfa = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"{Base}/{targetId}/mfa-reset", csrf, new { password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.NoContent, mfa.StatusCode);
            using (var sessions = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"{Base}/{targetId}/sessions/revoke", csrf, new { password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);

            using var again = fixture.Host.CreateClient();
            var againCsrf = await fixture.Host.CsrfAsync(again);
            using (var old = await again.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", againCsrf, new { email = targetEmail, password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
            using (var fresh = await again.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", againCsrf, new { email = targetEmail, password = temporary })))
                Assert.Equal("PASSWORD_CHANGE_REQUIRED", (await fresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());

            await using var scope = fixture.Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var actions = await db.AuditLogs.Where(entry => entry.TargetId == targetId.ToString("D") && entry.Action.StartsWith("USER_")).Select(entry => entry.Action).ToListAsync();
            Assert.Contains(UserManagementEndpoints.RoleChanged, actions);
            Assert.Contains(UserManagementEndpoints.Disabled, actions);
            Assert.Contains(UserManagementEndpoints.Enabled, actions);
            Assert.Contains(UserManagementEndpoints.PasswordReset, actions);
            Assert.Contains(UserManagementEndpoints.MfaReset, actions);
            Assert.Contains(UserManagementEndpoints.SessionsRevoked, actions);
        }
    }

    private async Task<Guid> CreateUserAsync(string email, MembershipRole role)
    {
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "E2E Kullanıcı", ForcePasswordChange = false, CreatedAt = now, UpdatedAt = now };
        var created = await users.CreateAsync(user, ApiEndToEndFixture.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
        db.UserSecurities.Add(new UserSecurity { UserId = user.Id, TotpState = TotpState.Disabled });
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = fixture.TenantId, UserId = user.Id, Role = role, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
    }
}
