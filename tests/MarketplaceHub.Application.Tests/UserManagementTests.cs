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

    // API-GUVENLIK-6: the answer to "add user" carries no account id and no "existing account" flag.
    [Theory]
    [InlineData(null)]
    [InlineData("one-time-password-0000000")]
    public async Task AddingAUser_AnswersWithoutTheAccountIdOrAnExistingAccountFlag(string? temporaryPassword)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await UserManagementEndpoints.AddedResponse(temporaryPassword).ExecuteAsync(context);

        context.Response.Body.Position = 0;
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
        Assert.Equal(["added", "temporaryPassword"], body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.True(body.GetProperty("added").GetBoolean());
        Assert.Equal(temporaryPassword, body.GetProperty("temporaryPassword").GetString());
    }
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
            var (client, _, signIn) = await fixture.SignInAsync(role, ownNetwork: true);
            using (client)
            using (signIn)
            using (var list = await client.GetAsync(Base))
                Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        }
        var (admin, _, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator, ownNetwork: true);
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
        var (admin, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator, ownNetwork: true);
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
            using (var newcomer = fixture.Host!.ClientFromOwnNetwork())
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
        var target = fixture.Host!.ClientFromOwnNetwork();
        var targetCsrf = await fixture.Host.CsrfAsync(target);
        using (var login = await target.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", targetCsrf, new { email = targetEmail, password = ApiEndToEndFixture.Password })))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var (admin, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator, ownNetwork: true);
        using (admin)
        using (signIn)
        using (target)
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
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

            using var again = fixture.Host!.ClientFromOwnNetwork();
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

    // API-GUVENLIK-5: two owners demoting each other at the same moment leave exactly one owner, never none. The two are
    // the only owners of a workspace of their own, so an owner another test of this class adds cannot make both
    // demotions legitimately allowed.
    [PostgreSqlFact]
    public async Task TwoOwnersDemotingEachOtherAtOnce_LeaveOneOwner()
    {
        var tenantId = Guid.CreateVersion7();
        await using (var scope = fixture.Host!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Tenants.Add(new Tenant { Id = tenantId, Code = $"um-owners-{fixture.Run}", DisplayName = "İki Sahip", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        var ownerEmail = $"owner1-um-{fixture.Run}@example.invalid";
        var otherEmail = $"owner2-um-{fixture.Run}@example.invalid";
        var ownerId = await CreateUserAsync(ownerEmail, MembershipRole.Owner, tenantId);
        var otherId = await CreateUserAsync(otherEmail, MembershipRole.Owner, tenantId);
        using var owner = fixture.Host.ClientFromOwnNetwork();
        using var other = fixture.Host.ClientFromOwnNetwork();
        var ownerCsrf = await fixture.Host.CsrfAsync(owner);
        var otherCsrf = await fixture.Host.CsrfAsync(other);
        using (var login = await owner.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", ownerCsrf, new { email = ownerEmail, password = ApiEndToEndFixture.Password })))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using (var login = await other.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", otherCsrf, new { email = otherEmail, password = ApiEndToEndFixture.Password })))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        {
            var responses = await Task.WhenAll(
                owner.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{otherId}/role", ownerCsrf, new { role = "ADMINISTRATOR", password = ApiEndToEndFixture.Password })),
                other.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{ownerId}/role", otherCsrf, new { role = "ADMINISTRATOR", password = ApiEndToEndFixture.Password })));
            try
            {
                // One change goes through; the other is refused as the last owner (or, when it starts after the first
                // committed, because its sender is no longer an owner).
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
                Assert.Single(responses, response => response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden);
            }
            finally
            {
                foreach (var response in responses) response.Dispose();
            }

            await using var scope = fixture.Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.TenantMemberships.CountAsync(x => x.TenantId == tenantId && x.Role == MembershipRole.Owner && x.Status == RecordStatus.Active));
        }
    }

    // API-GUVENLIK-6: an account of another workspace joins with the same answer shape as a new one: no account id, no flag.
    [PostgreSqlFact]
    public async Task AddingAnAccountOfAnotherWorkspace_DoesNotRevealItsId()
    {
        var email = $"elsewhere-um-{fixture.Run}@example.invalid";
        Guid elsewhereId;
        await using (var scope = fixture.Host!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var now = DateTimeOffset.UtcNow;
            var otherTenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"um-other-{fixture.Run}", DisplayName = "Başka Alan", CreatedAt = now, UpdatedAt = now };
            db.Tenants.Add(otherTenant);
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Başka Kullanıcı", CreatedAt = now, UpdatedAt = now };
            Assert.True((await users.CreateAsync(user, ApiEndToEndFixture.Password)).Succeeded);
            db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = otherTenant.Id, UserId = user.Id, Role = MembershipRole.Operations, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            elsewhereId = user.Id;
        }
        var (admin, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator, ownNetwork: true);
        using (admin)
        using (signIn)
        using (var added = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, Base, csrf, new { email, displayName = "Başka Kullanıcı", role = "READONLY", password = ApiEndToEndFixture.Password })))
        {
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var raw = await added.Content.ReadAsStringAsync();
            Assert.DoesNotContain(elsewhereId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("existingAccount", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("userId", raw, StringComparison.Ordinal);
        }
    }

    private async Task<Guid> CreateUserAsync(string email, MembershipRole role, Guid? tenantId = null)
    {
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "E2E Kullanıcı", ForcePasswordChange = false, CreatedAt = now, UpdatedAt = now };
        var created = await users.CreateAsync(user, ApiEndToEndFixture.Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
        db.UserSecurities.Add(new UserSecurity { UserId = user.Id, TotpState = TotpState.Disabled });
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = tenantId ?? fixture.TenantId, UserId = user.Id, Role = role, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
    }
}
