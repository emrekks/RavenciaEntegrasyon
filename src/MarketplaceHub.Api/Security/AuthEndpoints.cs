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
        return group;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, TokenHasher hasher, TimeProvider time)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || user.Status != "ACTIVE" || await users.IsLockedOutAsync(user)) return Results.Problem(statusCode: 401, title: "Invalid credentials");
        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return Results.Problem(statusCode: 401, title: "Invalid credentials");
        }
        await users.ResetAccessFailedCountAsync(user);
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, context.RequestAborted);
        var state = user.ForcePasswordChange ? SessionState.PasswordChangeRequired : security.TotpState == TotpState.Enabled ? SessionState.MfaChallenge : SessionState.Active;
        var membership = state == SessionState.Active ? await db.TenantMemberships.SingleAsync(x => x.UserId == user.Id && x.Status == RecordStatus.Active, context.RequestAborted) : null;
        var raw = TokenHasher.NewToken(); var now = time.GetUtcNow();
        db.UserSessions.Add(new UserSession { Id = Guid.NewGuid(), UserId = user.Id, TenantId = membership?.TenantId, State = state, TokenHash = hasher.Hash(raw), SessionVersion = user.SessionVersion, IssuedAt = now, LastSeenAt = now, ExpiresAt = now.AddMinutes(30), AbsoluteExpiresAt = now.AddHours(12) });
        user.LastLoginAt = now; await db.SaveChangesAsync(context.RequestAborted);
        SetSessionCookie(context.Response, raw, now.AddHours(12));
        return Results.Ok(new { state = SessionStateWire(state) });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, AppDbContext db, TimeProvider time)
    {
        if (CurrentSession(context) is { } session) { session.State = SessionState.Revoked; session.RevokedAt = time.GetUtcNow(); await db.SaveChangesAsync(context.RequestAborted); }
        context.Response.Cookies.Delete(SessionAuthMiddleware.CookieName, new CookieOptions { Secure = SecureCookie(context), Path = "/" });
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, TokenHasher hasher, TimeProvider time)
    {
        var session = RequireSession(context, SessionState.PasswordChangeRequired, SessionState.Active); if (session is null) return Forbidden();
        if (request.NewPassword.Length is < 15 or > 64 || WeakPasswords.Contains(request.NewPassword)) return Results.Problem(statusCode: 400, title: "Password does not meet policy");
        var user = await users.FindByIdAsync(session.UserId.ToString()); if (user is null) return Forbidden();
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded) return Results.ValidationProblem(result.Errors.ToDictionary(x => x.Code, x => new[] { x.Description }));
        user.ForcePasswordChange = false; user.SessionVersion++;
        await db.UserSessions.Where(x => x.UserId == user.Id && x.Id != session.Id && x.State != SessionState.Revoked).ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, time.GetUtcNow()), context.RequestAborted);
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, context.RequestAborted);
        session.SessionVersion = user.SessionVersion; session.State = security.TotpState == TotpState.Enabled ? SessionState.MfaChallenge : SessionState.Active;
        session.TenantId = session.State == SessionState.Active ? (await db.TenantMemberships.SingleAsync(x => x.UserId == user.Id, context.RequestAborted)).TenantId : null;
        var raw = TokenHasher.NewToken(); session.TokenHash = hasher.Hash(raw); await db.SaveChangesAsync(context.RequestAborted); SetSessionCookie(context.Response, raw, session.AbsoluteExpiresAt);
        return Results.Ok(new { state = SessionStateWire(session.State) });
    }

    private static async Task<IResult> MeAsync(HttpContext context, AppDbContext db)
    {
        var session = CurrentSession(context); if (session is null) return Results.Unauthorized();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == session.UserId, context.RequestAborted);
        return Results.Ok(new { user.Id, user.Email, user.DisplayName, state = SessionStateWire(session.State), tenantId = session.State == SessionState.Active ? session.TenantId : null });
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
        security.TotpState = TotpState.Pending; security.ProtectedTotpSecret = protection.CreateProtector("MarketplaceHub.Totp.v1").Protect(Convert.ToBase64String(secret)); security.EnrollmentExpiresAt = expires; security.LastAcceptedTimeStep = null;
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
        if (security.TotpState != TotpState.Pending || security.EnrollmentExpiresAt <= time.GetUtcNow() || security.ProtectedTotpSecret is null) return Results.Problem(statusCode: 409, title: "MFA enrollment is not pending");
        var secret = Convert.FromBase64String(protection.CreateProtector("MarketplaceHub.Totp.v1").Unprotect(security.ProtectedTotpSecret));
        if (!totp.TryValidate(secret, request.Code, null, out var step)) return Results.Problem(statusCode: 400, title: "Invalid verification code");
        await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
        security.TotpState = TotpState.Enabled; security.EnrollmentExpiresAt = null; security.LastAcceptedTimeStep = step; security.Version++;
        var codes = ReplaceRecoveryCodes(db, security, hasher, time.GetUtcNow()); await db.SaveChangesAsync(context.RequestAborted); await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> MfaChallengeAsync(CodeRequest request, HttpContext context, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher)
    {
        var session = RequireSession(context, SessionState.MfaChallenge); if (session is null) return Forbidden();
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == session.UserId, context.RequestAborted);
        var accepted = false;
        if (request.RecoveryCode is { Length: > 0 })
        {
            var digest = hasher.Hash(NormalizeRecovery(request.RecoveryCode));
            var consumed = await db.RecoveryCodes.Where(x => x.UserId == session.UserId && x.CodeDigest == digest && x.UsedAt == null && x.InvalidatedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.UsedAt, DateTimeOffset.UtcNow), context.RequestAborted);
            accepted = consumed == 1;
        }
        else if (security.TotpState == TotpState.Enabled && security.ProtectedTotpSecret is not null)
        {
            var secret = Convert.FromBase64String(protection.CreateProtector("MarketplaceHub.Totp.v1").Unprotect(security.ProtectedTotpSecret));
            if (totp.TryValidate(secret, request.Code, security.LastAcceptedTimeStep, out var step))
            {
                var updated = await db.UserSecurities.Where(x => x.UserId == session.UserId && (x.LastAcceptedTimeStep == null || x.LastAcceptedTimeStep < step))
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastAcceptedTimeStep, step).SetProperty(x => x.Version, x => x.Version + 1), context.RequestAborted);
                accepted = updated == 1;
            }
        }
        if (!accepted) return Results.Problem(statusCode: 400, title: "Invalid MFA challenge");
        session.State = SessionState.Active; session.TenantId = (await db.TenantMemberships.SingleAsync(x => x.UserId == session.UserId, context.RequestAborted)).TenantId;
        await db.SaveChangesAsync(context.RequestAborted); return Results.Ok(new { state = SessionStateWire(session.State) });
    }

    private static async Task<IResult> MfaDisableAsync(PasswordRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, TimeProvider time)
    {
        var session = RequireRecentActive(context, time); if (session is null) return Forbidden();
        var user = await users.FindByIdAsync(session.UserId.ToString()); if (user is null || !await users.CheckPasswordAsync(user, request.Password)) return Results.Problem(statusCode: 401, title: "Reauthentication failed");
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, context.RequestAborted);
        security.TotpState = TotpState.Disabled; security.ProtectedTotpSecret = null; security.LastAcceptedTimeStep = null; security.RecoveryBatchId = null;
        await db.RecoveryCodes.Where(x => x.UserId == user.Id && x.InvalidatedAt == null).ExecuteUpdateAsync(x => x.SetProperty(c => c.InvalidatedAt, time.GetUtcNow()), context.RequestAborted);
        user.SessionVersion++; session.SessionVersion = user.SessionVersion;
        await db.UserSessions.Where(x => x.UserId == user.Id && x.Id != session.Id && x.State != SessionState.Revoked).ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, time.GetUtcNow()), context.RequestAborted);
        await db.SaveChangesAsync(context.RequestAborted); return Results.NoContent();
    }

    private static async Task<IResult> RecoveryRegenerateAsync(PasswordRequest request, HttpContext context, UserManager<ApplicationUser> users, AppDbContext db, TokenHasher hasher, TimeProvider time)
    {
        var session = RequireRecentActive(context, time); if (session is null) return Forbidden();
        var user = await users.FindByIdAsync(session.UserId.ToString()); if (user is null || !await users.CheckPasswordAsync(user, request.Password)) return Results.Problem(statusCode: 401, title: "Reauthentication failed");
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, context.RequestAborted); if (security.TotpState != TotpState.Enabled) return Results.Problem(statusCode: 409, title: "MFA is not enabled");
        await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
        await db.RecoveryCodes.Where(x => x.UserId == user.Id && x.InvalidatedAt == null).ExecuteUpdateAsync(x => x.SetProperty(c => c.InvalidatedAt, time.GetUtcNow()), context.RequestAborted);
        var codes = ReplaceRecoveryCodes(db, security, hasher, time.GetUtcNow()); await db.SaveChangesAsync(context.RequestAborted); await transaction.CommitAsync(context.RequestAborted);
        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> RevokeSessionAsync(Guid id, HttpContext context, AppDbContext db, TimeProvider time)
    {
        var current = RequireSession(context, SessionState.Active); if (current is null) return Forbidden();
        var target = await db.UserSessions.SingleOrDefaultAsync(x => x.Id == id && x.UserId == current.UserId, context.RequestAborted); if (target is null) return Results.NotFound();
        target.State = SessionState.Revoked; target.RevokedAt = time.GetUtcNow(); await db.SaveChangesAsync(context.RequestAborted); return Results.NoContent();
    }

    private static async Task<IResult> RevokeOthersAsync(HttpContext context, AppDbContext db, TimeProvider time)
    {
        var current = RequireSession(context, SessionState.Active); if (current is null) return Forbidden();
        await db.UserSessions.Where(x => x.UserId == current.UserId && x.Id != current.Id && x.State != SessionState.Revoked).ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, time.GetUtcNow()), context.RequestAborted);
        return Results.NoContent();
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
    private static UserSession? RequireRecentActive(HttpContext context, TimeProvider time) => RequireSession(context, SessionState.Active) is { } session && session.IssuedAt >= time.GetUtcNow().AddMinutes(-10) ? session : null;
    private static IResult Forbidden() => Results.Problem(statusCode: 403, title: "Session state does not permit this operation");
    private static string NormalizeRecovery(string code) => code.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
    private static void SetSessionCookie(HttpResponse response, string token, DateTimeOffset expires) => response.Cookies.Append(SessionAuthMiddleware.CookieName, token, new CookieOptions { HttpOnly = true, Secure = SecureCookie(response.HttpContext), SameSite = SameSiteMode.Lax, Path = "/", Expires = expires });
    private static bool SecureCookie(HttpContext context) => !string.Equals(context.RequestServices.GetRequiredService<IConfiguration>()["MARKETPLACEHUB_ENVIRONMENT"], "PILOT_LOCAL", StringComparison.OrdinalIgnoreCase) || context.Request.IsHttps;
    private static string Base32(byte[] data) { const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; var output = new StringBuilder(); var buffer = 0; var bits = 0; foreach (var b in data) { buffer = (buffer << 8) | b; bits += 8; while (bits >= 5) { output.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; } } if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]); return output.ToString(); }
    private static string SessionStateWire(SessionState state) => state switch { SessionState.PasswordChangeRequired => "PASSWORD_CHANGE_REQUIRED", SessionState.MfaChallenge => "MFA_CHALLENGE", SessionState.Active => "ACTIVE", _ => "REVOKED" };
    private static readonly HashSet<string> WeakPasswords = new(StringComparer.Ordinal) { "Password123456!", "MarketplaceHub1!" };

    public sealed record LoginRequest(string Email, string Password);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record CodeRequest(string Code, string? RecoveryCode = null);
    public sealed record PasswordRequest(string Password);
}
