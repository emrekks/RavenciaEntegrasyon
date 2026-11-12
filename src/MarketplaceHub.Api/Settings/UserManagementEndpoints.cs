using System.Net.Mail;
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

namespace MarketplaceHub.Api.Settings;

// Ayarlar › Kullanıcılar: the workspace's users, managed by the owner and administrators only
// (RoleAuthorizationMiddleware: every /api/v1/settings/users route is Elevated, reads included).
//   GET  /api/v1/settings/users                          name, e-mail, role, 2FA, last sign-in, state
//   POST /api/v1/settings/users                          add: a new account gets a one-time password (shown once,
//                                                        changed at the first sign-in); an existing account joins
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
    public const string MembershipAdded = "USER_MEMBERSHIP_ADDED";
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
        var group = endpoints.MapGroup("/api/v1/settings/users");
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit);
        group.MapPut("/{id:guid}/role", ChangeRoleAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit);
        group.MapPut("/{id:guid}/status", ChangeStatusAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit);
        group.MapPost("/{id:guid}/password-reset", ResetPasswordAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit);
        group.MapPost("/{id:guid}/mfa-reset", ResetMfaAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit);
        group.MapPost("/{id:guid}/sessions/revoke", RevokeSessionsAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit);
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
        return Results.Ok(new { currentUserId = tenant.UserId, currentRole = tenant.Role, items });
    }

    private static async Task<IResult> CreateAsync(CreateRequest request, HttpContext http, AppDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
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
            db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, UserId = existing.Id, Role = role, CreatedAt = now, UpdatedAt = now });
            Audit(db, http, tenant, MembershipAdded, existing.Id, $"role={RoleWire(role)}; existing-account", now);
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Ok(new { userId = existing.Id, existingAccount = true, temporaryPassword = (string?)null });
        }

        var temporary = BreakGlassService.TemporaryPassword();
        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName, ForcePasswordChange = true, CreatedAt = now, UpdatedAt = now };
        var created = await users.CreateAsync(user, temporary);
        if (!created.Succeeded)
        {
            return created.Errors.Any(x => x.Code is "DuplicateEmail" or "DuplicateUserName")
                ? Conflict("USER_ALREADY_EXISTS", "Bu e-posta adresiyle bir hesap zaten var.")
                : Invalid("Kullanıcı oluşturulamadı: " + string.Join(", ", created.Errors.Select(x => x.Code)));
        }
        db.UserSecurities.Add(new UserSecurity { UserId = user.Id, TotpState = TotpState.Disabled });
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = tenant.TenantId, UserId = user.Id, Role = role, CreatedAt = now, UpdatedAt = now });
        Audit(db, http, tenant, Created, user.Id, $"role={RoleWire(role)}; one-time-password", now);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        return Results.Ok(new { userId = user.Id, existingAccount = false, temporaryPassword = temporary });
    }

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
        var now = time.GetUtcNow();
        var previous = membership.Role;
        membership.Role = role; membership.UpdatedAt = now; membership.Version++;
        Audit(db, http, tenant, RoleChanged, membership.UserId, $"from={RoleWire(previous)}; to={RoleWire(role)}", now);
        await db.SaveChangesAsync(http.RequestAborted);
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
        var now = time.GetUtcNow();
        membership.Status = target; membership.UpdatedAt = now; membership.Version++;
        var revoked = request.Active ? 0 : await RevokeWorkspaceSessionsAsync(membership.UserId, tenant.TenantId, db, now, http.RequestAborted);
        Audit(db, http, tenant, request.Active ? Enabled : Disabled, membership.UserId, request.Active ? "membership-active" : $"membership-disabled; sessions-revoked={revoked}", now);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> ResetPasswordAsync(Guid id, ReauthRequest request, HttpContext http, AppDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant) return ApiProblems.Unauthenticated(http);
        if (await TargetAsync(id, tenant, db, http) is not { } membership) return ApiProblems.NotFound(http, "Kullanıcı bulunamadı.");
        if (Guard(tenant, membership) is { } refused) return refused;
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
        if (Guard(tenant, membership) is { } refused) return refused;
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

    // Who may change whom, before anything is checked or counted.
    internal static IResult? Guard(TenantContext actor, TenantMembership target)
    {
        if (target.UserId == actor.UserId) return Refused("SELF_CHANGE_REFUSED", "Kendi hesabınızı buradan değiştiremezsiniz; Güvenlik ayarlarını kullanın.");
        if (target.Role == MembershipRole.Owner && !IsOwner(actor)) return Refused("OWNER_PROTECTED", "Sahip hesabını yalnız bir sahip değiştirebilir.");
        return null;
    }

    private static Task<TenantMembership?> TargetAsync(Guid userId, TenantContext tenant, AppDbContext db, HttpContext http) =>
        db.TenantMemberships.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.UserId == userId, http.RequestAborted);

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

    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static IResult Invalid(string message) => ApiProblems.Of(null, "INVALID_REQUEST", message, 400);
    private static IResult Refused(string code, string message) => ApiProblems.Of(null, code, message, 403);
    private static IResult Conflict(string code, string message) => ApiProblems.Of(null, code, message, 409);
}
