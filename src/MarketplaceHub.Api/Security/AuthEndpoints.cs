using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace MarketplaceHub.Api.Security;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth");
        group.MapGet("/csrf", (HttpResponse response) => Results.Ok(new { token = RequestSecurityMiddleware.IssueCsrf(response) }));
        group.MapPost("/login", LoginAsync).RequireRateLimiting("auth");
        group.MapPost("/logout", LogoutAsync);
        group.MapPost("/change-password", ChangePasswordAsync);
        group.MapPost("/reauthenticate", ReauthenticateAsync).RequireRateLimiting("auth");
        group.MapGet("/me", MeAsync);
        group.MapGet("/security-status", SecurityStatusAsync);
        group.MapGet("/sessions", SessionsAsync);
        group.MapPost("/mfa/setup", MfaSetupAsync);
        group.MapPost("/mfa/confirm", MfaConfirmAsync);
        group.MapPost("/mfa/challenge", MfaChallengeAsync).RequireRateLimiting("auth");
        group.MapPost("/mfa/disable", MfaDisableAsync);
        group.MapPost("/recovery-codes/regenerate", RecoveryRegenerateAsync);
        group.MapPost("/sessions/{id:guid}/revoke", RevokeSessionAsync);
        group.MapPost("/sessions/revoke-others", RevokeOthersAsync);
        group.MapDelete("/sessions/{id:guid}", DeleteClosedSessionAsync);
        group.MapDelete("/sessions/closed", DeleteClosedSessionsAsync);
        return group;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, TokenHasher hasher, TimeProvider time)
    {
        // A body without the fields used to reach request.Email.Trim() and fail with 500.
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password)) return InvalidBody("E-posta ve parola zorunludur.");
        var user = await users.FindByEmailAsync(request.Email.Trim());
        var now = time.GetUtcNow();
        if (user is null || user.Status != "ACTIVE" || await users.IsLockedOutAsync(user))
        {
            // The typed e-mail is not recorded: users sometimes type their password into it.
            AuthAudit.Record(db, context, AuthAudit.LoginFailed, user?.Id, null, user is null ? "unknown-account" : user.Status != "ACTIVE" ? "inactive-account" : "locked-out", now);
            await db.SaveChangesAsync(context.RequestAborted);
            return InvalidCredentials();
        }
        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            AuthAudit.Record(db, context, AuthAudit.LoginFailed, user.Id, null, "wrong-password", now);
            if (await users.IsLockedOutAsync(user)) AuthAudit.Record(db, context, AuthAudit.AccountLockedOut, user.Id, null, "failed-sign-ins", now);
            await db.SaveChangesAsync(context.RequestAborted);
            return InvalidCredentials();
        }
        await users.ResetAccessFailedCountAsync(user);
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, context.RequestAborted);
        var state = user.ForcePasswordChange ? SessionState.PasswordChangeRequired : security.TotpState == TotpState.Enabled ? SessionState.MfaChallenge : SessionState.Active;
        var memberships = await ActiveTenantMemberships(db, user.Id).OrderBy(x => x.CreatedAt).ToListAsync(context.RequestAborted);
        var membership = request.TenantId is Guid requestedTenant
            ? memberships.SingleOrDefault(x => x.TenantId == requestedTenant)
            : memberships.Count == 1 ? memberships[0] : null;
        if (membership is null)
        {
            if (memberships.Count == 0)
            {
                AuthAudit.Record(db, context, AuthAudit.LoginFailed, user.Id, null, "no-active-workspace", now);
                await db.SaveChangesAsync(context.RequestAborted);
                return Problem("TENANT_NOT_AVAILABLE", "Kullanıcı için aktif çalışma alanı bulunamadı.", 403);
            }
            var tenantIds = memberships.Select(x => x.TenantId).ToArray();
            var tenants = await db.Tenants.AsNoTracking().Where(x => x.Status == RecordStatus.Active && tenantIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, context.RequestAborted);
            return ApiProblems.Of(context, "TENANT_SELECTION_REQUIRED", "Devam etmek için bir çalışma alanı seçin.", 409, new Dictionary<string, object?> { ["tenants"] = memberships.Select(x => new { id = x.TenantId, displayName = tenants.GetValueOrDefault(x.TenantId)?.DisplayName ?? x.TenantId.ToString("D") }).ToList() });
        }
        var raw = TokenHasher.NewToken(); var sessionId = Guid.NewGuid();
        AuthAudit.Record(db, context, AuthAudit.LoginSucceeded, user.Id, membership.TenantId, $"state={SessionStateWire(state)}", now, sessionId);
        db.UserSessions.Add(new UserSession { Id = sessionId, UserId = user.Id, TenantId = membership.TenantId, State = state, TokenHash = hasher.Hash(raw), SessionVersion = user.SessionVersion, IssuedAt = now, ReauthenticatedAt = now, LastSeenAt = now, ExpiresAt = now.AddMinutes(30), AbsoluteExpiresAt = now.AddHours(12) });
        user.LastLoginAt = now; await db.SaveChangesAsync(context.RequestAborted);
        SetSessionCookie(context.Response, raw, now.AddHours(12));
        return Results.Ok(new { state = SessionStateWire(state) });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, AppDbContext db, TimeProvider time)
    {
        if (CurrentSession(context) is { } session)
        {
            session.State = SessionState.Revoked; session.RevokedAt = time.GetUtcNow();
            AuthAudit.Record(db, context, AuthAudit.SessionRevoked, session.UserId, session.TenantId, "logout", session.RevokedAt.Value, session.Id);
            await db.SaveChangesAsync(context.RequestAborted);
        }
        context.Response.Cookies.Delete(SessionAuthMiddleware.CookieName(context), new CookieOptions { Secure = SecureCookie(context), Path = "/" });
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, TokenHasher hasher, TimeProvider time)
    {
        var session = RequireSession(context, SessionState.PasswordChangeRequired, SessionState.Active); if (session is null) return Forbidden();
        if (string.IsNullOrEmpty(request.CurrentPassword) || request.NewPassword is null) return InvalidBody("Mevcut ve yeni parola zorunludur.");
        if (request.NewPassword.Length is < 15 or > 64 || WeakPasswords.Contains(request.NewPassword)) return Problem("PASSWORD_POLICY_FAILED", "Yeni parola 15-64 karakter olmalı ve yaygın parolalardan biri olmamalıdır.", 400);
        var user = await users.FindByIdAsync(session.UserId.ToString()); if (user is null) return Forbidden();
        if (session.TenantId is Guid currentTenantId && !await HasActiveTenantMembershipAsync(db, user.Id, currentTenantId, context.RequestAborted))
            return Problem("TENANT_NOT_AVAILABLE", "Aktif çalışma alanı bulunamadı.", 403);
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            AuthAudit.Record(db, context, AuthAudit.PasswordChangeFailed, user.Id, session.TenantId, string.Join(',', result.Errors.Select(x => x.Code)), time.GetUtcNow(), session.Id);
            await db.SaveChangesAsync(context.RequestAborted);
            return ApiProblems.Of(context, new("PASSWORD_CHANGE_FAILED", "Parola değiştirilemedi; mevcut parolayı ve yeni parola kurallarını kontrol edin.", 400, result.Errors.ToDictionary(x => x.Code, x => new[] { x.Description })));
        }
        AuthAudit.Record(db, context, AuthAudit.PasswordChanged, user.Id, session.TenantId, "other-sessions-revoked", time.GetUtcNow(), session.Id);
        user.ForcePasswordChange = false; user.SessionVersion++;
        await db.UserSessions.Where(x => x.UserId == user.Id && x.Id != session.Id && x.State != SessionState.Revoked).ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, time.GetUtcNow()), context.RequestAborted);
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, context.RequestAborted);
        session.SessionVersion = user.SessionVersion; session.ReauthenticatedAt = time.GetUtcNow(); session.State = security.TotpState == TotpState.Enabled ? SessionState.MfaChallenge : SessionState.Active;
        if (session.TenantId is null)
        {
            var membership = await ActiveTenantMemberships(db, user.Id).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(context.RequestAborted);
            if (membership is null) return Problem("TENANT_NOT_AVAILABLE", "Aktif çalışma alanı bulunamadı.", 403);
            session.TenantId = membership.TenantId;
        }
        var raw = TokenHasher.NewToken(); session.TokenHash = hasher.Hash(raw); await db.SaveChangesAsync(context.RequestAborted); SetSessionCookie(context.Response, raw, session.AbsoluteExpiresAt);
        return Results.Ok(new { state = SessionStateWire(session.State) });
    }

    private static async Task<IResult> ReauthenticateAsync(ReauthenticateRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        var session = RequireSession(context, SessionState.Active); if (session is null) return Forbidden();
        var user = await users.FindByIdAsync(session.UserId.ToString());
        if (user is null || !await PasswordCheck.VerifyAsync(users, user, request.Password)) return await PasswordRecheckFailedAsync(session, user, users, db, context, time);
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == session.UserId, context.RequestAborted);
        if (security.TotpState == TotpState.Enabled && !await ValidateSecondFactorAsync(request, session.UserId, security, context, db, protection, totp, hasher, time))
            return await SecondFactorFailedAsync(session, user, users, db, context, time, SecondFactorMethod(request));
        MfaAttemptPolicy.RegisterSuccess(session);
        session.ReauthenticatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(context.RequestAborted);
        return Results.Ok(new { reauthenticatedAt = session.ReauthenticatedAt });
    }

    private static async Task<IResult> MeAsync(HttpContext context, AppDbContext db)
    {
        var session = CurrentSession(context); if (session is null) return ApiProblems.Of(context, "AUTHENTICATION_REQUIRED", "Oturum bulunamadı.", 401);
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == session.UserId, context.RequestAborted);
        Guid? activeTenantId = session.State == SessionState.Active && Guid.TryParse(context.User.FindFirstValue("tenant_id"), out var tenantId) ? tenantId : null;
        return Results.Ok(new { user.Id, user.Email, user.DisplayName, role = context.User.FindFirstValue(ClaimTypes.Role), state = SessionStateWire(session.State), tenantId = activeTenantId });
    }

    private static async Task<IResult> SecurityStatusAsync(HttpContext context, AppDbContext db)
    {
        var session = RequireSession(context, SessionState.Active); if (session is null) return Forbidden();
        var security = await db.UserSecurities.AsNoTracking().SingleAsync(x => x.UserId == session.UserId, context.RequestAborted);
        var remaining = await db.RecoveryCodes.CountAsync(x => x.UserId == session.UserId && x.UsedAt == null && x.InvalidatedAt == null, context.RequestAborted);
        return Results.Ok(new { totpState = security.TotpState.ToString().ToUpperInvariant(), recoveryCodesRemaining = remaining });
    }

    private static async Task<IResult> SessionsAsync(HttpContext context, AppDbContext db)
    {
        var session = RequireSession(context, SessionState.Active); if (session is null) return Forbidden();
        var sessions = await db.UserSessions.AsNoTracking().Where(x => x.UserId == session.UserId).OrderByDescending(x => x.IssuedAt)
            .Select(x => new { x.Id, x.State, x.IssuedAt, x.LastSeenAt, x.ExpiresAt, current = x.Id == session.Id }).ToListAsync(context.RequestAborted);
        var response = sessions.Select(x => new { x.Id, state = SessionStateWire(x.State), x.IssuedAt, x.LastSeenAt, x.ExpiresAt, x.current });
        return Results.Ok(response);
    }

    private static async Task<IResult> MfaSetupAsync(HttpContext context, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TimeProvider time)
    {
        var session = RequireRecentActive(context, time); if (session is null) return Forbidden();
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == session.UserId, context.RequestAborted);
        var secret = totp.NewSecret(); var base32 = Base32(secret); var expires = time.GetUtcNow().AddMinutes(10);
        var protectedSecret = protection.CreateProtector("MarketplaceHub.Totp.v1").Protect(Convert.ToBase64String(secret));
        if (security.TotpState == TotpState.Enabled)
        {
            // Keep the currently enforced factor active while a replacement is
            // being scanned. Abandoning setup must never downgrade MFA.
            security.PendingProtectedTotpSecret = protectedSecret;
            security.PendingEnrollmentExpiresAt = expires;
        }
        else
        {
            security.TotpState = TotpState.Pending;
            security.ProtectedTotpSecret = null;
            security.EnrollmentExpiresAt = null;
            security.PendingProtectedTotpSecret = protectedSecret;
            security.PendingEnrollmentExpiresAt = expires;
        }
        security.Version++;
        await db.SaveChangesAsync(context.RequestAborted);
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == session.UserId, context.RequestAborted);
        var uri = $"otpauth://totp/MarketplaceHub:{Uri.EscapeDataString(user.Email ?? user.Id.ToString())}?secret={base32}&issuer=MarketplaceHub&digits=6&period=30";
        using var generator = new QRCodeGenerator(); using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q); var svg = new SvgQRCode(data).GetGraphic(4);
        return Results.Ok(new { otpauthUri = uri, qrSvg = svg, expiresAt = expires });
    }

    private static async Task<IResult> MfaConfirmAsync(CodeRequest request, HttpContext context, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        var session = RequireSession(context, SessionState.Active); if (session is null) return Forbidden();
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == session.UserId, context.RequestAborted);
        var pendingSecret = security.PendingProtectedTotpSecret ?? (security.TotpState == TotpState.Pending ? security.ProtectedTotpSecret : null);
        var pendingExpiresAt = security.PendingEnrollmentExpiresAt ?? (security.TotpState == TotpState.Pending ? security.EnrollmentExpiresAt : null);
        if (pendingExpiresAt <= time.GetUtcNow() || pendingSecret is null) return Problem("MFA_ENROLLMENT_NOT_PENDING", "Bekleyen bir doğrulayıcı kurulumu yok.", 409);
        var secret = Convert.FromBase64String(protection.CreateProtector("MarketplaceHub.Totp.v1").Unprotect(pendingSecret));
        if (!totp.TryValidate(secret, request.Code ?? string.Empty, null, out var step)) return Problem("MFA_CODE_INVALID", "Doğrulama kodu geçersiz.", 400);
        await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
        security.TotpState = TotpState.Enabled; security.ProtectedTotpSecret = pendingSecret; security.EnrollmentExpiresAt = null; security.PendingProtectedTotpSecret = null; security.PendingEnrollmentExpiresAt = null; security.LastAcceptedTimeStep = step; security.Version++;
        session.ReauthenticatedAt = time.GetUtcNow();
        AuthAudit.Record(db, context, AuthAudit.MfaEnabled, session.UserId, session.TenantId, "totp", time.GetUtcNow(), session.Id);
        var codes = ReplaceRecoveryCodes(db, security, hasher, time.GetUtcNow()); await db.SaveChangesAsync(context.RequestAborted); await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> MfaChallengeAsync(CodeRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        var session = RequireSession(context, SessionState.MfaChallenge); if (session is null) return Forbidden();
        if (session.TenantId is Guid currentTenantId && !await HasActiveTenantMembershipAsync(db, session.UserId, currentTenantId, context.RequestAborted))
            return Problem("TENANT_NOT_AVAILABLE", "Aktif çalışma alanı bulunamadı.", 403);
        var user = await users.FindByIdAsync(session.UserId.ToString()); if (user is null) return Forbidden();
        // An account locked meanwhile (wrong passwords or codes elsewhere) cannot finish signing in.
        if (await users.IsLockedOutAsync(user)) return await EndSessionAfterFailuresAsync(session, db, context, time, "account-locked-out");
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == session.UserId, context.RequestAborted);
        var accepted = false;
        if (request.RecoveryCode is { Length: > 0 })
        {
            var digest = hasher.Hash(NormalizeRecovery(request.RecoveryCode));
            var consumed = await db.RecoveryCodes.Where(x => x.UserId == session.UserId && x.CodeDigest == digest && x.UsedAt == null && x.InvalidatedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.UsedAt, time.GetUtcNow()), context.RequestAborted);
            accepted = consumed == 1;
        }
        else if (security.TotpState == TotpState.Enabled && security.ProtectedTotpSecret is not null)
        {
            var secret = Convert.FromBase64String(protection.CreateProtector("MarketplaceHub.Totp.v1").Unprotect(security.ProtectedTotpSecret));
            if (totp.TryValidate(secret, request.Code ?? string.Empty, security.LastAcceptedTimeStep, out var step))
            {
                var updated = await db.UserSecurities.Where(x => x.UserId == session.UserId && (x.LastAcceptedTimeStep == null || x.LastAcceptedTimeStep < step))
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastAcceptedTimeStep, step).SetProperty(x => x.Version, x => x.Version + 1), context.RequestAborted);
                accepted = updated == 1;
            }
        }
        if (!accepted)
            return await SecondFactorFailedAsync(session, user, users, db, context, time, request.RecoveryCode is { Length: > 0 } ? "recovery-code" : "totp",
                Problem("MFA_CHALLENGE_INVALID", "Doğrulama kodu veya kurtarma kodu geçersiz.", 400));
        if (session.TenantId is null)
        {
            var membership = await ActiveTenantMemberships(db, session.UserId).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(context.RequestAborted);
            if (membership is null) return Problem("TENANT_NOT_AVAILABLE", "Aktif çalışma alanı bulunamadı.", 403);
            session.TenantId = membership.TenantId;
        }
        MfaAttemptPolicy.RegisterSuccess(session);
        await users.ResetAccessFailedCountAsync(user);
        session.State = SessionState.Active; session.ReauthenticatedAt = time.GetUtcNow();
        AuthAudit.Record(db, context, AuthAudit.MfaSucceeded, session.UserId, session.TenantId, request.RecoveryCode is { Length: > 0 } ? "recovery-code" : "totp", session.ReauthenticatedAt.Value, session.Id);
        await db.SaveChangesAsync(context.RequestAborted); return Results.Ok(new { state = SessionStateWire(session.State) });
    }

    private static async Task<IResult> MfaDisableAsync(ReauthenticateRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        var session = RequireSession(context, SessionState.Active); if (session is null) return Forbidden();
        var user = await users.FindByIdAsync(session.UserId.ToString()); if (user is null || !await PasswordCheck.VerifyAsync(users, user, request.Password)) return await PasswordRecheckFailedAsync(session, user, users, db, context, time);
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, context.RequestAborted);
        if (security.TotpState == TotpState.Enabled && !await ValidateSecondFactorAsync(request, session.UserId, security, context, db, protection, totp, hasher, time))
            return await SecondFactorFailedAsync(session, user, users, db, context, time, SecondFactorMethod(request));
        MfaAttemptPolicy.RegisterSuccess(session);
        session.ReauthenticatedAt = time.GetUtcNow();
        AuthAudit.Record(db, context, AuthAudit.MfaDisabled, user.Id, session.TenantId, "other-sessions-revoked", session.ReauthenticatedAt.Value, session.Id);
        security.TotpState = TotpState.Disabled; security.ProtectedTotpSecret = null; security.EnrollmentExpiresAt = null; security.PendingProtectedTotpSecret = null; security.PendingEnrollmentExpiresAt = null; security.LastAcceptedTimeStep = null; security.RecoveryBatchId = null; security.Version++;
        await db.RecoveryCodes.Where(x => x.UserId == user.Id && x.InvalidatedAt == null).ExecuteUpdateAsync(x => x.SetProperty(c => c.InvalidatedAt, time.GetUtcNow()), context.RequestAborted);
        user.SessionVersion++; session.SessionVersion = user.SessionVersion;
        await db.UserSessions.Where(x => x.UserId == user.Id && x.Id != session.Id && x.State != SessionState.Revoked).ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, time.GetUtcNow()), context.RequestAborted);
        await db.SaveChangesAsync(context.RequestAborted); return Results.NoContent();
    }

    private static async Task<IResult> RecoveryRegenerateAsync(ReauthenticateRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        var session = RequireSession(context, SessionState.Active); if (session is null) return Forbidden();
        var user = await users.FindByIdAsync(session.UserId.ToString()); if (user is null || !await PasswordCheck.VerifyAsync(users, user, request.Password)) return await PasswordRecheckFailedAsync(session, user, users, db, context, time);
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, context.RequestAborted); if (security.TotpState != TotpState.Enabled) return Problem("MFA_NOT_ENABLED", "İki adımlı doğrulama açık değil.", 409);
        if (!await ValidateSecondFactorAsync(request, session.UserId, security, context, db, protection, totp, hasher, time))
            return await SecondFactorFailedAsync(session, user, users, db, context, time, SecondFactorMethod(request));
        MfaAttemptPolicy.RegisterSuccess(session);
        session.ReauthenticatedAt = time.GetUtcNow();
        AuthAudit.Record(db, context, AuthAudit.RecoveryCodesRegenerated, user.Id, session.TenantId, "previous-codes-invalidated", session.ReauthenticatedAt.Value, session.Id);
        await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
        await db.RecoveryCodes.Where(x => x.UserId == user.Id && x.InvalidatedAt == null).ExecuteUpdateAsync(x => x.SetProperty(c => c.InvalidatedAt, time.GetUtcNow()), context.RequestAborted);
        var codes = ReplaceRecoveryCodes(db, security, hasher, time.GetUtcNow()); await db.SaveChangesAsync(context.RequestAborted); await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> RevokeSessionAsync(Guid id, HttpContext context, AppDbContext db, TimeProvider time)
    {
        var current = RequireSession(context, SessionState.Active); if (current is null) return Forbidden();
        var target = await db.UserSessions.SingleOrDefaultAsync(x => x.Id == id && x.UserId == current.UserId, context.RequestAborted); if (target is null) return ApiProblems.NotFound(context, "Oturum bulunamadı.");
        target.State = SessionState.Revoked; target.RevokedAt = time.GetUtcNow();
        AuthAudit.Record(db, context, AuthAudit.SessionRevoked, current.UserId, current.TenantId, target.Id == current.Id ? "current-session" : "by-user", target.RevokedAt.Value, target.Id);
        await db.SaveChangesAsync(context.RequestAborted); return Results.NoContent();
    }

    private static async Task<IResult> RevokeOthersAsync(HttpContext context, AppDbContext db, TimeProvider time)
    {
        var current = RequireSession(context, SessionState.Active); if (current is null) return Forbidden();
        var revoked = await db.UserSessions.Where(x => x.UserId == current.UserId && x.Id != current.Id && x.State != SessionState.Revoked).ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, time.GetUtcNow()), context.RequestAborted);
        AuthAudit.Record(db, context, AuthAudit.OtherSessionsRevoked, current.UserId, current.TenantId, $"count={revoked}", time.GetUtcNow(), current.Id);
        await db.SaveChangesAsync(context.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteClosedSessionAsync(Guid id, HttpContext context, AppDbContext db)
    {
        var current = RequireSession(context, SessionState.Active); if (current is null) return Forbidden();
        if (id == current.Id) return Problem("CURRENT_SESSION_DELETE_REFUSED", "Açık olan oturum silinemez.", 409);
        var deleted = await db.UserSessions
            .Where(x => x.Id == id && x.UserId == current.UserId && x.State == SessionState.Revoked)
            .ExecuteDeleteAsync(context.RequestAborted);
        return deleted == 1 ? Results.NoContent() : Problem("SESSION_NOT_CLOSED", "Yalnız kapanmış oturumlar silinebilir.", 409);
    }

    private static async Task<IResult> DeleteClosedSessionsAsync(HttpContext context, AppDbContext db)
    {
        var current = RequireSession(context, SessionState.Active); if (current is null) return Forbidden();
        await db.UserSessions
            .Where(x => x.UserId == current.UserId && x.Id != current.Id && x.State == SessionState.Revoked)
            .ExecuteDeleteAsync(context.RequestAborted);
        return Results.NoContent();
    }

    // A wrong TOTP or recovery code counts in the session (the fifth ends it) and towards the account
    // lockout. A sign-in still at the MFA step also ends when the account gets locked.
    private static async Task<IResult> SecondFactorFailedAsync(UserSession session, ApplicationUser user, UserManager<ApplicationUser> users, AppDbContext db, HttpContext context, TimeProvider time, string method, IResult? refusal = null)
    {
        var now = time.GetUtcNow();
        var challenge = session.State == SessionState.MfaChallenge;
        await users.AccessFailedAsync(user);
        var lockedOut = await users.IsLockedOutAsync(user);
        var ended = MfaAttemptPolicy.RegisterFailure(session, now);
        AuthAudit.Record(db, context, AuthAudit.MfaFailed, session.UserId, session.TenantId, $"{method}; attempt={session.MfaFailedAttempts}", now, session.Id);
        if (lockedOut) AuthAudit.Record(db, context, AuthAudit.AccountLockedOut, session.UserId, session.TenantId, "failed-second-factor", now);
        if (ended || lockedOut && challenge) return await EndSessionAfterFailuresAsync(session, db, context, time, ended ? "too-many-second-factor-failures" : "account-locked-out");
        await db.SaveChangesAsync(CancellationToken.None);
        return refusal ?? Problem("SECOND_FACTOR_FAILED", "İkinci doğrulama kodu kabul edilmedi.", 401);
    }

    private static async Task<IResult> EndSessionAfterFailuresAsync(UserSession session, AppDbContext db, HttpContext context, TimeProvider time, string reason)
    {
        var now = time.GetUtcNow();
        MfaAttemptPolicy.Revoke(session, now);
        AuthAudit.Record(db, context, AuthAudit.SessionRevoked, session.UserId, session.TenantId, reason, now, session.Id);
        await db.SaveChangesAsync(CancellationToken.None);
        context.Response.Cookies.Delete(SessionAuthMiddleware.CookieName(context), new CookieOptions { Secure = SecureCookie(context), Path = "/" });
        return Problem("MFA_ATTEMPTS_EXCEEDED", "Doğrulama tamamlanamadı ve oturum kapatıldı; yeniden giriş yapın.", 401);
    }

    // PasswordCheck already counted the wrong password towards the lockout.
    private static async Task<IResult> PasswordRecheckFailedAsync(UserSession session, ApplicationUser? user, UserManager<ApplicationUser> users, AppDbContext db, HttpContext context, TimeProvider time)
    {
        var now = time.GetUtcNow();
        AuthAudit.Record(db, context, AuthAudit.ReauthenticationFailed, session.UserId, session.TenantId, "password", now, session.Id);
        if (user is not null && await users.IsLockedOutAsync(user)) AuthAudit.Record(db, context, AuthAudit.AccountLockedOut, session.UserId, session.TenantId, "failed-reauthentication", now);
        await db.SaveChangesAsync(CancellationToken.None);
        return Problem("REAUTHENTICATION_FAILED", "Parola doğrulanamadı.", 401);
    }

    private static string SecondFactorMethod(ReauthenticateRequest request) => string.IsNullOrWhiteSpace(request.RecoveryCode) ? "totp" : "recovery-code";

    private static async Task<bool> ValidateSecondFactorAsync(ReauthenticateRequest request, Guid userId, UserSecurity security, HttpContext context, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (!string.IsNullOrWhiteSpace(request.RecoveryCode))
        {
            var digest = hasher.Hash(NormalizeRecovery(request.RecoveryCode));
            var consumed = await db.RecoveryCodes.Where(x => x.UserId == userId && x.CodeDigest == digest && x.UsedAt == null && x.InvalidatedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.UsedAt, time.GetUtcNow()), context.RequestAborted);
            return consumed == 1;
        }
        if (string.IsNullOrWhiteSpace(request.Code) || security.ProtectedTotpSecret is null) return false;
        var secret = Convert.FromBase64String(protection.CreateProtector("MarketplaceHub.Totp.v1").Unprotect(security.ProtectedTotpSecret));
        if (!totp.TryValidate(secret, request.Code, security.LastAcceptedTimeStep, out var step)) return false;
        var updated = await db.UserSecurities.Where(x => x.UserId == userId && (x.LastAcceptedTimeStep == null || x.LastAcceptedTimeStep < step))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastAcceptedTimeStep, step).SetProperty(x => x.Version, x => x.Version + 1), context.RequestAborted);
        return updated == 1;
    }

    private static List<string> ReplaceRecoveryCodes(AppDbContext db, UserSecurity security, TokenHasher hasher, DateTimeOffset now)
    {
        var batch = Guid.NewGuid(); var codes = Enumerable.Range(0, 10).Select(_ => $"{TokenHasher.NewToken(5)[..5]}-{TokenHasher.NewToken(5)[..5]}".ToUpperInvariant()).ToList();
        db.RecoveryCodes.AddRange(codes.Select(code => new RecoveryCode { Id = Guid.NewGuid(), UserId = security.UserId, BatchId = batch, CodeDigest = hasher.Hash(NormalizeRecovery(code)), CreatedAt = now }));
        security.RecoveryBatchId = batch;
        return codes;
    }

    private static UserSession? CurrentSession(HttpContext context) => context.Items["UserSession"] as UserSession;
    private static UserSession? RequireSession(HttpContext context, params SessionState[] allowed) => CurrentSession(context) is { } session && allowed.Contains(session.State) ? session : null;
    private static UserSession? RequireRecentActive(HttpContext context, TimeProvider time) => RequireSession(context, SessionState.Active) is { ReauthenticatedAt: { } verified } session && verified >= time.GetUtcNow().AddMinutes(-10) ? session : null;
    private static IResult Forbidden() => Problem("SESSION_STATE_FORBIDDEN", "Oturumun şu anki adımı bu işleme izin vermiyor.", 403);
    private static IResult Problem(string code, string message, int status) => ApiProblems.Of(null, code, message, status);
    private static IResult InvalidCredentials() => Problem("INVALID_CREDENTIALS", "E-posta veya parola hatalı.", 401);
    private static IResult InvalidBody(string message) => Problem("INVALID_REQUEST", message, 400);
    private static IQueryable<TenantMembership> ActiveTenantMemberships(AppDbContext db, Guid userId) =>
        db.TenantMemberships.AsNoTracking()
            .Where(x => x.UserId == userId && x.Status == RecordStatus.Active)
            .Join(db.Tenants.AsNoTracking().Where(x => x.Status == RecordStatus.Active), membership => membership.TenantId, tenant => tenant.Id, (membership, _) => membership);
    private static Task<bool> HasActiveTenantMembershipAsync(AppDbContext db, Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        ActiveTenantMemberships(db, userId).AnyAsync(x => x.TenantId == tenantId, cancellationToken);
    private static string NormalizeRecovery(string code) => code.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
    private static void SetSessionCookie(HttpResponse response, string token, DateTimeOffset expires) => response.Cookies.Append(SessionAuthMiddleware.CookieName(response.HttpContext), token, new CookieOptions { HttpOnly = true, Secure = SecureCookie(response.HttpContext), SameSite = SameSiteMode.Lax, Path = "/", Expires = expires });
    private static bool SecureCookie(HttpContext context) => !string.Equals(context.RequestServices.GetRequiredService<IConfiguration>()["MARKETPLACEHUB_ENVIRONMENT"], "PILOT_LOCAL", StringComparison.OrdinalIgnoreCase) || context.Request.IsHttps;
    private static string Base32(byte[] data) { const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; var output = new StringBuilder(); var buffer = 0; var bits = 0; foreach (var b in data) { buffer = (buffer << 8) | b; bits += 8; while (bits >= 5) { output.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; } } if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]); return output.ToString(); }
    private static string SessionStateWire(SessionState state) => state switch { SessionState.PasswordChangeRequired => "PASSWORD_CHANGE_REQUIRED", SessionState.MfaChallenge => "MFA_CHALLENGE", SessionState.Active => "ACTIVE", _ => "REVOKED" };
    private static readonly HashSet<string> WeakPasswords = new(StringComparer.Ordinal) { "Password123456!", "MarketplaceHub1!" };

    public sealed record LoginRequest(string Email, string Password, Guid? TenantId = null);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record CodeRequest(string Code, string? RecoveryCode = null);
    public sealed record ReauthenticateRequest(string Password, string? Code = null, string? RecoveryCode = null);
}
