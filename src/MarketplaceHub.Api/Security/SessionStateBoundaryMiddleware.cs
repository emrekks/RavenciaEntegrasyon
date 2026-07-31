using MarketplaceHub.Domain;

namespace MarketplaceHub.Api.Security;

public sealed class SessionStateBoundaryMiddleware(RequestDelegate next)
{
    private static readonly HashSet<(string Method, string Path)> PasswordChangeAllowlist = new()
    {
        ("GET", "/api/v1/auth/csrf"), ("GET", "/api/v1/auth/me"),
        ("POST", "/api/v1/auth/change-password"), ("POST", "/api/v1/auth/logout")
    };
    private static readonly HashSet<(string Method, string Path)> MfaAllowlist = new()
    {
        ("GET", "/api/v1/auth/csrf"), ("GET", "/api/v1/auth/me"),
        ("POST", "/api/v1/auth/mfa/challenge"), ("POST", "/api/v1/auth/logout")
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Items["UserSession"] is UserSession session)
        {
            var key = (context.Request.Method.ToUpperInvariant(), context.Request.Path.Value ?? string.Empty);
            var allowed = session.State switch
            {
                SessionState.PasswordChangeRequired => PasswordChangeAllowlist.Contains(key),
                SessionState.MfaChallenge => MfaAllowlist.Contains(key),
                SessionState.Revoked => false,
                _ => true
            };
            if (!allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { type = "https://marketplacehub.invalid/problems/session-state", title = "Session state does not permit this operation", status = 403 }, context.RequestAborted);
                return;
            }
        }
        await next(context);
    }
}
