using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Api.Security;

// Sign-in and session events, kept in ops.audit_logs and written to the structured log with the user,
// the source address and the correlation id. Passwords, codes, tokens and the e-mail typed into a
// failed sign-in are never recorded. The caller saves the context.
public static class AuthAudit
{
    public const string LoginSucceeded = "AUTH_LOGIN_SUCCEEDED";
    public const string LoginFailed = "AUTH_LOGIN_FAILED";
    public const string AccountLockedOut = "AUTH_ACCOUNT_LOCKED_OUT";
    public const string MfaSucceeded = "AUTH_MFA_SUCCEEDED";
    public const string MfaFailed = "AUTH_MFA_FAILED";
    public const string MfaEnabled = "AUTH_MFA_ENABLED";
    public const string MfaDisabled = "AUTH_MFA_DISABLED";
    public const string RecoveryCodesRegenerated = "AUTH_RECOVERY_CODES_REGENERATED";
    public const string ReauthenticationFailed = "AUTH_REAUTHENTICATION_FAILED";
    public const string PasswordChanged = "AUTH_PASSWORD_CHANGED";
    public const string PasswordChangeFailed = "AUTH_PASSWORD_CHANGE_FAILED";
    public const string SessionRevoked = "AUTH_SESSION_REVOKED";
    public const string OtherSessionsRevoked = "AUTH_OTHER_SESSIONS_REVOKED";
    private const int MaximumReasonLength = 512;

    public static AuditLog Record(AppDbContext db, HttpContext http, string action, Guid? userId, Guid? tenantId, string reason, DateTimeOffset now, Guid? sessionId = null)
    {
        var sourceAddress = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var detail = $"{reason}; ip={sourceAddress}";
        var entry = new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = action,
            TargetType = sessionId is null ? "User" : "UserSession",
            TargetId = (sessionId ?? userId)?.ToString("D"),
            Reason = detail.Length > MaximumReasonLength ? detail[..MaximumReasonLength] : detail,
            CorrelationId = http.TraceIdentifier,
            CreatedAt = now
        };
        db.AuditLogs.Add(entry);

        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MarketplaceHub.Auth");
        var level = action is LoginSucceeded or MfaSucceeded or MfaEnabled or PasswordChanged or SessionRevoked or OtherSessionsRevoked or RecoveryCodesRegenerated
            ? LogLevel.Information
            : LogLevel.Warning;
        logger?.Log(level, "Auth event {AuthAction} for user {UserId}, session {SessionId}, from {SourceIp}: {Reason} (correlation {CorrelationId}).",
            action, userId, sessionId, sourceAddress, reason, http.TraceIdentifier);
        return entry;
    }
}
