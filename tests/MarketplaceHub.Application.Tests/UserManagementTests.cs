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
            using (var withoutPassword = await admin.SendAsync(Add(csrf, new { email, displayName = "Yeni Kullanıcı", role = "OPERATIONS" })))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, withoutPassword.StatusCode);
                Assert.Equal("REAUTHENTICATION_FAILED", (await ApiTestHost.ProblemAsync(withoutPassword)).GetProperty("code").GetString());
            }
            using (var asOwner = await admin.SendAsync(Add(csrf, new { email, displayName = "Yeni Kullanıcı", role = "OWNER", password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, asOwner.StatusCode);
                Assert.Equal("OWNER_ROLE_RESERVED", (await ApiTestHost.ProblemAsync(asOwner)).GetProperty("code").GetString());
            }

            using (var withoutKey = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, Base, csrf, new { email, displayName = "Yeni Kullanıcı", role = "OPERATIONS", password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, withoutKey.StatusCode);
                Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await ApiTestHost.ProblemAsync(withoutKey)).GetProperty("code").GetString());
            }

            string temporary;
            var key = $"um-create-{fixture.Run}";
            using (var created = await admin.SendAsync(Add(csrf, new { email, displayName = "Yeni Kullanıcı", role = "OPERATIONS", password = ApiEndToEndFixture.Password }, key)))
            {
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);
                temporary = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("temporaryPassword").GetString()!;
                Assert.Equal(24, temporary.Length);
            }
            // The same request again with the same key creates nothing, and the one-time password is not replayed: it was
            // never stored for a replay (SecretResponseRoutes).
            using (var repeat = await admin.SendAsync(Add(csrf, new { email, displayName = "Yeni Kullanıcı", role = "OPERATIONS", password = ApiEndToEndFixture.Password }, key)))
            {
                Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
                var raw = await repeat.Content.ReadAsStringAsync();
                Assert.Contains("IDEMPOTENCY_RESPONSE_WITHHELD", raw, StringComparison.Ordinal);
                Assert.DoesNotContain(temporary, raw, StringComparison.Ordinal);
            }
            await using (var keyScope = fixture.Host!.Services.CreateAsyncScope())
            {
                var keyDb = keyScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var records = await keyDb.ApiIdempotencyRecords.AsNoTracking().Where(x => x.TenantId == fixture.TenantId && x.RouteTemplate == Base).ToListAsync();
                Assert.NotEmpty(records);
                Assert.All(records, record => Assert.True(record.ResponseBody is null || !record.ResponseBody.Contains(temporary, StringComparison.Ordinal)));
                Assert.Equal(1, await keyDb.Users.CountAsync(user => user.Email == email));
            }
            using (var again = await admin.SendAsync(Add(csrf, new { email, displayName = "Yeni Kullanıcı", role = "OPERATIONS", password = ApiEndToEndFixture.Password })))
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

    // API-GUVENLIK-6 (owner decision 16 Nov 2026): an existing account of another workspace is invited, not added. The answer
    // has the new-account shape without the password, the invitation grants nothing (no sign-in to the workspace, no role
    // even on a session that names it, no realtime hub), and only the invited account accepts it, once, before it expires.
    [PostgreSqlFact]
    public async Task AnExistingAccountIsInvited_GetsNothingUntilItAccepts_AndAnExpiredInvitationCannotBeAccepted()
    {
        var email = $"invited-um-{fixture.Run}@example.invalid";
        var (invitedId, homeTenantId) = await CreateAccountElsewhereAsync(email, "Ev Alanı");
        var (admin, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Administrator, ownNetwork: true);
        using (admin)
        using (signIn)
        {
            using (var added = await admin.SendAsync(Add(csrf, new { email, displayName = "Davetli Kişi", role = "READONLY", password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.OK, added.StatusCode);
                var raw = await added.Content.ReadAsStringAsync();
                Assert.DoesNotContain(invitedId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
                var body = JsonSerializer.Deserialize<JsonElement>(raw);
                Assert.Equal(["added", "temporaryPassword"], body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
                Assert.Equal(JsonValueKind.Null, body.GetProperty("temporaryPassword").ValueKind);
            }
            using (var twice = await admin.SendAsync(Add(csrf, new { email, displayName = "Davetli Kişi", role = "READONLY", password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
                Assert.Equal("INVITATION_PENDING", (await ApiTestHost.ProblemAsync(twice)).GetProperty("code").GetString());
            }
            using (var list = await admin.GetAsync(Base))
            {
                var body = await list.Content.ReadFromJsonAsync<JsonElement>();
                Assert.DoesNotContain(body.GetProperty("items").EnumerateArray(), item => item.GetProperty("email").GetString() == email);
                var invitation = Assert.Single(body.GetProperty("invitations").EnumerateArray(), item => item.GetProperty("email").GetString() == email);
                Assert.Equal("Davetli Kişi", invitation.GetProperty("displayName").GetString());
                Assert.Equal("READONLY", invitation.GetProperty("role").GetString());
                Assert.False(invitation.GetProperty("expired").GetBoolean());
            }
        }
        await using (var scope = fixture.Host!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.TenantMemberships.AnyAsync(x => x.TenantId == fixture.TenantId && x.UserId == invitedId));
            Assert.True(await db.AuditLogs.AnyAsync(entry => entry.Action == UserManagementEndpoints.Invited && entry.TenantId == fixture.TenantId && entry.TargetId == invitedId.ToString("D")));
        }

        // Pending: the workspace is neither offered nor reachable at sign-in.
        var (invitee, inviteeCsrf, inviteeSignIn) = await SignInAsAsync(email);
        using (invitee)
        using (inviteeSignIn)
        {
            Assert.Equal(HttpStatusCode.OK, inviteeSignIn.StatusCode);
            using (var me = await invitee.GetAsync("/api/v1/auth/me"))
                Assert.Equal(homeTenantId, (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenantId").GetGuid());
            var (direct, _, directSignIn) = await SignInAsAsync(email, fixture.TenantId);
            using (direct)
            using (directSignIn)
            {
                // Asking for the inviting workspace opens no session there: the answer offers only the account's own.
                Assert.Equal(HttpStatusCode.Conflict, directSignIn.StatusCode);
                var problem = await ApiTestHost.ProblemAsync(directSignIn);
                Assert.Equal("TENANT_SELECTION_REQUIRED", problem.GetProperty("code").GetString());
                Assert.Equal([homeTenantId], problem.GetProperty("tenants").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray());
            }

            // Even a session row that names the inviting workspace gets no role, no tenant and no realtime hub.
            Guid sessionId;
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                sessionId = await db.UserSessions.Where(x => x.UserId == invitedId && x.TenantId == homeTenantId && x.State == SessionState.Active).OrderByDescending(x => x.IssuedAt).Select(x => x.Id).FirstAsync();
                await db.UserSessions.Where(x => x.Id == sessionId).ExecuteUpdateAsync(x => x.SetProperty(s => s.TenantId, fixture.TenantId));
                Assert.Empty(await MarketplaceHub.Api.Realtime.OperationsHubSessionWatcher.ValidSessions(db, [sessionId], DateTimeOffset.UtcNow).ToListAsync());
            }
            using (var me = await invitee.GetAsync("/api/v1/auth/me"))
            {
                var body = await me.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(JsonValueKind.Null, body.GetProperty("role").ValueKind);
                Assert.Equal(JsonValueKind.Null, body.GetProperty("tenantId").ValueKind);
            }
            using (var data = await invitee.GetAsync("/api/v1/settings/appearance"))
                Assert.Equal(HttpStatusCode.Unauthorized, data.StatusCode);
            using (var gated = await invitee.GetAsync(Base))
                Assert.Equal(HttpStatusCode.Forbidden, gated.StatusCode);
            using (var invitations = await invitee.GetAsync("/api/v1/auth/invitations"))
                Assert.Equal(HttpStatusCode.Forbidden, invitations.StatusCode);
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserSessions.Where(x => x.Id == sessionId).ExecuteUpdateAsync(x => x.SetProperty(s => s.TenantId, homeTenantId));

            // The invited account lists its own invitation: the workspace, who invited, the role.
            Guid invitationId;
            using (var mine = await invitee.GetAsync("/api/v1/auth/invitations"))
            {
                Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
                var invitation = Assert.Single((await mine.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
                Assert.Equal($"E2E {fixture.Run}", invitation.GetProperty("workspaceName").GetString());
                Assert.Equal("E2E Administrator", invitation.GetProperty("invitedBy").GetString());
                Assert.Equal("READONLY", invitation.GetProperty("role").GetString());
                invitationId = invitation.GetProperty("id").GetGuid();
            }

            // Nobody else answers it.
            var (other, otherCsrf, otherSignIn) = await fixture.SignInAsync(MembershipRole.Operations, ownNetwork: true);
            using (other)
            using (otherSignIn)
            {
                using (var foreign = await other.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{invitationId}/accept", otherCsrf)))
                    Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
                using (var foreignDecline = await other.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{invitationId}/decline", otherCsrf)))
                    Assert.Equal(HttpStatusCode.NoContent, foreignDecline.StatusCode);
                using (var othersList = await other.GetAsync("/api/v1/auth/invitations"))
                    Assert.Empty((await othersList.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
            }

            // Expired: it cannot be accepted, the invited side no longer lists it, the inviter sees it expired and renews it.
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().TenantInvitations.Where(x => x.Id == invitationId).ExecuteUpdateAsync(x => x.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            using (var expired = await invitee.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{invitationId}/accept", inviteeCsrf)))
            {
                Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
                Assert.Equal("INVITATION_EXPIRED", (await ApiTestHost.ProblemAsync(expired)).GetProperty("code").GetString());
            }
            using (var none = await invitee.GetAsync("/api/v1/auth/invitations"))
                Assert.Empty((await none.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
            var (renewer, renewerCsrf, renewerSignIn) = await fixture.SignInAsync(MembershipRole.Administrator, ownNetwork: true);
            using (renewer)
            using (renewerSignIn)
            {
                using (var list = await renewer.GetAsync(Base))
                    Assert.True(Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitations").EnumerateArray(), item => item.GetProperty("email").GetString() == email).GetProperty("expired").GetBoolean());
                using (var renewed = await renewer.SendAsync(Add(renewerCsrf, new { email, displayName = "Davetli Kişi", role = "READONLY", password = ApiEndToEndFixture.Password })))
                    Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
            }

            // Accepted: the membership exists with the offered role, the invitation is gone, a repeat finds nothing.
            using (var accepted = await invitee.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{invitationId}/accept", inviteeCsrf)))
            {
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(fixture.TenantId, body.GetProperty("tenantId").GetGuid());
            }
            using (var repeat = await invitee.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{invitationId}/accept", inviteeCsrf)))
                Assert.Equal(HttpStatusCode.NotFound, repeat.StatusCode);
        }
        await using (var scope = fixture.Host!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = await db.TenantMemberships.SingleAsync(x => x.TenantId == fixture.TenantId && x.UserId == invitedId);
            Assert.Equal(MembershipRole.ReadOnly, membership.Role);
            Assert.Equal(RecordStatus.Active, membership.Status);
            Assert.False(await db.TenantInvitations.AnyAsync(x => x.UserId == invitedId));
            Assert.True(await db.AuditLogs.AnyAsync(entry => entry.Action == UserManagementEndpoints.InvitationAccepted && entry.TenantId == fixture.TenantId && entry.ActorUserId == invitedId));
        }
        var (joined, _, joinedSignIn) = await SignInAsAsync(email, fixture.TenantId);
        using (joined)
        using (joinedSignIn)
        using (var me = await joined.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, joinedSignIn.StatusCode);
            var body = await me.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(fixture.TenantId, body.GetProperty("tenantId").GetGuid());
            Assert.Equal("READONLY", body.GetProperty("role").GetString());
        }
    }

    // Reddet removes the invitation and adds nothing; Geri Çek removes it from the inviter's side; an administrator cannot
    // withdraw an invitation to the owner role, and such an invitation is no owner: it adds no membership at all.
    [PostgreSqlFact]
    public async Task AnInvitationIsDeclinedOrWithdrawn_AndAnOwnerInvitationIsNoOwner()
    {
        var email = $"declines-um-{fixture.Run}@example.invalid";
        var (invitedId, _) = await CreateAccountElsewhereAsync(email, "Reddeden Alan");
        var (owner, ownerCsrf, ownerSignIn) = await fixture.SignInAsync(MembershipRole.Owner, ownNetwork: true);
        var (invitee, inviteeCsrf, inviteeSignIn) = await SignInAsAsync(email);
        using (owner)
        using (ownerSignIn)
        using (invitee)
        using (inviteeSignIn)
        {
            using (var invited = await owner.SendAsync(Add(ownerCsrf, new { email, displayName = "Reddeden", role = "OPERATIONS", password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
            var invitationId = await InvitationIdAsync(invitedId);
            using (var declined = await invitee.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{invitationId}/decline", inviteeCsrf)))
                Assert.Equal(HttpStatusCode.NoContent, declined.StatusCode);
            using (var late = await invitee.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{invitationId}/accept", inviteeCsrf)))
                Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
            using (var hidden = await invitee.GetAsync("/api/v1/auth/invitations"))
                Assert.Empty((await hidden.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());

            // Declined: for a week the same workspace is answered as if the invitation were still pending, so the inviter
            // does not learn it was declined and the invited person is not asked again at once.
            using (var quiet = await owner.SendAsync(Add(ownerCsrf, new { email, displayName = "Reddeden", role = "OPERATIONS", password = ApiEndToEndFixture.Password })))
            {
                Assert.Equal(HttpStatusCode.Conflict, quiet.StatusCode);
                Assert.Equal("INVITATION_PENDING", (await ApiTestHost.ProblemAsync(quiet)).GetProperty("code").GetString());
            }
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().TenantInvitations.Where(x => x.Id == invitationId).ExecuteUpdateAsync(x => x.SetProperty(i => i.DeclinedAt, DateTimeOffset.UtcNow.AddDays(-8)));

            // Invited again after the quiet week, now as an owner: still no membership, so no owner and no role anywhere.
            using (var asOwner = await owner.SendAsync(Add(ownerCsrf, new { email, displayName = "Reddeden", role = "OWNER", password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
            var ownerInvitationId = await InvitationIdAsync(invitedId);
            Assert.Equal(invitationId, ownerInvitationId);
            using (var shown = await invitee.GetAsync("/api/v1/auth/invitations"))
                Assert.Single((await shown.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
            await using (var scope = fixture.Host!.Services.CreateAsyncScope())
                Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().TenantMemberships.AnyAsync(x => x.TenantId == fixture.TenantId && x.UserId == invitedId));
            var (admin, adminCsrf, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator, ownNetwork: true);
            using (admin)
            using (adminSignIn)
            using (var refused = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Delete, $"{Base}/invitations/{ownerInvitationId}", adminCsrf)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Equal("OWNER_PROTECTED", (await ApiTestHost.ProblemAsync(refused)).GetProperty("code").GetString());
            }
            using (var withdrawn = await owner.SendAsync(ApiTestHost.Json(HttpMethod.Delete, $"{Base}/invitations/{ownerInvitationId}", ownerCsrf)))
                Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
            using (var again = await owner.SendAsync(ApiTestHost.Json(HttpMethod.Delete, $"{Base}/invitations/{ownerInvitationId}", ownerCsrf)))
                Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
            using (var gone = await invitee.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{ownerInvitationId}/accept", inviteeCsrf)))
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            using (var mine = await invitee.GetAsync("/api/v1/auth/invitations"))
                Assert.Empty((await mine.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        }
        await using (var check = fixture.Host!.Services.CreateAsyncScope())
        {
            var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.TenantMemberships.AnyAsync(x => x.TenantId == fixture.TenantId && x.UserId == invitedId));
            Assert.False(await db.TenantInvitations.AnyAsync(x => x.UserId == invitedId));
            var actions = await db.AuditLogs.Where(entry => entry.TenantId == fixture.TenantId && entry.TargetId == invitedId.ToString("D")).Select(entry => entry.Action).ToListAsync();
            Assert.Contains(UserManagementEndpoints.InvitationDeclined, actions);
            Assert.Single(actions, action => action == UserManagementEndpoints.InvitationWithdrawn);
        }
    }

    // Security review 16 Nov 2026 (a): the inviter's authority is checked again at acceptance. An administrator deactivated,
    // or an owner demoted, behind the endpoints' back (no invitation withdrawn) leaves invitations that grant nothing:
    // Kabul Et answers INVITATION_REVOKED and removes them.
    [PostgreSqlFact]
    public async Task AcceptingChecksTheInvitersAuthorityAgain()
    {
        var adminEmail = $"inviter-admin-{fixture.Run}@example.invalid";
        var ownerEmail = $"inviter-owner-{fixture.Run}@example.invalid";
        var adminId = await CreateUserAsync(adminEmail, MembershipRole.Administrator);
        var ownerId = await CreateUserAsync(ownerEmail, MembershipRole.Owner);
        var firstEmail = $"revoked-a-{fixture.Run}@example.invalid";
        var secondEmail = $"revoked-b-{fixture.Run}@example.invalid";
        var (firstId, _) = await CreateAccountElsewhereAsync(firstEmail, "Birinci Ev");
        var (secondId, _) = await CreateAccountElsewhereAsync(secondEmail, "İkinci Ev");

        var (admin, adminCsrf, adminSignIn) = await SignInAsAsync(adminEmail);
        using (admin)
        using (adminSignIn)
        using (var invited = await admin.SendAsync(Add(adminCsrf, new { email = firstEmail, displayName = "Birinci", role = "ADMINISTRATOR", password = ApiEndToEndFixture.Password })))
            Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        var (owner, ownerCsrf, ownerSignIn) = await SignInAsAsync(ownerEmail);
        using (owner)
        using (ownerSignIn)
        using (var invited = await owner.SendAsync(Add(ownerCsrf, new { email = secondEmail, displayName = "İkinci", role = "OWNER", password = ApiEndToEndFixture.Password })))
            Assert.Equal(HttpStatusCode.OK, invited.StatusCode);

        await using (var scope = fixture.Host!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.TenantMemberships.Where(x => x.TenantId == fixture.TenantId && x.UserId == adminId).ExecuteUpdateAsync(x => x.SetProperty(m => m.Status, RecordStatus.Disabled));
            await db.TenantMemberships.Where(x => x.TenantId == fixture.TenantId && x.UserId == ownerId).ExecuteUpdateAsync(x => x.SetProperty(m => m.Role, MembershipRole.Administrator));
        }

        foreach (var (email, userId) in new[] { (firstEmail, firstId), (secondEmail, secondId) })
        {
            var invitationId = await InvitationIdAsync(userId);
            var (invitee, inviteeCsrf, inviteeSignIn) = await SignInAsAsync(email);
            using (invitee)
            using (inviteeSignIn)
            using (var accept = await invitee.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{invitationId}/accept", inviteeCsrf)))
            {
                Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
                Assert.Equal("INVITATION_REVOKED", (await ApiTestHost.ProblemAsync(accept)).GetProperty("code").GetString());
            }
            await using var scope = fixture.Host!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.TenantMemberships.AnyAsync(x => x.TenantId == fixture.TenantId && x.UserId == userId));
            Assert.False(await db.TenantInvitations.AnyAsync(x => x.UserId == userId));
            Assert.True(await db.AuditLogs.AnyAsync(entry => entry.Action == UserManagementEndpoints.InvitationRevoked && entry.TenantId == fixture.TenantId && entry.ActorUserId == userId));
        }
    }

    // Security review 16 Nov 2026 (b): deactivating a user, or demoting them below the elevated roles, withdraws their
    // outstanding invitations in the same change; an owner made administrator loses only the invitations to the owner role.
    [PostgreSqlFact]
    public async Task DeactivatingOrDemotingAnInviterWithdrawsTheirInvitations()
    {
        var disabledEmail = $"inv-disabled-{fixture.Run}@example.invalid";
        var demotedEmail = $"inv-demoted-{fixture.Run}@example.invalid";
        var ownerEmail = $"inv-owner-{fixture.Run}@example.invalid";
        var disabledId = await CreateUserAsync(disabledEmail, MembershipRole.Administrator);
        var demotedId = await CreateUserAsync(demotedEmail, MembershipRole.Administrator);
        var ownerId = await CreateUserAsync(ownerEmail, MembershipRole.Owner);
        var targets = new Dictionary<string, Guid>();
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            var email = $"inv-target-{name}-{fixture.Run}@example.invalid";
            targets[name] = (await CreateAccountElsewhereAsync(email, $"Alan {name}")).UserId;
        }
        async Task InviteAsync(string inviterEmail, string target, string role)
        {
            var (client, csrf, signIn) = await SignInAsAsync(inviterEmail);
            using (client)
            using (signIn)
            using (var invited = await client.SendAsync(Add(csrf, new { email = $"inv-target-{target}-{fixture.Run}@example.invalid", displayName = target, role, password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        }
        await InviteAsync(disabledEmail, "a", "OPERATIONS");
        await InviteAsync(demotedEmail, "b", "READONLY");
        await InviteAsync(ownerEmail, "c", "OWNER");
        await InviteAsync(ownerEmail, "d", "ACCOUNTING");

        var (fixtureOwner, csrf, signIn) = await fixture.SignInAsync(MembershipRole.Owner, ownNetwork: true);
        using (fixtureOwner)
        using (signIn)
        {
            using (var disable = await fixtureOwner.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{disabledId}/status", csrf, new { active = false, password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
            using (var demote = await fixtureOwner.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{demotedId}/role", csrf, new { role = "OPERATIONS", password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.NoContent, demote.StatusCode);
            using (var ownerToAdmin = await fixtureOwner.SendAsync(ApiTestHost.Json(HttpMethod.Put, $"{Base}/{ownerId}/role", csrf, new { role = "ADMINISTRATOR", password = ApiEndToEndFixture.Password })))
                Assert.Equal(HttpStatusCode.NoContent, ownerToAdmin.StatusCode);
        }

        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.TenantInvitations.AnyAsync(x => x.UserId == targets["a"]));
        Assert.False(await db.TenantInvitations.AnyAsync(x => x.UserId == targets["b"]));
        Assert.False(await db.TenantInvitations.AnyAsync(x => x.UserId == targets["c"]));
        Assert.True(await db.TenantInvitations.AnyAsync(x => x.UserId == targets["d"] && x.InvitedByUserId == ownerId));
        var withdrawn = await db.AuditLogs.Where(entry => entry.TenantId == fixture.TenantId && entry.Action == UserManagementEndpoints.InvitationWithdrawn).Select(entry => entry.TargetId).ToListAsync();
        foreach (var name in new[] { "a", "b", "c" }) Assert.Contains(targets[name].ToString("D"), withdrawn);
        Assert.DoesNotContain(targets["d"].ToString("D"), withdrawn);
    }

    // The invitation routes need a fully signed-in session.
    [PostgreSqlFact]
    public async Task TheInvitationRoutesRefuseAVisitorWithoutASession()
    {
        using var visitor = fixture.Host!.ClientFromOwnNetwork();
        var csrf = await fixture.Host.CsrfAsync(visitor);
        using (var list = await visitor.GetAsync("/api/v1/auth/invitations"))
            Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        using (var accept = await visitor.SendAsync(ApiTestHost.Json(HttpMethod.Post, $"/api/v1/auth/invitations/{Guid.NewGuid()}/accept", csrf)))
            Assert.Equal(HttpStatusCode.Unauthorized, accept.StatusCode);
    }

    private static HttpRequestMessage Add(string csrf, object body, string? key = null)
    {
        var request = ApiTestHost.Json(HttpMethod.Post, Base, csrf, body);
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return request;
    }

    private async Task<(HttpClient Client, string Csrf, HttpResponseMessage SignIn)> SignInAsAsync(string email, Guid? tenantId = null)
    {
        var client = fixture.Host!.ClientFromOwnNetwork();
        var csrf = await fixture.Host.CsrfAsync(client);
        var signIn = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf, new { email, password = ApiEndToEndFixture.Password, tenantId }));
        return (client, csrf, signIn);
    }

    // An account that belongs to a workspace of its own only.
    private async Task<(Guid UserId, Guid TenantId)> CreateAccountElsewhereAsync(string email, string workspace)
    {
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var home = new Tenant { Id = Guid.CreateVersion7(), Code = $"um-{Guid.NewGuid():N}"[..30], DisplayName = workspace, CreatedAt = now, UpdatedAt = now };
        db.Tenants.Add(home);
        await db.SaveChangesAsync();
        return (await CreateUserAsync(email, MembershipRole.Operations, home.Id), home.Id);
    }

    private async Task<Guid> InvitationIdAsync(Guid userId)
    {
        await using var scope = fixture.Host!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().TenantInvitations.Where(x => x.TenantId == fixture.TenantId && x.UserId == userId).Select(x => x.Id).SingleAsync();
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
