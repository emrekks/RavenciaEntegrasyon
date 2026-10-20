using System.Security.Claims;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Security;

public sealed class SessionAuthMiddleware(RequestDelegate next)
{
    public const string ProductionCookieName = "__Host-MarketplaceHub";
    public const string PilotLocalCookieName = "MarketplaceHub-Session-PILOT-LOCAL";
    public const string MfaClaim = "mfa";

    public async Task InvokeAsync(HttpContext context, AppDbContext db, TokenHasher hasher, TimeProvider timeProvider)
    {
        if (context.Request.Cookies.TryGetValue(CookieName(context), out var token))
        {
            // Session, user, membership and second-factor state in one round trip (three before).
            var found = await Lookup(db, hasher.Hash(token)).SingleOrDefaultAsync(context.RequestAborted);
            var now = timeProvider.GetUtcNow();
            var session = found?.Session;
            if (found is not null && session is not null && session.State != SessionState.Revoked && session.ExpiresAt > now && session.AbsoluteExpiresAt > now
                && found.UserSessionVersion == session.SessionVersion && found.UserStatus == "ACTIVE")
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, session.UserId.ToString()), new(ClaimTypes.Name, found.UserName ?? session.UserId.ToString()),
                    new("session_id", session.Id.ToString()), new("session_state", session.State.ToString()),
                    new(MfaClaim, found.TotpState == TotpState.Enabled ? "enabled" : "disabled")
                };
                if (session.State == SessionState.Active && session.TenantId is Guid tenantId && found.Role is { } role)
                {
                    claims.Add(new("tenant_id", tenantId.ToString()));
                    claims.Add(new(ClaimTypes.Role, role.ToString().ToUpperInvariant()));
                }
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ServerSession"));
                context.Items["UserSession"] = session;
                if (session.LastSeenAt is null || session.LastSeenAt < now.AddMinutes(-5)) { session.LastSeenAt = now; session.ExpiresAt = Min(now.AddMinutes(30), session.AbsoluteExpiresAt); await db.SaveChangesAsync(context.RequestAborted); }
            }
        }
        await next(context);
    }

    // The session row is tracked: endpoints change it (state, re-authentication time) and save it.
    internal static IQueryable<SessionLookup> Lookup(AppDbContext db, string tokenHash) =>
        from session in db.UserSessions
        where session.TokenHash == tokenHash
        join user in db.Users on session.UserId equals user.Id
        select new SessionLookup
        {
            Session = session,
            UserSessionVersion = user.SessionVersion,
            UserStatus = user.Status,
            UserName = user.UserName,
            Role = db.TenantMemberships
                .Where(membership => membership.TenantId == session.TenantId && membership.UserId == session.UserId && membership.Status == RecordStatus.Active
                    && db.Tenants.Any(tenant => tenant.Id == membership.TenantId && tenant.Status == RecordStatus.Active))
                .Select(membership => (MembershipRole?)membership.Role)
                .FirstOrDefault(),
            TotpState = db.UserSecurities.Where(security => security.UserId == session.UserId).Select(security => (TotpState?)security.TotpState).FirstOrDefault()
        };

    internal sealed class SessionLookup
    {
        public UserSession? Session { get; init; }
        public long UserSessionVersion { get; init; }
        public string? UserStatus { get; init; }
        public string? UserName { get; init; }
        public MembershipRole? Role { get; init; }
        public TotpState? TotpState { get; init; }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    public static string CookieName(HttpContext context) =>
        string.Equals(context.RequestServices.GetRequiredService<IConfiguration>()["MARKETPLACEHUB_ENVIRONMENT"], "PILOT_LOCAL", StringComparison.OrdinalIgnoreCase) && !context.Request.IsHttps
            ? PilotLocalCookieName
            : ProductionCookieName;
}
