using System.Net.Mail;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Settings;

// Ayarlar › Kullanıcılar: the workspace's users, managed by the owner and administrators only
// (RoleAuthorizationMiddleware: every /api/v1/settings/users route is Elevated, reads included).
//   GET  /api/v1/settings/users                          name, e-mail, role, 2FA, last sign-in, state; pending invitations
//   POST /api/v1/settings/users                          add (Idempotency-Key): a new account gets a one-time password
//                                                        (shown once, changed at the first sign-in); an existing account
//                                                        is invited and joins only when it accepts (API-GUVENLIK-6)
//   DELETE /api/v1/settings/users/invitations/{id}       Daveti Geri Çek: the pending invitation is removed
//   PUT  /api/v1/settings/users/{id}/role                change the role in this workspace
//   PUT  /api/v1/settings/users/{id}/status              Pasife Al / Etkinleştir (the membership in this workspace)
//   POST /api/v1/settings/users/{id}/password-reset      a new one-time password, sessions ended
//   POST /api/v1/settings/users/{id}/mfa-reset           authenticator and recovery codes removed, sessions ended
//   POST /api/v1/settings/users/{id}/sessions/revoke     Oturumları Kapat
// The panel sends no e-mail invitations (there is no mail service for users), so the one-time password is
// shown once to the person who made it, for them to hand over. Every change takes the caller's password (and
// authenticator code when it is on) again and is written to ops.audit_logs.
// Safety: nobody changes their own account here (Güvenlik is for that), an administrator cannot touch an owner
// or make one, the last active owner is never demoted or disabled, and the account-wide resets (password,
// authenticator) are refused for an account that belongs to another workspace too.
public static class UserManagementEndpoints
{
    public const string Created = "USER_CREATED";
    // Written before invitations (16 Nov 2026) when an existing account was added at once; kept for reading old logs.
    public const string MembershipAdded = "USER_MEMBERSHIP_ADDED";
    public const string Invited = "USER_INVITED";
    public const string InvitationWithdrawn = "USER_INVITATION_WITHDRAWN";
    public const string InvitationAccepted = "USER_INVITATION_ACCEPTED";
    public const string InvitationDeclined = "USER_INVITATION_DECLINED";
    public const string InvitationRevoked = "USER_INVITATION_REVOKED";
    public const string RoleChanged = "USER_ROLE_CHANGED";
    public const string Disabled = "USER_DISABLED";
    public const string Enabled = "USER_ENABLED";
    public const string PasswordReset = "USER_PASSWORD_RESET";
    public const string MfaReset = "USER_MFA_RESET";
    public const string SessionsRevoked = "USER_SESSIONS_REVOKED";

    public sealed record CreateRequest(string? Email, string? DisplayName, string? Role, string? Password, string? Code = null, string? RecoveryCode = null);
    public sealed record RoleRequest(string? Role, string? Password, string? Code = null, string? RecoveryCode = null);
    public sealed record StatusRequest(bool Active, string? Password, string? Code = null, string? RecoveryCode = null);
    public sealed record ReauthRequest(string? Password, string? Code = null, string? RecoveryCode = null);

    public static IEndpointRouteBuilder MapUserManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/settings/users").WithGates(ApiGate.Elevated, ApiGate.Elevated);
        group.MapGet("", ListAsync).Produces<TenantUsersView>();
        // A repeat with the same key is answered IDEMPOTENCY_RESPONSE_WITHHELD: the one-time password is never stored
        // for a replay (SecretResponseRoutes).
        group.MapPost("", CreateAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).RequireIdempotencyKey().Produces<AddedBody>();
        group.MapDelete("/invitations/{invitationId:guid}", WithdrawInvitationAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).Idempotent(WriteReasons.Removes).Produces(StatusCodes.Status204NoContent);
        group.MapPut("/{id:guid}/role", ChangeRoleAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).Idempotent("sets the role the body names").Produces(StatusCodes.Status204NoContent);
        group.MapPut("/{id:guid}/status", ChangeStatusAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).Idempotent("sets the active state the body names").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/{id:guid}/password-reset", ResetPasswordAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).NotIdempotent("each call issues a new one-time temporary password and ends the user's sessions").Produces<TemporaryPasswordView>();
        group.MapPost("/{id:guid}/mfa-reset", ResetMfaAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).Idempotent("clears the user's second factor; a repeat finds it cleared").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/{id:guid}/sessions/revoke", RevokeSessionsAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit).Idempotent("ends the user's open sessions; a repeat ends none").Produces<SessionsRevokedView>();
        return endpoints;
    }

    private static async Task<IResult> ListAsync(HttpContext http, AppDbContext db, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        var now = time.GetUtcNow();
        var rows = await (
            from membership in db.TenantMemberships.AsNoTracking()
            where membership.TenantId == tenant.TenantId
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            select new
            {
                user.Id,
                user.DisplayName,
                user.Email,
                membership.Role,
                MembershipStatus = membership.Status,
                UserStatus = user.Status,
                user.LastLoginAt,
                user.LockoutEnd,
                user.ForcePasswordChange,
                user.CreatedAt,
                Totp = db.UserSecurities.Where(security => security.UserId == user.Id).Select(security => (TotpState?)security.TotpState).FirstOrDefault(),
                OpenSessions = db.UserSessions.Count(session => session.UserId == user.Id && session.TenantId == tenant.TenantId && session.State != SessionState.Revoked && session.ExpiresAt > now && session.AbsoluteExpiresAt > now)
            }).ToListAsync(http.RequestAborted);
        var items = rows
            .OrderBy(x => x.MembershipStatus == RecordStatus.Active ? 0 : 1).ThenBy(x => x.Role).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new
            {
                id = x.Id,
                displayName = x.DisplayName,
                email = x.Email,
                role = RoleWire(x.Role),
                active = x.MembershipStatus == RecordStatus.Active && x.UserStatus == "ACTIVE",
                lockedOut = x.LockoutEnd is { } end && end > now,
                mfaEnabled = x.Totp == TotpState.Enabled,
                passwordChangePending = x.ForcePasswordChange,
                lastLoginAt = x.LastLoginAt,
                createdAt = x.CreatedAt,
                openSessions = x.OpenSessions,
                self = x.Id == tenant.UserId
            });
        // The name and e-mail the inviter typed, not the account's own: the list tells nothing about the account.
        var invitationRows = await db.TenantInvitations.AsNoTracking().Where(x => x.TenantId == tenant.TenantId).ToListAsync(http.RequestAborted);
        var invitations = invitationRows
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new TenantInvitationView(x.Id, x.DisplayName, x.Email, RoleWire(x.Role), x.CreatedAt, x.ExpiresAt, x.ExpiresAt <= now));
        return Results.Ok(new { currentUserId = tenant.UserId, currentRole = tenant.Role, items, invitations });
    }

    // Withdrawing only takes back an offer that grants nothing yet, so it asks for no password; an administrator cannot
    // withdraw an invitation to the owner role, as they cannot change an owner.
    private static async Task<IResult> WithdrawInvitationAsync(Guid invitationId, HttpContext http, AppDbContext db, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        var invitation = await db.TenantInvitations.SingleOrDefaultAsync(x => x.Id == invitationId && x.TenantId == tenant.TenantId, http.RequestAborted);
        if (invitation is null) return Results.NoContent();
        if (invitation.Role == MembershipRole.Owner && !IsOwner(tenant)) return Refused("OWNER_PROTECTED", "Sahip rolündeki bir daveti yalnız bir sahip geri çekebilir.");
        var now = time.GetUtcNow();
        if (await db.TenantInvitations.Where(x => x.Id == invitation.Id).ExecuteDeleteAsync(http.RequestAborted) == 1)
        {
            Audit(db, http, tenant, InvitationWithdrawn, invitation.UserId, $"role={RoleWire(invitation.Role)}; invitation={invitation.Id:D}", now);
            await db.SaveChangesAsync(http.RequestAborted);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> CreateAsync(CreateRequest request, HttpContext http, AppDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        if (RequireIdempotency(http) is { } missingKey) return missingKey;
        var email = request.Email?.Trim() ?? string.Empty;
        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (!IsEmail(email)) return Invalid("Geçerli bir e-posta adresi girin.");
        if (displayName.Length is 0 or > 160) return Invalid("Ad Soyad 1-160 karakter olmalıdır.");
        if (ParseRole(request.Role) is not { } role) return Invalid("Geçerli bir rol seçin.");
        if (role == MembershipRole.Owner && !IsOwner(tenant)) return Refused("OWNER_ROLE_RESERVED", "Sahip rolünü yalnız bir sahip verebilir.");
        if (await ReauthAsync(request.Password, request.Code, request.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;

        var now = time.GetUtcNow();
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            if (await db.TenantMemberships.AnyAsync(x => x.TenantId == tenant.TenantId && x.UserId == existing.Id, http.RequestAborted))
                return Conflict("USER_ALREADY_MEMBER", "Bu e-posta adresiyle bir kullanıcı bu çalışma alanında zaten var.");
            // API-GUVENLIK-6 (owner decision 16 Nov 2026): an existing account is invited, never added. The invitation grants
            // nothing until the person accepts it after signing in (WorkspaceInvitationEndpoints).
            var invitation = await db.TenantInvitations.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.UserId == existing.Id, http.RequestAborted);
            // A declined invitation answers like a pending one for TenantInvitation.DeclineQuiet: the inviter does not learn
            // it was declined, and the invited person is not asked again at every sign-in.
            if (invitation is not null && (invitation.DeclinedAt is { } declined ? declined > now - TenantInvitation.DeclineQuiet : invitation.ExpiresAt > now))
                return Conflict("INVITATION_PENDING", "Bu e-posta adresine bu çalışma alanından gönderilmiş, yanıt bekleyen bir davet var.");
            if (invitation is null)
            {
                invitation = new TenantInvitation { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, UserId = existing.Id, Email = email, DisplayName = displayName };
                db.TenantInvitations.Add(invitation);
            }
            // An expired (or long-declined) invitation is renewed in place: the same row, a new role, name and period.
            invitation.Role = role; invitation.Email = email; invitation.DisplayName = displayName; invitation.InvitedByUserId = tenant.UserId;
            invitation.CreatedAt = now; invitation.ExpiresAt = now.Add(TenantInvitation.Lifetime); invitation.DeclinedAt = null;
            Audit(db, http, tenant, Invited, existing.Id, $"role={RoleWire(role)}; invitation={invitation.Id:D}; expires={invitation.ExpiresAt:O}", now);
            try { await db.SaveChangesAsync(http.RequestAborted); }
            catch (DbUpdateException) { return Conflict("USER_ADD_FAILED", "Kullanıcı şu anda eklenemedi; listeyi yenileyip yeniden deneyin."); }
            return AddedResponse(null);
        }

        var temporary = BreakGlassService.TemporaryPassword();
        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName, ForcePasswordChange = true, CreatedAt = now, UpdatedAt = now };
        var created = await users.CreateAsync(user, temporary);
        if (!created.Succeeded)
        {
            // An account made by someone else meanwhile: the answer does not say that an account exists (API-GUVENLIK-6).
            return created.Errors.Any(x => x.Code is "DuplicateEmail" or "DuplicateUserName")
                ? Conflict("USER_ADD_FAILED", "Kullanıcı şu anda eklenemedi; listeyi yenileyip yeniden deneyin.")
                : Invalid("Kullanıcı oluşturulamadı: " + string.Join(", ", created.Errors.Select(x => x.Code)));
        }
        db.UserSecurities.Add(new UserSecurity { UserId = user.Id, TotpState = TotpState.Disabled });
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, UserId = user.Id, Role = role, CreatedAt = now, UpdatedAt = now });
        Audit(db, http, tenant, Created, user.Id, $"role={RoleWire(role)}; one-time-password", now);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        return AddedResponse(temporary);
    }

    // API-GUVENLIK-6: adding a user answers in one shape whether the e-mail was new or an existing account was invited:
    // no account id and no "existing account" flag. Only a new account carries the one-time password, because that is
    // how the inviter hands the new account over; so the body itself tells the inviter "an invitation was sent" (an
    // existing account) from "an account was made" (owner accepted, 16 Nov 2026). Nothing here hides that difference,
    // in the body or in timing; what stays hidden is the account itself, which joins nothing before it accepts.
    internal static IResult AddedResponse(string? temporaryPassword) => Results.Ok(new AddedBody(true, temporaryPassword));

    internal sealed record AddedBody(bool Added, string? TemporaryPassword);

    private static async Task<IResult> ChangeRoleAsync(Guid id, RoleRequest request, HttpContext http, AppDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        if (ParseRole(request.Role) is not { } role) return Invalid("Geçerli bir rol seçin.");
        if (await TargetAsync(id, tenant, db, http) is not { } membership) return ApiProblems.NotFound(http, "Kullanıcı bulunamadı.");
        if (Guard(tenant, membership) is { } refused) return refused;
        if (role == MembershipRole.Owner && !IsOwner(tenant)) return Refused("OWNER_ROLE_RESERVED", "Sahip rolünü yalnız bir sahip verebilir.");
        if (membership.Role == role) return Results.NoContent();
        if (membership.Role == MembershipRole.Owner && await IsLastActiveOwnerAsync(membership, db, http)) return Conflict("LAST_OWNER", "Çalışma alanının son etkin sahibinin rolü değiştirilemez.");
        if (await ReauthAsync(request.Password, request.Code, request.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        var ownerChange = membership.Role == MembershipRole.Owner;
        await using var transaction = ownerChange ? await LockMembershipsAsync(db, tenant.TenantId, http.RequestAborted) : await db.Database.BeginTransactionAsync(http.RequestAborted);
        if (ownerChange && await IsLastActiveOwnerAsync(membership, db, http)) return Conflict("LAST_OWNER", "Çalışma alanının son etkin sahibinin rolü değiştirilemez.");
        var now = time.GetUtcNow();
        var previous = membership.Role;
        membership.Role = role; membership.UpdatedAt = now; membership.Version++;
        Audit(db, http, tenant, RoleChanged, membership.UserId, $"from={RoleWire(previous)}; to={RoleWire(role)}", now);
        // Security review 16 Nov 2026: invitations the user can no longer give stop with the demotion. Below the elevated
        // roles all of them; an owner made administrator, those to the owner role.
        if (role is not (MembershipRole.Owner or MembershipRole.Administrator))
            await WithdrawInvitationsOfAsync(db, http, tenant, membership.UserId, ownerOnly: false, "inviter-demoted", now);
        else if (role == MembershipRole.Administrator && previous == MembershipRole.Owner)
            await WithdrawInvitationsOfAsync(db, http, tenant, membership.UserId, ownerOnly: true, "inviter-demoted", now);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeStatusAsync(Guid id, StatusRequest request, HttpContext http, AppDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        if (await TargetAsync(id, tenant, db, http) is not { } membership) return ApiProblems.NotFound(http, "Kullanıcı bulunamadı.");
        if (Guard(tenant, membership) is { } refused) return refused;
        var target = request.Active ? RecordStatus.Active : RecordStatus.Disabled;
        if (membership.Status == target) return Results.NoContent();
        if (!request.Active && membership.Role == MembershipRole.Owner && await IsLastActiveOwnerAsync(membership, db, http)) return Conflict("LAST_OWNER", "Çalışma alanının son etkin sahibi pasife alınamaz.");
        if (await ReauthAsync(request.Password, request.Code, request.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        var ownerChange = !request.Active && membership.Role == MembershipRole.Owner;
        await using var transaction = ownerChange ? await LockMembershipsAsync(db, tenant.TenantId, http.RequestAborted) : await db.Database.BeginTransactionAsync(http.RequestAborted);
        if (ownerChange && await IsLastActiveOwnerAsync(membership, db, http)) return Conflict("LAST_OWNER", "Çalışma alanının son etkin sahibi pasife alınamaz.");
        var now = time.GetUtcNow();
        membership.Status = target; membership.UpdatedAt = now; membership.Version++;
        var revoked = request.Active ? 0 : await RevokeWorkspaceSessionsAsync(membership.UserId, tenant.TenantId, db, now, http.RequestAborted);
        Audit(db, http, tenant, request.Active ? Enabled : Disabled, membership.UserId, request.Active ? "membership-active" : $"membership-disabled; sessions-revoked={revoked}", now);
        // Security review 16 Nov 2026: a deactivated user's outstanding invitations stop with them.
        if (!request.Active) await WithdrawInvitationsOfAsync(db, http, tenant, membership.UserId, ownerOnly: false, "inviter-disabled", now);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> ResetPasswordAsync(Guid id, ReauthRequest request, HttpContext http, AppDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        if (await TargetAsync(id, tenant, db, http) is not { } membership) return ApiProblems.NotFound(http, "Kullanıcı bulunamadı.");
        if (CredentialGuard(tenant, membership) is { } refused) return refused;
        if (await SharedAccountAsync(membership, db, http)) return Conflict("USER_IN_OTHER_WORKSPACE", "Bu hesap başka bir çalışma alanına da üye; parolası buradan sıfırlanamaz.");
        if (await ReauthAsync(request.Password, request.Code, request.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        var user = await users.FindByIdAsync(membership.UserId.ToString());
        if (user is null) return ApiProblems.NotFound(http, "Kullanıcı bulunamadı.");
        var temporary = BreakGlassService.TemporaryPassword();
        var now = time.GetUtcNow();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, temporary);
        if (!reset.Succeeded) return Invalid("Parola sıfırlanamadı: " + string.Join(", ", reset.Errors.Select(x => x.Code)));
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        // The user store saved the row; read it again before changing it.
        await db.Entry(user).ReloadAsync(http.RequestAborted);
        user.ForcePasswordChange = true; user.SessionVersion++; user.UpdatedAt = now;
        var revoked = await RevokeAllSessionsAsync(user.Id, db, now, http.RequestAborted);
        Audit(db, http, tenant, PasswordReset, user.Id, $"one-time-password; sessions-revoked={revoked}", now);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { temporaryPassword = temporary });
    }

    private static async Task<IResult> ResetMfaAsync(Guid id, ReauthRequest request, HttpContext http, AppDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        if (await TargetAsync(id, tenant, db, http) is not { } membership) return ApiProblems.NotFound(http, "Kullanıcı bulunamadı.");
        if (CredentialGuard(tenant, membership) is { } refused) return refused;
        if (await SharedAccountAsync(membership, db, http)) return Conflict("USER_IN_OTHER_WORKSPACE", "Bu hesap başka bir çalışma alanına da üye; iki adımlı doğrulaması buradan sıfırlanamaz.");
        if (await ReauthAsync(request.Password, request.Code, request.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var user = await db.Users.SingleAsync(x => x.Id == membership.UserId, http.RequestAborted);
        var security = await db.UserSecurities.SingleOrDefaultAsync(x => x.UserId == user.Id, http.RequestAborted);
        if (security is null) db.UserSecurities.Add(new UserSecurity { UserId = user.Id, TotpState = TotpState.Disabled });
        else
        {
            security.TotpState = TotpState.Disabled; security.ProtectedTotpSecret = null; security.EnrollmentExpiresAt = null; security.PendingProtectedTotpSecret = null;
            security.PendingEnrollmentExpiresAt = null; security.LastAcceptedTimeStep = null; security.RecoveryBatchId = null; security.Version++;
        }
        await db.RecoveryCodes.Where(x => x.UserId == user.Id && x.InvalidatedAt == null).ExecuteUpdateAsync(x => x.SetProperty(c => c.InvalidatedAt, now), http.RequestAborted);
        user.SessionVersion++; user.UpdatedAt = now;
        var revoked = await RevokeAllSessionsAsync(user.Id, db, now, http.RequestAborted);
        Audit(db, http, tenant, MfaReset, user.Id, $"authenticator-and-recovery-codes-removed; sessions-revoked={revoked}", now);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeSessionsAsync(Guid id, ReauthRequest request, HttpContext http, AppDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        if (await TargetAsync(id, tenant, db, http) is not { } membership) return ApiProblems.NotFound(http, "Kullanıcı bulunamadı.");
        if (Guard(tenant, membership) is { } refused) return refused;
        if (await ReauthAsync(request.Password, request.Code, request.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        var now = time.GetUtcNow();
        var revoked = await RevokeWorkspaceSessionsAsync(membership.UserId, tenant.TenantId, db, now, http.RequestAborted);
        Audit(db, http, tenant, SessionsRevoked, membership.UserId, $"sessions-revoked={revoked}", now);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { revoked });
    }

    // Removes the inviter's outstanding (not declined) invitations of this workspace, each audited as withdrawn. Declined
    // rows stay: they only hold the re-invite quiet period and grant nothing.
    private static async Task<int> WithdrawInvitationsOfAsync(AppDbContext db, HttpContext http, TenantContext actor, Guid inviterId, bool ownerOnly, string reason, DateTimeOffset now)
    {
        var outstanding = await db.TenantInvitations
            .Where(x => x.TenantId == actor.TenantId && x.InvitedByUserId == inviterId && x.DeclinedAt == null && (!ownerOnly || x.Role == MembershipRole.Owner))
            .ToListAsync(http.RequestAborted);
        foreach (var invitation in outstanding)
        {
            db.TenantInvitations.Remove(invitation);
            Audit(db, http, actor, InvitationWithdrawn, invitation.UserId, $"role={RoleWire(invitation.Role)}; invitation={invitation.Id:D}; {reason}; inviter={inviterId:D}", now);
        }
        return outstanding.Count;
    }

    // Who may change whom, before anything is checked or counted.
    internal static IResult? Guard(TenantContext actor, TenantMembership target)
    {
        if (target.UserId == actor.UserId) return Refused("SELF_CHANGE_REFUSED", "Kendi hesabınızı buradan değiştiremezsiniz; Güvenlik ayarlarını kullanın.");
        if (target.Role == MembershipRole.Owner && !IsOwner(actor)) return Refused("OWNER_PROTECTED", "Sahip hesabını yalnız bir sahip değiştirebilir.");
        return null;
    }

    // A password or second-factor reset hands the actor the target's sign-in: an administrator may do it for the roles
    // below it only, never for another administrator (scan 25 Nov 2026, E-5); an owner may for anyone but themself.
    internal static IResult? CredentialGuard(TenantContext actor, TenantMembership target)
    {
        if (Guard(actor, target) is { } refused) return refused;
        if (target.Role == MembershipRole.Administrator && !IsOwner(actor)) return Refused("ADMIN_PROTECTED", "Bir yöneticinin parolasını veya iki adımlı doğrulamasını yalnız bir sahip sıfırlayabilir.");
        return null;
    }

    private static Task<TenantMembership?> TargetAsync(Guid userId, TenantContext tenant, AppDbContext db, HttpContext http) =>
        db.TenantMemberships.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.UserId == userId, http.RequestAborted);

    // API-GUVENLIK-5: demoting or disabling an owner re-checks "another active owner remains" under a lock on the
    // workspace's memberships, held until the change commits. Two owners demoting each other at once used to pass the
    // check both (it ran before the seconds-long re-authentication) and leave the workspace without an owner; now the
    // second waits for the first and sees its result. The rows are locked in one order, so two changes cannot deadlock.
    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> LockMembershipsAsync(AppDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM iam.tenant_memberships WHERE \"TenantId\" = {tenantId} ORDER BY \"Id\" FOR UPDATE", cancellationToken);
        return transaction;
    }

    // An active owner is one whose membership and account are both active.
    private static async Task<bool> IsLastActiveOwnerAsync(TenantMembership membership, AppDbContext db, HttpContext http) =>
        !await db.TenantMemberships.AnyAsync(x => x.TenantId == membership.TenantId && x.UserId != membership.UserId && x.Role == MembershipRole.Owner && x.Status == RecordStatus.Active
            && db.Users.Any(user => user.Id == x.UserId && user.Status == "ACTIVE"), http.RequestAborted);

    private static Task<bool> SharedAccountAsync(TenantMembership membership, AppDbContext db, HttpContext http) =>
        db.TenantMemberships.AnyAsync(x => x.UserId == membership.UserId && x.TenantId != membership.TenantId && x.Status == RecordStatus.Active, http.RequestAborted);

    // Sessions in this workspace, and those still before a workspace was chosen.
    private static Task<int> RevokeWorkspaceSessionsAsync(Guid userId, Guid tenantId, AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.UserSessions.Where(x => x.UserId == userId && (x.TenantId == tenantId || x.TenantId == null) && x.State != SessionState.Revoked)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, now), cancellationToken);

    private static Task<int> RevokeAllSessionsAsync(Guid userId, AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.UserSessions.Where(x => x.UserId == userId && x.State != SessionState.Revoked)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, now), cancellationToken);

    private static Task<IResult?> ReauthAsync(string? password, string? code, string? recoveryCode, HttpContext http, UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time) =>
        AuthEndpoints.ReauthenticateAsync(new AuthEndpoints.ReauthenticateRequest(password ?? string.Empty, code, recoveryCode), http, users, db, protection, totp, hasher, time);

    private static void Audit(AppDbContext db, HttpContext http, TenantContext actor, string action, Guid targetUserId, string reason, DateTimeOffset now)
    {
        var detail = $"{reason}; ip={http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = actor.TenantId,
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = "User",
            TargetId = targetUserId.ToString("D"),
            Reason = detail.Length > 512 ? detail[..512] : detail,
            CorrelationId = http.TraceIdentifier,
            CreatedAt = now
        });
    }

    internal static MembershipRole? ParseRole(string? role) => role?.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
    {
        "OWNER" => MembershipRole.Owner,
        "ADMINISTRATOR" => MembershipRole.Administrator,
        "OPERATIONS" => MembershipRole.Operations,
        "ACCOUNTING" => MembershipRole.Accounting,
        "READONLY" => MembershipRole.ReadOnly,
        _ => null
    };

    internal static string RoleWire(MembershipRole role) => role.ToString().ToUpperInvariant();

    private static bool IsOwner(TenantContext tenant) => string.Equals(tenant.Role, "OWNER", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmail(string value)
    {
        if (value.Length is < 3 or > 256 || value.Contains(' ', StringComparison.Ordinal)) return false;
        return MailAddress.TryCreate(value, out var parsed) && string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase) && parsed.Host.Contains('.', StringComparison.Ordinal);
    }

    private static IResult Invalid(string message) => ApiProblems.Of(null, "INVALID_REQUEST", message, 400);
    private static IResult Refused(string code, string message) => ApiProblems.Of(null, code, message, 403);
    private static IResult Conflict(string code, string message) => ApiProblems.Of(null, code, message, 409);
}
