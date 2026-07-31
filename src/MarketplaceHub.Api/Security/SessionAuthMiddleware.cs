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

    public async Task InvokeAsync(HttpContext context, AppDbContext db, TokenHasher hasher, TimeProvider timeProvider)
    {
        if (context.Request.Cookies.TryGetValue(CookieName(context), out var token))
        {
            var hash = hasher.Hash(token);
            var session = await db.UserSessions.SingleOrDefaultAsync(x => x.TokenHash == hash, context.RequestAborted);
            var now = timeProvider.GetUtcNow();
            if (session is not null && session.State != SessionState.Revoked && session.ExpiresAt > now && session.AbsoluteExpiresAt > now)
            {
                var user = await db.Users.SingleOrDefaultAsync(x => x.Id == session.UserId, context.RequestAborted);
                if (user is not null && user.SessionVersion == session.SessionVersion && user.Status == "ACTIVE")
                {
                    var claims = new List<Claim>
                    {
                        new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.UserName ?? user.Id.ToString()),
                        new("session_id", session.Id.ToString()), new("session_state", session.State.ToString())
                    };
                    if (session.State == SessionState.Active && session.TenantId is Guid tenantId)
                    {
                        var membership = await db.TenantMemberships.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == user.Id && x.Status == RecordStatus.Active, context.RequestAborted);
                        if (membership is not null) { claims.Add(new("tenant_id", tenantId.ToString())); claims.Add(new(ClaimTypes.Role, membership.Role.ToString().ToUpperInvariant())); }
                    }
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ServerSession"));
                    context.Items["UserSession"] = session;
                    if (session.LastSeenAt is null || session.LastSeenAt < now.AddMinutes(-5)) { session.LastSeenAt = now; session.ExpiresAt = Min(now.AddMinutes(30), session.AbsoluteExpiresAt); await db.SaveChangesAsync(context.RequestAborted); }
                }
            }
        }
        await next(context);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    public static string CookieName(HttpContext context) =>
        string.Equals(context.RequestServices.GetRequiredService<IConfiguration>()["MARKETPLACEHUB_ENVIRONMENT"], "PILOT_LOCAL", StringComparison.OrdinalIgnoreCase) && !context.Request.IsHttps
            ? PilotLocalCookieName
            : ProductionCookieName;
}
